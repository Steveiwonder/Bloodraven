using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Bloodraven;

public sealed class FatalRunnerException(string message, Exception inner) : Exception(message, inner);

public sealed class CodexRunner(AppOptions options, SessionStore sessions)
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

    public async Task<string> RunAsync(string prompt, CancellationToken stoppingToken, Action<JsonElement>? progress = null,
        string conversation = "default", IReadOnlyList<string>? images = null,
        Func<string, JsonElement, CancellationToken, Task<bool>>? approve = null, ModelSettings? settings = null, TaskTimings? timing = null)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        run.CancelAfter(TimeSpan.FromSeconds(options.TaskTimeoutSeconds));
        lock (gate)
        {
            if (current is not null) throw new InvalidOperationException("A Codex task is already running.");
            current = run;
        }
        try
        {
            if (approve is not null)
                return await AppServerRunner.RunAsync(options, sessions, conversation, prompt, images ?? [], progress, approve, run.Token, settings, timing);
            string? sessionId;
            try { sessionId = await sessions.GetAsync(run.Token, conversation); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new CodexFailure("session-load", "could not read saved session; check session storage", false, cause: ex.GetType().Name); }
            timing?.Mark(sessionId is null ? "New session" : "Resuming session");
            var start = new ProcessStartInfo(options.CodexExecutable)
            {
                WorkingDirectory = options.WorkingDirectory, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            options.RemoveBridgeSecrets(start);
            (settings ?? new()).AddArguments(start);
            foreach (var arg in new[] { "-C", options.WorkingDirectory, "exec", "--json", "--sandbox", options.Sandbox })
                start.ArgumentList.Add(arg);
            if (sessionId is not null)
            {
                start.ArgumentList.Add("resume");
                start.ArgumentList.Add(sessionId);
            }
            foreach (var path in images ?? []) { start.ArgumentList.Add("--image"); start.ArgumentList.Add(path); }
            start.ArgumentList.Add("-"); // Prompts are stdin data, never CLI options.
            Process StartProcess()
            {
                try { return Process.Start(start) ?? throw new InvalidOperationException(); }
                catch (Exception ex)
                { throw new CodexFailure("process-start", "could not launch Codex; check executable path and service permissions", sessionId is not null, cause: ex.GetType().Name); }
            }
            using var process = StartProcess();
            timing?.Mark("Process started");
            timing?.Mark("Session ready");
            var descendants = new List<LinuxProcess>();
            using var registration = run.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        descendants.AddRange(LinuxProcess.Descendants(process.Id));
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { /* Rechecked in finally; never start another task if reaping fails. */ }
            });
            string? finalMessage = null;
            Exception? streamFailure = null;
            async Task Guard(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException) Interlocked.CompareExchange(ref streamFailure, ex, null);
                    run.Cancel();
                    throw;
                }
            }
            var output = Guard(async () =>
            {
                await foreach (var line in BoundedLines.ReadAsync(process.StandardOutput, 1_048_576, run.Token))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    timing?.Mark("First event", first: true);
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "thread.started" && root.TryGetProperty("thread_id", out var id))
                        await sessions.SetAsync(id.GetString()!, run.Token, conversation);
                    if (type is "turn.failed" or "error")
                        throw new CodexFailure(type, CodexFailure.Classify(root.GetRawText()), sessionId is not null);
                    progress?.Invoke(root);
                    if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                        item.TryGetProperty("type", out var kind) && kind.GetString() == "agent_message" &&
                        item.TryGetProperty("text", out var text))
                    {
                        finalMessage = text.GetString();
                        timing?.Mark("Final answer");
                        if (finalMessage?.Length > 100_000) throw new InvalidDataException("Codex response exceeded 100,000 characters.");
                    }
                }
            });
            // Retain only a bounded private tail for classification; never log it.
            var diagnostic = new StringBuilder();
            var error = Guard(async () =>
            {
                var buffer = new char[2048];
                int count;
                while ((count = await process.StandardError.ReadAsync(buffer.AsMemory(), run.Token)) > 0)
                {
                    diagnostic.Append(buffer, 0, count);
                    if (diagnostic.Length > 8192) diagnostic.Remove(0, diagnostic.Length - 8192);
                }
            });
            var input = Guard(async () =>
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), run.Token);
                process.StandardInput.Close();
                timing?.Mark("Prompt sent");
            });
            try
            {
                await Task.WhenAll(output, error, input, process.WaitForExitAsync(run.Token));
                run.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new CodexFailure("process-exit", CodexFailure.Classify(diagnostic.ToString()), sessionId is not null, process.ExitCode);
                return finalMessage ?? "Codex finished without a final message.";
            }
            catch when (streamFailure is not null)
            {
                if (streamFailure is CodexFailure failure) throw failure;
                throw new CodexFailure("output-or-session-save", streamFailure is JsonException ? "invalid JSON from Codex"
                    : streamFailure is InvalidDataException ? "output exceeded limits or session data was invalid"
                    : "stream I/O or session persistence failed", sessionId is not null,
                    process.HasExited ? process.ExitCode : null, streamFailure.GetType().Name);
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    // WaitForExit only waits for the root, not its descendants. Do not report idle
                    // while a child is still processing the termination signal.
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (descendants.Any(p => p.IsAlive()))
                    {
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("A Codex descendant did not exit.");
                        await Task.Delay(20);
                    }
                }
                catch (Exception ex) { throw new FatalRunnerException("Unable to reap Codex; stopping the service to prevent overlapping tasks.", ex); }
            }
        }
        finally { timing?.Mark("Runner stopped"); lock (gate) current = null; }
    }
}

// Ubuntu-specific identity checks include start time so a recycled PID is never confused
// with a child we terminated. Zombies are already dead and await their parent's reaper.
sealed record LinuxProcess(int Id, int Parent, string Start, string State)
{
    static LinuxProcess? Read(int id)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{id}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return new LinuxProcess(id, int.Parse(fields[1]), fields[19], fields[0]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    public bool IsAlive()
    {
        var now = Read(Id);
        return now is not null && now.Start == Start && now.State is not "Z" and not "X";
    }
    public static List<LinuxProcess> Descendants(int parent)
    {
        if (!OperatingSystem.IsLinux()) return [];
        var processes = Directory.EnumerateDirectories("/proc").Select(Path.GetFileName)
            .Where(name => int.TryParse(name, out _)).Select(name => Read(int.Parse(name!)))
            .OfType<LinuxProcess>().ToArray();
        var found = new List<LinuxProcess>();
        var parents = new Queue<int>();
        parents.Enqueue(parent);
        while (parents.TryDequeue(out var id))
            foreach (var child in processes.Where(p => p.Parent == id))
            { found.Add(child); parents.Enqueue(child.Id); }
        return found;
    }
}

public static class BoundedLines
{
    public static async IAsyncEnumerable<string> ReadAsync(TextReader reader, int max,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n') { yield return line.ToString().TrimEnd('\r'); line.Clear(); }
                else
                {
                    if (line.Length >= max) throw new InvalidDataException("Codex output line exceeded its limit.");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
