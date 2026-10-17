using System.Diagnostics;
using System.Text.Json;

namespace Bloodraven;

// Adapt Claude's public print-mode stream to the existing bounded runner's events.
// Session persistence, cancellation, process reaping and secret filtering stay shared.
public static class ClaudeProtocol
{
    public static void Configure(ProcessStartInfo start, string? session, ModelSettings settings, string permission, bool images)
    {
        settings.Validate();
        if (!Engines.ValidClaudePermission(permission))
        {
            throw new InvalidDataException("Invalid saved Claude permission mode.");
        }

        foreach (var arg in new[] { "--print", "--verbose", "--output-format", "stream-json", "--permission-mode", permission })
        {
            start.ArgumentList.Add(arg);
        }

        if (session is not null)
        {
            start.ArgumentList.Add("--resume");
            start.ArgumentList.Add(session);
        }

        if (settings.Model is not null)
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(settings.Model);
        }

        if (settings.Effort is not null)
        {
            start.ArgumentList.Add("--effort");
            start.ArgumentList.Add(settings.Effort);
        }

        if (images)
        {
            start.ArgumentList.Add("--input-format");
            start.ArgumentList.Add("stream-json");
        }
    }

    public static async Task<string> InputAsync(string prompt, IReadOnlyList<string> images, CancellationToken token)
    {
        if (images.Count == 0)
        {
            return prompt;
        }

        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var path in images)
        {
            var media = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => throw new ArgumentException("Unsupported Claude image type.")
            };

            if (new FileInfo(path).Length > 5 * 1024 * 1024)
            {
                throw new ArgumentException("Claude images must be 5 MB or smaller.");
            }

            content.Add(new { type = "image", source = new { type = "base64", media_type = media, data = Convert.ToBase64String(await File.ReadAllBytesAsync(path, token)) } });
        }

        return JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } }) + "\n";
    }

    public static JsonElement Normalize(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        if (type == "system" && root.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "init" && root.TryGetProperty("session_id", out var id))
        {
            return JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = id.GetString() });
        }

        if (type == "result")
        {
            if (!root.TryGetProperty("subtype", out var resultKind) || resultKind.GetString() != "success" || root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                return JsonSerializer.SerializeToElement(new { type = "error", error = root });
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Claude returned no text result.");
            }

            return JsonSerializer.SerializeToElement(new { type = "item.completed", item = new { type = "agent_message", text = result.GetString() } });
        }

        if (type == "assistant" && root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(c => c.TryGetProperty("type", out var block) && block.GetString() == "tool_use"))
        {
            // Do not forward reasoning, raw tool inputs or intermediary answers.
            return JsonSerializer.SerializeToElement(new { type = "item.started", item = new { type = "mcp_tool_call" } });
        }

        return JsonSerializer.SerializeToElement(new { type = "ignored" });
    }
}
