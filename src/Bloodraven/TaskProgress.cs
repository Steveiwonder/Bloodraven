using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bloodraven;

// Keep only the latest activity. Progress is transient, never journalled or replayed.
public sealed class TaskProgress(string? botToken = null)
{
    readonly object gate = new();
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    string? latest;
    readonly Dictionary<string, CommandState> commands = new();
    readonly Queue<string> recent = new();
    int completed;
    bool capped;
    bool activityReported;
    sealed class CommandState(TimeSpan observed)
    {
        public TimeSpan Observed { get; } = observed;
        public string Command { get; set; } = "Command details unavailable";
        public string Output { get; set; } = "";
        public bool Done { get; set; }
    }

    static string? Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    string Excerpt(string value, int limit, bool tail = false)
    {
        // Redact before truncation so splitting a credential cannot expose it.
        if (!string.IsNullOrEmpty(botToken)) value = value.Replace(botToken, "[redacted]", StringComparison.Ordinal);
        if (value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) return "[private key output omitted]";
        try
        {
            value = Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, @"(?i)(\b(?:bearer|basic)\s+)\S+", "$1[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, "(?i)((?:[\\w-]*(?:token|password|passwd|secret|api[_-]?key)[\\w-]*)[\"']?\\s*(?:=|:)\\s*|--(?:token|password|secret|api-key)\\s+)(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", "$1[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, @"(https?://)[^\s/@]+:[^\s/@]+@", "$1[redacted]@", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException) { return "[output omitted]"; }
        value = string.Concat(value.Where(c => !char.IsControl(c) || c == '\n')).Replace('`', '\'').Trim();
        if (tail) value = string.Join('\n', value.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(3));
        if (value.Length <= limit) return value;
        if (tail)
        {
            var start = value.Length - limit;
            if (char.IsLowSurrogate(value[start])) start++;
            return "…" + value[start..];
        }
        if (char.IsHighSurrogate(value[limit - 1])) limit--;
        return value[..limit] + "…";
    }

    void RecordCommand(JsonElement item, string eventType)
    {
        var id = Text(item, "id");
        if (string.IsNullOrEmpty(id) || id.Length > 128)
        {
            latest = eventType == "item.completed" ? "A command finished; details unavailable." : "Codex is running a command; details unavailable.";
            return;
        }
        if (!commands.TryGetValue(id, out var command))
        {
            if (commands.Count >= 1024) { capped = true; return; }
            commands[id] = command = new CommandState(elapsed.Elapsed);
            activityReported = true;
        }
        if (command.Done) return; // Repeated completion events must not inflate counts.
        if (Text(item, "command") is { } name) command.Command = Excerpt(name, 240);
        var output = Text(item, "aggregated_output");
        if (output is not null)
        {
            var excerpt = Excerpt(output, 600, tail: true);
            if (excerpt.Length > 0 && excerpt != command.Output)
            {
                command.Output = excerpt;
                AddRecent("Latest command output:\n```\n" + excerpt + "\n```");
            }
        }
        if (eventType == "item.completed")
        {
            command.Done = true;
            completed++;
            var exit = item.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var n)
                ? $" (exit {n})" : " (exit code unavailable)";
            AddRecent($"A command finished{exit}:\n```\n{command.Command}\n```");
        }
    }

    void AddRecent(string message)
    {
        if (recent.Count == 3) recent.Dequeue();
        recent.Enqueue(message);
    }

    public void Observe(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) ||
            type.GetString() is not ("item.started" or "item.updated" or "item.completed") ||
            !root.TryGetProperty("item", out var item) || !item.TryGetProperty("type", out var kind)) return;
        string? message = null;
        // agent_message can contain the final answer before the process exits.
        // Keep answer delivery exclusively in the runner's final-result path.
        if (kind.GetString() == "command_execution")
        {
            lock (gate) RecordCommand(item, type.GetString()!);
            return;
        }
        else if (kind.GetString() == "file_change") message = "Codex is updating files.";
        else if (kind.GetString() == "mcp_tool_call") message = "Codex is using a tool.";
        // Agent messages, reasoning, and tool result payloads are never forwarded.
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (gate) latest = message;
    }

    public string TakeUpdate()
    {
        var details = new List<string>();
        lock (gate)
        {
            var running = commands.Values.Where(c => !c.Done).ToArray();
            if (commands.Count > 0) details.Add($"Commands: {completed} completed, {running.Length} running" + (capped ? " (tracking limit reached)" : ""));
            foreach (var command in running.Take(2))
                details.Add($"Running (observed for {(long)(elapsed.Elapsed - command.Observed).TotalSeconds}s):\n```\n{command.Command}\n```");
            if (latest is not null) details.Add(latest);
            if (recent.Count == 0 && latest is null && !activityReported) details.Add("No new command output or activity reported since the last update.");
            details.AddRange(recent);
            recent.Clear();
            latest = null;
            activityReported = false;
        }
        var seconds = (long)elapsed.Elapsed.TotalSeconds;
        var duration = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
        return $"Still working… ({duration} elapsed)\n\n" + string.Join("\n\n", details);
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
