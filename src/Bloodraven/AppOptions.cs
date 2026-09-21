using System.Diagnostics;

namespace Bloodraven;

public sealed class AppOptions
{
    public string TelegramBotToken { get; } = Required("BLOODRAVEN_TELEGRAM_BOT_TOKEN");
    public long AllowedUserId { get; } = Positive("BLOODRAVEN_ALLOWED_USER_ID");
    public long? AllowedChatId { get; } = OptionalChatId();
    public string WorkingDirectory { get; } = Path.GetFullPath(Required("BLOODRAVEN_WORKING_DIRECTORY"));
    public string StateDirectory { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("BLOODRAVEN_STATE_DIRECTORY") ?? "./state");
    public string CodexExecutable { get; } = Environment.GetEnvironmentVariable("BLOODRAVEN_CODEX_EXECUTABLE") ?? "codex";
    public string Sandbox { get; } = ValidateSandbox(Environment.GetEnvironmentVariable("BLOODRAVEN_CODEX_SANDBOX") ?? "workspace-write");
    public int TaskTimeoutSeconds { get; } = Timeout();
    public int ProgressIntervalSeconds { get; } = ProgressInterval();

    static int ProgressInterval()
    {
        var value = Environment.GetEnvironmentVariable("BLOODRAVEN_PROGRESS_INTERVAL_SECONDS");
        return value is null ? 30 : int.TryParse(value, out var n) && (n == 0 || n is >= 10 and <= 3600) ? n
            : throw new InvalidOperationException("BLOODRAVEN_PROGRESS_INTERVAL_SECONDS must be 0 (off) or 10–3600.");
    }

    static string Required(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
        ? Environment.GetEnvironmentVariable(name)! : throw new InvalidOperationException($"Missing {name}.");
    static long Positive(string name) => long.TryParse(Required(name), out var n) && n > 0
        ? n : throw new InvalidOperationException($"{name} must be a positive integer.");
    static long? OptionalChatId()
    {
        var value = Environment.GetEnvironmentVariable("BLOODRAVEN_ALLOWED_CHAT_ID");
        if (value is null) return null;
        return long.TryParse(value, out var id) && id > 0 ? id
            : throw new InvalidOperationException("BLOODRAVEN_ALLOWED_CHAT_ID must be a positive private-chat ID, or omitted.");
    }
    static string ValidateSandbox(string value) => value is "read-only" or "workspace-write" or "danger-full-access"
        ? value : throw new InvalidOperationException("Invalid BLOODRAVEN_CODEX_SANDBOX.");
    static int Timeout()
    {
        var value = Environment.GetEnvironmentVariable("BLOODRAVEN_TASK_TIMEOUT_SECONDS");
        return value is null ? 3600 : int.TryParse(value, out var n) && n is >= 30 and <= 86400 ? n
            : throw new InvalidOperationException("BLOODRAVEN_TASK_TIMEOUT_SECONDS must be 30–86400.");
    }
    public bool Accepts(TelegramMessage message) => message.Chat.Type == "private" &&
        message.From?.Id == AllowedUserId && (AllowedChatId is null || message.Chat.Id == AllowedChatId);
    public void RemoveBridgeSecrets(ProcessStartInfo start)
    {
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("BLOODRAVEN_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
    }
}

public static class Preflight
{
    public static async Task CheckAsync(AppOptions options, CancellationToken token)
    {
        await CheckCommandAsync(options, "git", ["rev-parse", "--show-toplevel"], token);
        await CheckCommandAsync(options, options.CodexExecutable, ["--version"], token);
        await CheckCommandAsync(options, options.CodexExecutable, ["login", "status"], token);
    }
    static async Task CheckCommandAsync(AppOptions options, string executable, string[] args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        options.RemoveBridgeSecrets(start);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Preflight failed to start.");
        try
        {
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token),
                process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token), process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new InvalidOperationException("Git/Codex preflight failed. Check repository, PATH and login as the service user.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
