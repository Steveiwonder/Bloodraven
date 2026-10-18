namespace Bloodraven;

// Only fixed diagnostic labels are exposed. CLI stderr and error events may
// contain prompts, credentials, URLs or command output and are never logged.
public sealed class CodexFailure(string stage, string reason, bool resumed, int? exitCode = null, string? cause = null) : InvalidOperationException($"Codex failed during {stage}: {reason}.")
{
    public const string ModelCapacity = "selected model is at capacity";

    public string UserMessage(long taskId)
    {
        if (Reason == ModelCapacity)
        {
            return $"Task {taskId} stopped because the selected Codex model is at capacity. Your conversation has been kept. Wait a moment and send a continuation message, or use /model to choose another model before continuing. The task may already have made changes; ask Codex to check what completed before repeating any steps.";
        }

        return $"Task {taskId} failed: {Reason}. Your conversation has been kept. Check the service logs for the failure stage and exit code.";
    }

    public string Stage { get; } = stage;
    public string Reason { get; } = reason;
    public bool Resumed { get; } = resumed;
    public int? ExitCode { get; } = exitCode;
    public string? Cause { get; } = cause;

    public static string Classify(string diagnostic)
    {
        var text = diagnostic.ToLowerInvariant();

        if (text.Contains("unexpected argument") || text.Contains("unrecognized option"))
        {
            return "CLI arguments rejected; check installed Codex compatibility";
        }

        if (text.Contains("context window") || text.Contains("context length") || text.Contains("too many tokens"))
        {
            return "context limit reached";
        }

        if (text.Contains("server_overloaded") || text.Contains("model is at capacity"))
        {
            return ModelCapacity;
        }

        if (text.Contains("usage limit") || text.Contains("quota") || text.Contains("rate limit") || text.Contains("rate_limit") || text.Contains("credit balance") || text.Contains("insufficient credits"))
        {
            return "usage or rate limit reached";
        }

        if (text.Contains("unauthorized") || text.Contains("authentication") || text.Contains("refresh token") || text.Contains("not logged in") || text.Contains("authentication_failed") || text.Contains("invalid api key"))
        {
            return "authentication rejected; check Codex login as the service user";
        }

        if (text.Contains("model") && (text.Contains("not supported") || text.Contains("does not exist") || text.Contains("not found") || text.Contains("not available")))
        {
            return "selected model unavailable or unsupported";
        }

        if (text.Contains("reasoning") && (text.Contains("unsupported") || text.Contains("not supported") || text.Contains("invalid")))
        {
            return "reasoning setting rejected";
        }

        if (text.Contains("session") && (text.Contains("not found") || text.Contains("does not exist")))
        {
            return "saved session not found";
        }

        if (text.Contains("permission denied") || text.Contains("operation not permitted") || text.Contains("read-only file system"))
        {
            return "operating-system or sandbox permission denied";
        }

        if (text.Contains("connection") || text.Contains("dns") || text.Contains("tls") || text.Contains("timed out"))
        {
            return "network connection failed or timed out";
        }

        return "unclassified CLI error; reproduce in the terminal for private diagnostics";
    }
}
