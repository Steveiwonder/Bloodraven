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

    public async Task<string> RunAsync(string prompt, CancellationToken stoppingToken)
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
            var sessionId = await sessions.GetAsync(run.Token);
            var start = new ProcessStartInfo(options.CodexExecutable)
            {
                WorkingDirectory = options.WorkingDirectory, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            options.RemoveBridgeSecrets(start);
            foreach (var arg in new[] { "-C", options.WorkingDirectory, "exec", "--json", "--sandbox", options.Sandbox })
                start.ArgumentList.Add(arg);
            if (sessionId is not null)
            {
                start.ArgumentList.Add("resume");
                start.ArgumentList.Add(sessionId);
            }
            start.ArgumentList.Add("-"); // Prompts are stdin data, never CLI options.
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex.");
            using var registration = run.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
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
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "thread.started" && root.TryGetProperty("thread_id", out var id))
                        await sessions.SetAsync(id.GetString()!, run.Token);
                    if (type is "turn.failed" or "error") throw new InvalidOperationException("Codex reported a failed turn.");
                    if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                        item.TryGetProperty("type", out var kind) && kind.GetString() == "agent_message" &&
                        item.TryGetProperty("text", out var text))
                    {
                        finalMessage = text.GetString();
                        if (finalMessage?.Length > 100_000) throw new InvalidDataException("Codex response exceeded 100,000 characters.");
                    }
                }
            });
            // Drain stderr without retaining or relaying potentially sensitive diagnostics.
            var error = Guard(() => process.StandardError.BaseStream.CopyToAsync(Stream.Null, run.Token));
            var input = Guard(async () =>
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), run.Token);
                process.StandardInput.Close();
            });
            try
            {
                await Task.WhenAll(output, error, input, process.WaitForExitAsync(run.Token));
                run.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException($"Codex exited with code {process.ExitCode}. Check Codex login and configuration locally.");
                return finalMessage ?? "Codex finished without a final message.";
            }
            catch when (streamFailure is not null)
            {
                throw new InvalidOperationException("Codex output or session persistence failed. Review the repository before retrying.");
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) { throw new FatalRunnerException("Unable to reap Codex; stopping the service to prevent overlapping tasks.", ex); }
            }
        }
        finally { lock (gate) current = null; }
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
