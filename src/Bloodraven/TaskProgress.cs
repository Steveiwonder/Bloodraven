using System.Diagnostics;
using System.Text.Json;

namespace Bloodraven;

// Keep only the latest activity. Progress is transient, never journalled or replayed.
public sealed class TaskProgress(string botToken)
{
    readonly object gate = new();
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    string? latest;

    public void Observe(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) ||
            type.GetString() is not ("item.started" or "item.updated" or "item.completed") ||
            !root.TryGetProperty("item", out var item) || !item.TryGetProperty("type", out var kind)) return;
        string? message = null;
        if (kind.GetString() == "agent_message" && item.TryGetProperty("text", out var text))
            message = text.GetString();
        else if (kind.GetString() == "command_execution")
            message = type.GetString() == "item.completed" ? "A command finished; Codex is continuing." : "Codex is running a command.";
        else if (kind.GetString() == "file_change") message = "Codex is updating files.";
        else if (kind.GetString() == "mcp_tool_call") message = "Codex is using a tool.";
        // Do not forward reasoning, raw command output, stderr, or tool payloads.
        if (string.IsNullOrWhiteSpace(message)) return;
        message = message.Replace(botToken, "[redacted]", StringComparison.Ordinal);
        message = string.Concat(message.Where(c => !char.IsControl(c) || c == '\n')).Trim();
        if (message.Length > 700)
        {
            var length = char.IsHighSurrogate(message[699]) ? 699 : 700;
            message = message[..length] + "…";
        }
        lock (gate) latest = message;
    }

    public string TakeUpdate()
    {
        string? activity;
        lock (gate) { activity = latest; latest = null; }
        var seconds = (long)elapsed.Elapsed.TotalSeconds;
        var duration = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
        return $"Still working… ({duration} elapsed)" + (activity is null ? "" : "\n\n" + activity);
    }

    public async Task RunAsync(TimeSpan interval, Func<string, CancellationToken, Task> send, CancellationToken token)
    {
        if (interval == TimeSpan.Zero) return;
        while (true)
        {
            await Task.Delay(interval, token);
            await send(TakeUpdate(), token);
        }
    }
}
