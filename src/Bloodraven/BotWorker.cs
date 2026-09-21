namespace Bloodraven;

public sealed class BotWorker(TelegramClient telegram, CodexRunner codex, SessionStore sessions,
    Journal journal, AppOptions options, ILogger<BotWorker> logger) : BackgroundService
{
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly ApprovalBroker approvals = new(journal);
    readonly AttachmentStore attachments = new(options, telegram);
    readonly object taskGate = new();
    CancellationTokenSource? activeTask;
    DateTimeOffset nextSend;
    DateTimeOffset? lastPoll;
    public static string Version => typeof(BotWorker).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    async Task<long> SendPacedAsync(long chatId, FormattedMessage message, CancellationToken token,
        InlineButton[][]? buttons = null, long? edit = null, string? document = null)
    {
        await sendGate.WaitAsync(token);
        try
        {
            var delay = nextSend - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            long id = 0;
            if (document is null) id = await telegram.SendAsync(chatId, message, token, buttons, edit);
            else await telegram.SendDocumentAsync(chatId, document, token);
            nextSend = DateTimeOffset.UtcNow.AddMilliseconds(1100);
            return id;
        }
        catch (TelegramException ex) { nextSend = DateTimeOffset.UtcNow.AddSeconds(ex.RetryAfter); throw; }
        finally { sendGate.Release(); }
    }

    async Task<string> RunWithProgressAsync(Job job, CancellationToken token)
    {
        var (prompt, images) = await attachments.PrepareAsync(job, token);
        var progress = new TaskProgress(options.TelegramBotToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reporting = progress.RunAsync(TimeSpan.FromSeconds(options.ProgressIntervalSeconds), async (text, ct) =>
        {
            var state = await journal.SnapshotAsync(ct);
            var messageId = state.Jobs.FirstOrDefault(j => j.Id == job.Id)?.ProgressMessageId;
            if (state.Replies.Count != 0 || messageId is null or <= 0) return;
            try { await SendPacedAsync(job.ChatId, TelegramFormatter.Format($"{job.Conversation}\n{text}")[0], ct, edit: messageId); }
            catch (TelegramException ex)
            {
                logger.LogWarning("Progress edit unavailable (status {Status}); skipping update.", ex.Status);
                await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfter), ct);
            }
        }, lifetime.Token);
        try
        {
            return await codex.RunAsync(prompt, token, progress.Observe, job.Conversation, images,
                job.ApprovalRequired ? (method, details, ct) => approvals.RequestAsync(job.ChatId, job.Conversation, method, details, ct) : null);
        }
        finally
        {
            lifetime.Cancel();
            try { await reporting; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.StateDirectory);
        using var lease = new FileStream(Path.Combine(options.StateDirectory, "instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        await Preflight.CheckAsync(options, stoppingToken);
        await journal.InitializeAsync(stoppingToken);
        attachments.CleanupStale(await journal.SnapshotAsync(stoppingToken));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] tasks = [PollAsync(lifetime.Token), ConsumeAsync(lifetime.Token), DeliverAsync(lifetime.Token), ScheduleAsync(lifetime.Token)];
        try { await Task.WhenAny(tasks); lifetime.Cancel(); await Task.WhenAll(tasks); }
        finally { lifetime.Cancel(); File.Delete(Path.Combine(options.StateDirectory, "ready")); }
    }

    async Task<string> StatusAsync(JournalData state, bool health, CancellationToken token)
    {
        string? session;
        try { session = await sessions.GetAsync(token, state.ActiveConversation, state.Approvals ?? options.ApprovalDefault); }
        catch (InvalidDataException) { session = "invalid — use /new"; }
        var running = state.Jobs.FirstOrDefault(j => j.Running);
        var text = $"Bloodraven {Version}\nStatus: {(codex.IsRunning ? "working" : "idle")}\nConversation: {state.ActiveConversation}\nSession: {session ?? "none"}\nRunning: {(running is null ? "none" : $"{running.Id} ({running.Conversation})")}\nWaiting: {state.Jobs.Count(j => !j.Running)}\nPending replies: {state.Replies.Count}\nApproval requests: {approvals.Count}";
        if (health)
        {
            string availability;
            using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
            check.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Preflight.CheckAsync(options, check.Token); availability = "repository, executable and login checks passed now"; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { availability = "health check timed out after 5 seconds"; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { availability = "check failed; inspect the local Codex login and repository permissions"; }
            text += $"\nRepository: {options.WorkingDirectory}\nCodex: {availability}\nLast successful poll: {lastPoll:O}\nApprovals: {((state.Approvals ?? options.ApprovalDefault) ? "on" : "off")}\nSchedules: {state.Schedules.Count(s => !s.Paused)} enabled\nRecent failures:";
            var failures = state.Outcomes.Where(o => o.Status != "Completed").TakeLast(5).ToArray();
            text += failures.Length == 0 ? " none" : "\n" + string.Join('\n', failures.Select(o => $"{o.Finished:yyyy-MM-dd HH:mm} UTC · {o.Conversation} · {o.Status}"));
        }
        return text;
    }

    async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var state = await journal.SnapshotAsync(token);
            if (state.Replies.Count >= 100) { await Task.Delay(1000, token); continue; }
            IReadOnlyList<TelegramUpdate> updates;
            try { updates = await telegram.GetUpdatesAsync(state.Offset, token); }
            catch (TelegramException ex)
            {
                logger.LogWarning("Telegram polling unavailable (status {Status}); retrying.", ex.Status);
                await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfter), token); continue;
            }
            lastPoll = DateTimeOffset.UtcNow;
            await AtomicFile.WriteAsync(Path.Combine(options.StateDirectory, "ready"), lastPoll.Value.ToString("O"), token);
            foreach (var update in updates)
            {
                if (update.UpdateId < state.Offset) continue;
                if (update.Callback is { } callback)
                {
                    await HandleCallbackAsync(update.UpdateId, callback, token);
                    state = await journal.SnapshotAsync(token);
                    continue;
                }
                var message = update.Message;
                var allowed = message is not null && options.Accepts(message);
                var text = (message?.Text ?? message?.Caption)?.Trim() ?? "";
                Attachment[] files = [];
                string? error = null;
                if (allowed)
                {
                    try { files = AttachmentStore.Read(message!); }
                    catch (ArgumentException ex) { error = ex.Message; }
                    if (files.Length > 0 && text.Length == 0) text = "Please inspect the attached file.";
                    allowed = text.Length > 0 || error is not null;
                }
                var command = Command(text);
                var status = allowed && command is "/status" or "/health" ? await StatusAsync(state, command == "/health", token) : null;
                await journal.ChangeAsync(d =>
                {
                    d.Offset = update.UpdateId + 1;
                    if (!allowed) return;
                    var chat = message!.Chat.Id;
                    if (error is not null) { Journal.AddReply(d, chat, error); return; }
                    if (text.Length > 16384) { Journal.AddReply(d, chat, "Message too large (maximum 16,384 characters)."); return; }
                    if (command == "/cancel") return;
                    if (status is not null) { Journal.AddReply(d, chat, status); return; }
                    if (BotCommands.Apply(d, chat, text, options.ApprovalDefault)) return;
                    if (text.StartsWith('/') && command is not ("/new" or "/file"))
                    { Journal.AddReply(d, chat, "Unknown command. Use /help for Bloodraven commands, or send a normal message to Codex."); return; }
                    if (d.Jobs.Count >= 20) Journal.AddReply(d, chat, "Queue full. Try again after current work finishes.");
                    else
                    {
                        d.Jobs.Add(new Job(update.UpdateId, chat, text, Conversation: d.ActiveConversation,
                            Attachments: files, ApprovalRequired: d.Approvals ?? options.ApprovalDefault));
                        Journal.AddReply(d, chat, $"Queued · {d.ActiveConversation}.");
                    }
                }, token);
                if (allowed && error is null && command == "/cancel")
                {
                    bool cancelled;
                    lock (taskGate) { cancelled = activeTask is not null; activeTask?.Cancel(); }
                    await journal.ChangeAsync(d => Journal.AddReply(d, message!.Chat.Id,
                        cancelled ? "Cancellation requested; queued tasks are unaffected." : "No task is running."), token);
                }
                state = await journal.SnapshotAsync(token);
                if (state.Replies.Count >= 100) break;
            }
        }
    }

    async Task HandleCallbackAsync(long updateId, TelegramCallback callback, CancellationToken token)
    {
        var message = callback.Message;
        var allowed = message is not null && options.Accepts(message with { From = callback.From });
        var answer = "Unavailable.";
        await journal.ChangeAsync(d =>
        {
            d.Offset = updateId + 1;
            if (!allowed) return;
            var parts = (callback.Data ?? "").Split(':', 2);
            if (parts.Length != 2) return;
            if (parts[0] is "approve" or "decline")
            {
                answer = approvals.Decide(parts[1], message!.Chat.Id, parts[0] == "approve")
                    ? (parts[0] == "approve" ? "Approved once." : "Declined.") : "This approval has expired or was already answered.";
                d.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), message!.Chat.Id, answer, EditMessageId: message.MessageId));
                return;
            }
            var command = parts[0] switch {
                "conv" => "/conversation ", "front" => "/front ", "remove" => "/remove ",
                "schedpause" => "/schedule pause ", "schedresume" => "/schedule resume ", "scheddelete" => "/schedule delete ", _ => null
            };
            if (command is not null) { BotCommands.Apply(d, message!.Chat.Id, command + parts[1], options.ApprovalDefault); answer = "Done."; }
        }, token);
        try { await telegram.AnswerCallbackAsync(callback.Id, answer, token); }
        catch (TelegramException ex) { logger.LogWarning("Callback acknowledgement unavailable ({Status}).", ex.Status); }
    }

    async Task ScheduleAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var state = await journal.SnapshotAsync(token);
            var now = DateTimeOffset.UtcNow;
            if (state.Replies.Count < 100 && state.Jobs.Count < 20 && state.Schedules.Any(s => !s.Paused && s.NextRun <= now))
                await journal.ChangeAsync(d => Scheduling.EnqueueDue(d, now, options.ApprovalDefault), token);
            await Task.Delay(1000, token);
        }
    }

    async Task ConsumeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var state = await journal.SnapshotAsync(token);
            if (state.Jobs.Count == 0 || state.Replies.Count >= 100) { await Task.Delay(250, token); continue; }
            Job? job = null;
            await journal.ChangeAsync(d =>
            {
                var index = d.Jobs.FindIndex(j => !j.Running);
                if (index < 0) return;
                job = d.Jobs[index] with { Running = true };
                d.Jobs[index] = job;
                d.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), job.ChatId, $"Working… · {job.Conversation}", ProgressJobId: job.Id));
            }, token);
            if (job is null) continue;
            using var taskLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            taskLifetime.CancelAfter(TimeSpan.FromSeconds(options.TaskTimeoutSeconds));
            lock (taskGate) activeTask = taskLifetime;
            string result;
            string outcome = "Completed";
            string? document = null;
            try
            {
                switch (Command(job.Text))
                {
                    case "/new":
                        await sessions.ClearAsync(taskLifetime.Token, job.Conversation);
                        result = $"Started a fresh Codex conversation: {job.Conversation}."; break;
                    case "/file":
                        document = await attachments.ExportAsync(job.Text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "", taskLifetime.Token);
                        result = $"File: {Path.GetFileName(document)}"; break;
                    default: result = await RunWithProgressAsync(job, taskLifetime.Token); break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { outcome = "Cancelled or timed out"; result = "Task cancelled or timed out. It may have made changes; review them before retrying."; }
            catch (ArgumentException ex) { outcome = "Failed"; result = ex.Message; }
            catch (Exception ex) when (ex is not FatalRunnerException)
            {
                logger.LogWarning("Task {Id} failed ({ErrorType}).", job.Id, ex.GetType().Name);
                outcome = "Failed";
                result = "Task failed. Check Codex login, repository permissions, attachments and saved session locally; /new resets a broken session. Review changes before retrying.";
            }
            finally { lock (taskGate) activeTask = null; }
            await journal.ChangeAsync(d =>
            {
                var current = d.Jobs.FirstOrDefault(j => j.Id == job.Id);
                if (current?.ProgressMessageId is > 0)
                    d.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), job.ChatId, $"{outcome} · {job.Conversation}", EditMessageId: current.ProgressMessageId));
                d.Jobs.RemoveAll(j => j.Id == job.Id);
                d.Outcomes.Add(new TaskOutcome(job.Id, job.Conversation, outcome, DateTimeOffset.UtcNow));
                if (d.Outcomes.Count > 50) d.Outcomes.RemoveRange(0, d.Outcomes.Count - 50);
                d.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), job.ChatId,
                    job.Conversation == "default" ? result : $"Conversation: {job.Conversation}\n\n{result}", DocumentPath: document));
            }, token);
            attachments.Cleanup(job.Id);
        }
    }

    async Task DeliverAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var reply = (await journal.SnapshotAsync(token)).Replies.FirstOrDefault();
            if (reply is null) { await Task.Delay(250, token); continue; }
            IReadOnlyList<FormattedMessage> parts = reply.Plain ? new[] { new FormattedMessage(System.Net.WebUtility.HtmlEncode(reply.Text), reply.Text) } : TelegramFormatter.Format(reply.Text);
            try
            {
                var id = await SendPacedAsync(reply.ChatId, parts[reply.Part], token,
                    reply.Part + 1 == parts.Count ? reply.Buttons : null, reply.EditMessageId, reply.DocumentPath);
                await journal.ChangeAsync(d =>
                {
                    var index = d.Replies.FindIndex(r => r.Id == reply.Id);
                    if (index < 0) return;
                    if (reply.Part + 1 == parts.Count || reply.DocumentPath is not null) d.Replies.RemoveAt(index);
                    else d.Replies[index] = reply with { Part = reply.Part + 1 };
                    if (reply.ProgressJobId is { } jobId && id > 0)
                    {
                        var jobIndex = d.Jobs.FindIndex(j => j.Id == jobId);
                        if (jobIndex >= 0) d.Jobs[jobIndex] = d.Jobs[jobIndex] with { ProgressMessageId = id };
                        else
                        {
                            var outcome = d.Outcomes.LastOrDefault(o => o.Id == jobId);
                            d.Replies.Insert(0, new Reply(Guid.NewGuid().ToString("N"), reply.ChatId,
                                $"{outcome?.Status ?? "Finished"} · {outcome?.Conversation ?? "default"}", EditMessageId: id));
                        }
                    }
                }, token);
                if (reply.DocumentPath is not null) Directory.Delete(Path.GetDirectoryName(reply.DocumentPath)!, true);
            }
            catch (TelegramException ex)
            {
                if (reply.EditMessageId is not null && ex.Status == 400)
                { await journal.ChangeAsync(d => d.Replies.RemoveAll(r => r.Id == reply.Id), token); continue; }
                logger.LogWarning("Reply delivery unavailable (status {Status}); retaining reply for retry.", ex.Status);
                await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfter), token);
            }
            catch (Exception ex) when (reply.DocumentPath is not null && ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogWarning("Queued attachment unavailable ({ErrorType}).", ex.GetType().Name);
                await journal.ChangeAsync(d =>
                {
                    var index = d.Replies.FindIndex(r => r.Id == reply.Id);
                    if (index >= 0) d.Replies[index] = reply with { DocumentPath = null, Part = 0,
                        Text = "The queued file is no longer available. Request it again with /file after checking the local file." };
                }, token);
            }
        }
    }

    public static string Command(string text) => text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()?.Split('@', 2)[0].ToLowerInvariant() ?? "";
}
