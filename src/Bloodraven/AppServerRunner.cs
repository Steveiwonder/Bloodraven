using System.Diagnostics;
using System.Text.Json;

namespace Bloodraven;

// Approval mode never falls back to exec: unsupported protocols fail closed.
public static class AppServerRunner
{
    public static async Task<string> RunAsync(AppOptions options, SessionStore sessions, string conversation,
        string prompt, IReadOnlyList<string> images, Action<JsonElement>? progress,
        Func<string, JsonElement, CancellationToken, Task<bool>> approve, CancellationToken token)
    {
        var start = new ProcessStartInfo(options.CodexExecutable) {
            WorkingDirectory = options.WorkingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("app-server");
        options.RemoveBridgeSecrets(start);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex app-server.");
        var descendants = new List<LinuxProcess>();
        using var cancel = token.Register(() =>
        {
            try
            {
                if (!process.HasExited) { descendants.AddRange(LinuxProcess.Descendants(process.Id)); process.Kill(true); }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        await using var lines = BoundedLines.ReadAsync(process.StandardOutput, 1_048_576, token).GetAsyncEnumerator(token);
        string? final = null;
        bool finished = false;
        int sequence = 0;
        var proposals = new Dictionary<string, JsonElement>();
        async Task Write(object value)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        async Task Handle(JsonElement root)
        {
            if (!root.TryGetProperty("method", out var method)) return;
            var name = method.GetString();
            var p = root.TryGetProperty("params", out var parameters) ? parameters : default;
            if (root.TryGetProperty("id", out var requestId))
            {
                if (name is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                {
                    // File approvals may only carry an itemId. Include the previously
                    // announced complete patch, never ask the user to approve a blind change.
                    var itemId = p.TryGetProperty("itemId", out var itemIdValue) ? itemIdValue.GetString() : null;
                    proposals.TryGetValue(itemId ?? "", out var proposal);
                    var hasCommand = p.TryGetProperty("command", out var proposedCommand) && proposedCommand.ValueKind == JsonValueKind.String;
                    var visible = name == "item/commandExecution/requestApproval" ? hasCommand : proposal.ValueKind == JsonValueKind.Object;
                    using var description = JsonDocument.Parse(JsonSerializer.Serialize(new { request = p, proposal = proposal.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : proposal }));
                    var accepted = visible && await approve(name, description.RootElement, token);
                    await Write(new { id = requestId.Clone(), result = new { decision = accepted ? "accept" : "decline" } });
                }
                else if (name == "item/permissions/requestApproval")
                    await Write(new { id = requestId.Clone(), result = new { permissions = new { }, scope = "turn" } });
                else
                    await Write(new { id = requestId.Clone(), error = new { code = -32601, message = "Unsupported interactive request; no permission granted." } });
                return;
            }
            if (name is "item/started" or "item/completed" && p.TryGetProperty("item", out var item))
            {
                var kind = item.GetProperty("type").GetString();
                if (kind == "fileChange" && item.TryGetProperty("id", out var proposalId))
                {
                    if (proposals.Count >= 100) proposals.Clear();
                    proposals[proposalId.GetString()!] = item.Clone();
                }
                if (name == "item/completed" && kind == "agentMessage" && item.TryGetProperty("text", out var text) &&
                    (!item.TryGetProperty("phase", out var phase) || phase.ValueKind == JsonValueKind.Null || phase.GetString() == "final_answer"))
                {
                    final = text.GetString();
                    if (final?.Length > 100_000) throw new InvalidDataException("Codex response exceeded its limit.");
                }
                if (kind is "commandExecution" or "fileChange" or "mcpToolCall")
                {
                    using var mapped = JsonDocument.Parse(JsonSerializer.Serialize(new {
                        type = name == "item/started" ? "item.started" : "item.completed",
                        item = new {
                            type = kind == "commandExecution" ? "command_execution" : kind == "fileChange" ? "file_change" : "mcp_tool_call",
                            id = item.GetProperty("id").GetString(),
                            command = item.TryGetProperty("command", out var cmd) ? cmd.GetString() : null,
                            aggregated_output = item.TryGetProperty("aggregatedOutput", out var output) ? output.GetString() : null,
                            exit_code = item.TryGetProperty("exitCode", out var exit) ? exit.Clone() : (JsonElement?)null
                        }
                    }));
                    progress?.Invoke(mapped.RootElement);
                }
            }
            if (name == "turn/completed")
            {
                var status = p.GetProperty("turn").GetProperty("status").GetString();
                if (status != "completed") throw new InvalidOperationException("Codex turn did not complete successfully.");
                finished = true;
            }
        }
        async Task<JsonElement> Request(string method, object parameters)
        {
            var id = ++sequence;
            await Write(new { id, method, @params = parameters });
            while (await lines.MoveNextAsync())
            {
                using var json = JsonDocument.Parse(lines.Current);
                var root = json.RootElement;
                if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex app-server rejected the request. Check the installed Codex version and local configuration.");
                    return root.GetProperty("result").Clone();
                }
                await Handle(root);
            }
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Codex app-server disconnected.");
        }
        try
        {
            await Request("initialize", new { clientInfo = new { name = "bloodraven", version = "0.2.0" } });
            await Write(new { method = "initialized", @params = new { } });
            var saved = await sessions.GetAsync(token, conversation, approved: true);
            var thread = await Request(saved is null ? "thread/start" : "thread/resume", new {
                threadId = saved, cwd = options.WorkingDirectory, approvalPolicy = "unlessTrusted", sandbox = "readOnly"
            });
            var threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;
            await sessions.SetAsync(threadId, token, conversation, approved: true);
            var input = new List<object> { new { type = "text", text = prompt } };
            input.AddRange(images.Select(path => (object)new { type = "localImage", path }));
            await Request("turn/start", new { threadId, input, cwd = options.WorkingDirectory,
                approvalPolicy = "unlessTrusted", sandboxPolicy = new { type = "readOnly" } });
            while (!finished && await lines.MoveNextAsync())
            { using var json = JsonDocument.Parse(lines.Current); await Handle(json.RootElement); }
            token.ThrowIfCancellationRequested();
            if (!finished) throw new InvalidOperationException("Codex disconnected before turn completion.");
            return final ?? "Codex finished without a final message.";
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                { descendants.AddRange(LinuxProcess.Descendants(process.Id)); process.Kill(true); }
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                try { await stderr.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (descendants.Any(p => p.IsAlive()))
                { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(20); }
            }
            catch (Exception ex) { throw new FatalRunnerException("Unable to reap Codex app-server.", ex); }
        }
    }
}
