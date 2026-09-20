using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient<TelegramClient>();
builder.Services.AddSingleton<AppOptions>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<CodexRunner>();
builder.Services.AddHostedService<BotWorker>();
await builder.Build().RunAsync();

sealed class AppOptions
{
    public string TelegramBotToken { get; } = Required("BLOODRAVEN_TELEGRAM_BOT_TOKEN");
    public long AllowedUserId { get; } = Long("BLOODRAVEN_ALLOWED_USER_ID");
    public long? AllowedChatId { get; } = OptionalLong("BLOODRAVEN_ALLOWED_CHAT_ID");
    public string WorkingDirectory { get; } = Path.GetFullPath(Required("BLOODRAVEN_WORKING_DIRECTORY"));
    public string StateDirectory { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("BLOODRAVEN_STATE_DIRECTORY") ?? "./state");
    public string CodexExecutable { get; } = Environment.GetEnvironmentVariable("BLOODRAVEN_CODEX_EXECUTABLE") ?? "codex";
    public string Sandbox { get; } = Environment.GetEnvironmentVariable("BLOODRAVEN_CODEX_SANDBOX") ?? "workspace-write";

    static string Required(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
        ? Environment.GetEnvironmentVariable(name)!
        : throw new InvalidOperationException($"Missing required environment variable {name}.");
    static long Long(string name) => long.TryParse(Required(name), out var value)
        ? value : throw new InvalidOperationException($"{name} must be an integer.");
    static long? OptionalLong(string name) => long.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;
}

sealed class BotWorker(
    TelegramClient telegram,
    CodexRunner codex,
    SessionStore sessions,
    AppOptions options,
    ILogger<BotWorker> logger) : BackgroundService
{
    readonly Channel<BotMessage> queue = Channel.CreateBounded<BotMessage>(new BoundedChannelOptions(20)
    {
        FullMode = BoundedChannelFullMode.Wait
    });
    long offset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.StateDirectory);
        if (!Directory.Exists(Path.Combine(options.WorkingDirectory, ".git")))
            throw new InvalidOperationException("BLOODRAVEN_WORKING_DIRECTORY must be a Git repository.");

        var consumer = ConsumeAsync(stoppingToken);
        logger.LogInformation("Bloodraven is listening for Telegram messages.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var update in await telegram.GetUpdatesAsync(offset, stoppingToken))
                {
                    offset = update.UpdateId + 1;
                    var message = update.Message;
                    if (message?.Text is null || message.From?.Id != options.AllowedUserId ||
                        (options.AllowedChatId is not null && message.Chat.Id != options.AllowedChatId))
                        continue;

                    var text = message.Text.Trim();
                    var command = text.Split(' ', 2)[0].ToLowerInvariant();
                    if (command == "/cancel")
                    {
                        await telegram.SendAsync(message.Chat.Id, codex.Cancel() ? "Cancellation requested." : "Nothing is running.", stoppingToken);
                        continue;
                    }
                    if (command == "/status")
                    {
                        var id = await sessions.GetAsync(stoppingToken);
                        await telegram.SendAsync(message.Chat.Id, $"Status: {(codex.IsRunning ? "working" : "idle")}\nSession: {id ?? "none"}\nQueued: {queue.Reader.Count}", stoppingToken);
                        continue;
                    }

                    if (!queue.Writer.TryWrite(new BotMessage(message.Chat.Id, text)))
                        await telegram.SendAsync(message.Chat.Id, "The queue is full. Try again after the current tasks finish.", stoppingToken);
                    else
                        await telegram.SendAsync(message.Chat.Id, "Queued.", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram polling failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        queue.Writer.TryComplete();
        await consumer;
    }

    async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (message.Text.Split(' ', 2)[0].ToLowerInvariant())
                {
                    case "/start":
                    case "/help":
                        await telegram.SendAsync(message.ChatId, "Bloodraven connects this private chat to Codex.\n\n/new - start a new session\n/status - show status\n/cancel - stop the current task\n/help - show this message", stoppingToken);
                        break;
                    case "/new":
                        await sessions.ClearAsync(stoppingToken);
                        await telegram.SendAsync(message.ChatId, "Started a fresh Codex conversation.", stoppingToken);
                        break;
                    default:
                        await telegram.SendAsync(message.ChatId, "Working…", stoppingToken);
                        var result = await codex.RunAsync(message.Text, stoppingToken);
                        foreach (var part in Split(result, 3900))
                            await telegram.SendAsync(message.ChatId, part, stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                await telegram.SendAsync(message.ChatId, "Task cancelled.", stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to handle a message.");
                await telegram.SendAsync(message.ChatId, $"Task failed: {ex.Message}", stoppingToken);
            }
        }
    }

    static IEnumerable<string> Split(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) yield return "Codex finished without a response.";
        for (var i = 0; i < text.Length; i += max)
            yield return text.Substring(i, Math.Min(max, text.Length - i));
    }
}

sealed class CodexRunner(AppOptions options, SessionStore sessions, ILogger<CodexRunner> logger)
{
    readonly object gate = new();
    CancellationTokenSource? current;
    public bool IsRunning { get { lock (gate) return current is not null; } }

