using System.Diagnostics;
using System.Text.Json;

namespace Bloodraven;

// Only locally controlled stage/reason labels and numeric RPC codes are exposed.
public sealed class AppServerException(string stage, string reason, int? rpcCode = null)
    : Exception($"Approval mode failed during {stage}: {reason}" + (rpcCode is null ? "." : $" (RPC {rpcCode})."))
{
    public string Stage { get; } = stage;
    public string Reason { get; } = reason;
    public int? RpcCode { get; } = rpcCode;
}

// Approval mode never falls back to exec: unsupported protocols fail closed.
public static class AppServerRunner
{
    public static async Task<string> RunAsync(AppOptions options, SessionStore sessions, string conversation,
        string prompt, IReadOnlyList<string> images, Action<JsonElement>? progress,
        Func<string, JsonElement, CancellationToken, Task<bool>> approve, CancellationToken token, ModelSettings? settings = null, TaskTimings? timing = null, string? sideSession = null)
    {
        var start = new ProcessStartInfo(options.CodexExecutable) {
            WorkingDirectory = options.WorkingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        (settings ?? new()).AddArguments(start);
        start.ArgumentList.Add("app-server");
        if (sideSession is not null)
        {
            // Disable integrations in this process only; never rewrite the user's config.
            foreach (var feature in new[] { "apps", "multi_agent", "multi_agent_v2", "browser_use", "computer_use", "plugins", "shell_tool", "code_mode", "code_mode_host" })
            { start.ArgumentList.Add("-c"); start.ArgumentList.Add($"features.{feature}=false"); }
        }
        options.RemoveBridgeSecrets(start);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex app-server.");
        timing?.Mark("Process started");
        var descendants = new System.Collections.Concurrent.ConcurrentBag<LinuxProcess>();
        using var cancel = token.Register(() =>
        {
            try
            {
                if (!process.HasExited) { foreach (var child in LinuxProcess.Descendants(process.Id)) descendants.Add(child); process.Kill(true); }
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
                    if (item.GetRawText().Length <= 2600) proposals[proposalId.GetString()!] = item.Clone();
                    else proposals.Remove(proposalId.GetString()!);
                }
                if (name == "item/completed" && kind == "agentMessage" && item.TryGetProperty("text", out var text) &&
                    (!item.TryGetProperty("phase", out var phase) || phase.ValueKind == JsonValueKind.Null || phase.GetString() == "final_answer"))
                {
                    final = text.GetString();
                    timing?.Mark("Final answer");
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
                if (status != "completed") throw new AppServerException("turn/completed", "Codex did not complete the turn; review any changes before retrying");
                finished = true;
            }
        }
        async Task<JsonElement> Request(string method, object parameters)
        {
            var id = ++sequence;
            await Write(new { id, method, @params = parameters });
            if (method == "turn/start") timing?.Mark("Prompt sent");
            while (await lines.MoveNextAsync())
            {
                timing?.Mark("First event", first: true);
                using var json = JsonDocument.Parse(lines.Current);
                var root = json.RootElement;
                if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        int? code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var number) ? number : null;
                        throw new AppServerException(method, code is -32600 or -32601 or -32602
                            ? "Codex rejected the protocol request; check CLI compatibility"
                            : "Codex rejected the request; check local authentication and configuration", code);
                    }
                    return root.GetProperty("result").Clone();
                }
                await Handle(root);
            }
            token.ThrowIfCancellationRequested();
            throw new AppServerException(method, "Codex app-server disconnected");
        }
        try
        {
            await Request("initialize", new { clientInfo = new { name = "bloodraven", version = BotWorker.Version } });
            timing?.Mark("Initialized");
            await Write(new { method = "initialized", @params = new { } });
            string threadId;
            if (sideSession is not null)
            {
                // Discover configured MCP servers so even auto-approved remote tools are disabled.
                var configuration = await Request("config/read", new { includeLayers = false, cwd = options.WorkingDirectory });
                var overrides = new Dictionary<string, object>();
                if (configuration.GetProperty("config").TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Object)
                {
                    var disabledServers = new Dictionary<string, object>();
                    foreach (var server in servers.EnumerateObject())
                    {
                        // config/read emits absent optional settings as JSON null. Codex's
                        // JSON-to-TOML override conversion turns null into an empty string,
                        // which breaks numeric settings such as tool_timeout_sec.
                        var definition = server.Value.EnumerateObject()
                            .Where(p => p.Value.ValueKind != JsonValueKind.Null)
                            .ToDictionary(p => p.Name, p => (object)p.Value.Clone());
                        definition["enabled"] = false;
                        disabledServers[server.Name] = definition;
                    }
                    // Override the structured table: dotted keys split server names
                    // containing dots, even when those names are TOML-quoted.
                    overrides["mcp_servers"] = disabledServers;
                }
                var fork = await Request("thread/fork", new {
                    threadId = sideSession, cwd = options.WorkingDirectory,
                    approvalPolicy = "untrusted", sandbox = "read-only", config = overrides,
                    developerInstructions = "Answer only the user's BTW side question using the inherited conversation as context. " +
                        "The original task is still running separately. Do not continue it, change files, execute commands, " +
                        "use external integrations, delegate work, or perform actions. Give a concise answer; " +
                        "be explicit when the saved context does not contain the current status."
                });
                threadId = fork.GetProperty("thread").GetProperty("id").GetString()!;
                if (threadId == sideSession) throw new AppServerException("thread/fork", "Codex did not create a separate thread");
                // Deliberately do not persist the fork as the conversation's active session.
            }
            else
            {
                var saved = await sessions.GetAsync(token, conversation, approved: true);
                timing?.Mark(saved is null ? "New session" : "Resuming session");
                var thread = saved is null
                    ? await Request("thread/start", new { cwd = options.WorkingDirectory, approvalPolicy = "untrusted", sandbox = "read-only" })
                    : await Request("thread/resume", new { threadId = saved, cwd = options.WorkingDirectory, approvalPolicy = "untrusted", sandbox = "read-only" });
                threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;
                await sessions.SetAsync(threadId, token, conversation, approved: true);
            }
            timing?.Mark("Session ready");
            var input = new List<object> { new { type = "text", text = prompt, text_elements = Array.Empty<object>() } };
            input.AddRange(images.Select(path => (object)new { type = "localImage", path }));
            await Request("turn/start", new { threadId, input, cwd = options.WorkingDirectory,
                model = settings?.Model, effort = settings?.Effort,
                approvalPolicy = "untrusted", sandboxPolicy = new { type = "readOnly", networkAccess = false } });
            while (!finished && await lines.MoveNextAsync())
            { using var json = JsonDocument.Parse(lines.Current); await Handle(json.RootElement); }
            token.ThrowIfCancellationRequested();
            if (!finished) throw new AppServerException("turn/completed", "Codex disconnected before turn completion");
            return final ?? "Codex finished without a final message.";
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                { foreach (var child in LinuxProcess.Descendants(process.Id)) descendants.Add(child); try { process.Kill(true); } catch (InvalidOperationException) { } }
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
