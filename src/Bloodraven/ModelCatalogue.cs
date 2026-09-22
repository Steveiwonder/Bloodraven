using System.Diagnostics;
using System.Text.Json;

namespace Bloodraven;

public sealed record AvailableModel(string Id, string[] Efforts);

public static class ModelCatalogue
{
    // Discovery never starts a thread or a model turn.
    public static async Task<IReadOnlyList<AvailableModel>> ReadAsync(AppOptions options, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var start = new ProcessStartInfo(options.CodexExecutable) {
            WorkingDirectory = options.WorkingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("app-server");
        options.RemoveBridgeSecrets(start);
        using var process = Process.Start(start) ?? throw new IOException("Could not start catalogue discovery.");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await using var lines = BoundedLines.ReadAsync(process.StandardOutput, 1_048_576, token).GetAsyncEnumerator(token);
            var sequence = 0;
            async Task Write(object value)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
            async Task<JsonElement> Request(string method, object parameters)
            {
                var id = ++sequence;
                await Write(new { id, method, @params = parameters });
                while (await lines.MoveNextAsync())
                {
                    using var doc = JsonDocument.Parse(lines.Current);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var incoming))
                        await Write(new { id = incoming.Clone(), error = new { code = -32601, message = "Unsupported during discovery" } });
                    else if (root.TryGetProperty("id", out var response) && response.ValueKind == JsonValueKind.Number && response.GetInt32() == id)
                    {
                        if (root.TryGetProperty("error", out _)) throw new IOException("Catalogue request rejected.");
                        return root.GetProperty("result").Clone();
                    }
                }
                throw new IOException("Catalogue connection closed.");
            }
            await Request("initialize", new { clientInfo = new { name = "bloodraven", version = BotWorker.Version } });
            await Write(new { method = "initialized", @params = new { } });
            var models = new Dictionary<string, AvailableModel>();
            var cursors = new HashSet<string>();
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var result = await Request("model/list", new { limit = 20, cursor, includeHidden = false });
                foreach (var model in result.GetProperty("data").EnumerateArray())
                {
                    var id = model.GetProperty("model").GetString();
                    if (id is null || !ModelSettings.ValidModel(id) || id == "default") continue;
                    if (model.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                    var efforts = model.TryGetProperty("supportedReasoningEfforts", out var levels)
                        ? levels.EnumerateArray().Select(e => e.GetProperty("reasoningEffort").GetString())
                            .Where(e => e is not null && ModelSettings.ValidEffort(e)).Select(e => e!).Distinct().ToArray() : [];
                    models[id] = new AvailableModel(id, efforts);
                    if (models.Count > 200) throw new IOException("Catalogue too large.");
                }
                cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
                if (string.IsNullOrEmpty(cursor)) return models.Values.ToArray();
                if (!cursors.Add(cursor)) throw new IOException("Repeated catalogue page.");
            }
            throw new IOException("Too many catalogue pages.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }
}
