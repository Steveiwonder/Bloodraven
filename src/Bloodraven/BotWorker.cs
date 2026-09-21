namespace Bloodraven;

public sealed class BotWorker(TelegramClient telegram, CodexRunner codex, SessionStore sessions,
    Journal journal, AppOptions options, ILogger<BotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.StateDirectory);
        // Two instances must not consume the same bot/journal concurrently.
        using var lease = new FileStream(Path.Combine(options.StateDirectory, "instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        await Preflight.CheckAsync(options, stoppingToken);
        await journal.InitializeAsync(stoppingToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] tasks = [PollAsync(lifetime.Token), ConsumeAsync(lifetime.Token), DeliverAsync(lifetime.Token)];
        try
        {
            await Task.WhenAny(tasks);
            // A stopped consumer is a failed service, not an invisible abandoned queue.
            lifetime.Cancel();
            await Task.WhenAll(tasks);
        }
        finally
        {
            lifetime.Cancel();
            File.Delete(Path.Combine(options.StateDirectory, "ready"));
        }
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
                await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfter), token);
                continue;
            }
            await AtomicFile.WriteAsync(Path.Combine(options.StateDirectory, "ready"), DateTimeOffset.UtcNow.ToString("O"), token);
            foreach (var update in updates)
            {
                if (update.UpdateId < state.Offset) continue;
                var message = update.Message;
                var text = message?.Text?.Trim();
                var allowed = message is not null && options.Accepts(message) && !string.IsNullOrWhiteSpace(text);
                var command = Command(text ?? "");
                string? status = null;
                if (allowed && command == "/status")
                {
                    string? session;
                    try { session = await sessions.GetAsync(token); }
                    catch (InvalidDataException) { session = "invalid — use /new"; }
                    status = $"Status: {(codex.IsRunning ? "working" : "idle")}\nSession: {session ?? "none"}\nQueued: {state.Jobs.Count}\nPending replies: {state.Replies.Count}";
                }
                await journal.ChangeAsync(d =>
                {
                    d.Offset = update.UpdateId + 1;
                    if (!allowed) return;
                    if (command == "/cancel") return; // Never replay cancellation after a restart.
                    if (status is not null) Journal.AddReply(d, message!.Chat.Id, status);
                    else if (d.Jobs.Count >= 20 || text!.Length > 16_384)
                        Journal.AddReply(d, message!.Chat.Id, "Queue full or message too large. Try again after current work finishes.");
                    else
                    {
                        d.Jobs.Add(new Job(update.UpdateId, message!.Chat.Id, text!));
                        Journal.AddReply(d, message.Chat.Id, "Queued.");
                    }
                }, token);
                if (allowed && command == "/cancel")
                {
                    var cancelled = codex.Cancel();
                    await journal.ChangeAsync(d => Journal.AddReply(d, message!.Chat.Id,
                        cancelled ? "Cancellation requested; queued tasks are unaffected." : "Nothing is running."), token);
                }
                state = await journal.SnapshotAsync(token);
                if (state.Replies.Count >= 100) break;
            }
        }
    }

    async Task ConsumeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var state = await journal.SnapshotAsync(token);
            var job = state.Jobs.FirstOrDefault();
            if (job is null || state.Replies.Count >= 100) { await Task.Delay(250, token); continue; }
            await journal.ChangeAsync(d =>
            {
                d.Jobs[0] = job with { Running = true };
                Journal.AddReply(d, job.ChatId, "Working…");
            }, token);
            string result;
            try
            {
                switch (Command(job.Text))
                {
                    case "/start": case "/help":
                        result = "Bloodraven connects this private chat to Codex.\n/new — fresh session\n/status — status\n/cancel — cancel current task (not queued tasks)\n/help — help";
                        break;
                    case "/new":
                        await sessions.ClearAsync(token);
                        result = "Started a fresh Codex conversation.";
                        break;
                    default: result = await codex.RunAsync(job.Text, token); break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { result = "Task cancelled or timed out. It may have made changes; review them before retrying."; }
            catch (Exception ex) when (ex is not FatalRunnerException)
            {
                // Exception messages/stack traces can contain secrets and prompt/output text.
                logger.LogWarning("Task {Id} failed ({ErrorType}).", job.Id, ex.GetType().Name);
                result = "Task failed. Check Codex login, repository permissions, and saved session locally; /new resets a broken session. Review changes before retrying.";
            }
            await journal.ChangeAsync(d =>
            {
                d.Jobs.RemoveAll(j => j.Id == job.Id);
                Journal.AddReply(d, job.ChatId, result);
            }, token);
        }
    }

    async Task DeliverAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var reply = (await journal.SnapshotAsync(token)).Replies.FirstOrDefault();
            if (reply is null) { await Task.Delay(250, token); continue; }
            var parts = TelegramFormatter.Format(reply.Text);
            try
            {
                await telegram.SendAsync(reply.ChatId, parts[reply.Part], token);
                await journal.ChangeAsync(d =>
                {
                    var index = d.Replies.FindIndex(r => r.Id == reply.Id);
                    if (reply.Part + 1 == parts.Count) d.Replies.RemoveAt(index);
                    else d.Replies[index] = reply with { Part = reply.Part + 1 };
                }, token);
                await Task.Delay(1100, token); // Pace replies to the single private chat.
            }
            catch (TelegramException ex)
            {
                logger.LogWarning("Reply delivery unavailable (status {Status}); retaining reply for retry.", ex.Status);
                await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfter), token);
            }
        }
    }

    public static string Command(string text) => text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()?.Split('@', 2)[0].ToLowerInvariant() ?? "";
}