    public bool Cancel()
    {
        lock (gate)
        {
            if (current is null) return false;
            current.Cancel();
            return true;
        }
    }

    public async Task<string> RunAsync(string prompt, CancellationToken stoppingToken)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (gate) current = runCts;
        try
        {
            var sessionId = await sessions.GetAsync(runCts.Token);
            var start = new ProcessStartInfo(options.CodexExecutable)
            {
                WorkingDirectory = options.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--json");
            start.ArgumentList.Add("--sandbox");
            start.ArgumentList.Add(options.Sandbox);
            if (sessionId is not null)
            {
                start.ArgumentList.Add("resume");
                start.ArgumentList.Add(sessionId);
            }
            start.ArgumentList.Add(prompt);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex.");
            using var cancellationRegistration = runCts.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            var stderrTask = process.StandardError.ReadToEndAsync(runCts.Token);
            string? finalMessage = null;
            while (await process.StandardOutput.ReadLineAsync(runCts.Token) is { } line)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    if (type == "thread.started" &&
                        root.TryGetProperty("thread_id", out var thread))
                        await sessions.SetAsync(thread.GetString()!, runCts.Token);
                    if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                        item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message" &&
                        item.TryGetProperty("text", out var text))
                        finalMessage = text.GetString();
                }
                catch (JsonException ex) { logger.LogWarning(ex, "Ignored malformed Codex JSONL output."); }
            }

            await process.WaitForExitAsync(runCts.Token);
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Codex exited with code {process.ExitCode}." : stderr.Trim());
            return finalMessage ?? "Codex finished without a final message.";
        }
        finally { lock (gate) current = null; }
    }
}

sealed class SessionStore(AppOptions options)
{
    string FilePath => Path.Combine(options.StateDirectory, "session.txt");
    readonly SemaphoreSlim mutex = new(1, 1);

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        await mutex.WaitAsync(cancellationToken);
        try { return File.Exists(FilePath) ? (await File.ReadAllTextAsync(FilePath, cancellationToken)).Trim() : null; }
        finally { mutex.Release(); }
    }

    public async Task SetAsync(string id, CancellationToken cancellationToken)
    {
        await mutex.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.StateDirectory);
            var temporary = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporary, id, cancellationToken);
            File.Move(temporary, FilePath, true);
        }
        finally { mutex.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await mutex.WaitAsync(cancellationToken);
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        finally { mutex.Release(); }
    }
}

sealed class TelegramClient(HttpClient http, AppOptions options)
{
    string Api(string method) => $"https://api.telegram.org/bot{options.TelegramBotToken}/{method}";

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        var response = await http.PostAsJsonAsync(Api("getUpdates"), new { offset, timeout = 30, allowed_updates = new[] { "message" } }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<List<TelegramUpdate>>>(cancellationToken: cancellationToken);
        if (envelope is null || !envelope.Ok) throw new InvalidOperationException(envelope?.Description ?? "Telegram returned an invalid response.");
        return envelope.Result ?? [];
    }

    public async Task SendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var response = await http.PostAsJsonAsync(Api("sendMessage"), new { chat_id = chatId, text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

sealed record BotMessage(long ChatId, string Text);
sealed record TelegramEnvelope<T>(bool Ok, T? Result, string? Description);
sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    [property: JsonPropertyName("message")] TelegramMessage? Message);
sealed record TelegramMessage(
    [property: JsonPropertyName("from")] TelegramUser? From,
    [property: JsonPropertyName("chat")] TelegramChat Chat,
    [property: JsonPropertyName("text")] string? Text);
sealed record TelegramUser(long Id);
sealed record TelegramChat(long Id);
