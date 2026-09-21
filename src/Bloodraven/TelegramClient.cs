using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bloodraven;

public sealed class TelegramException(int status, int retryAfter = 5, bool formatting = false)
    : Exception($"Telegram request failed (status {status}).")
{
    public int Status { get; } = status;
    public int RetryAfter { get; } = Math.Clamp(retryAfter, 1, 3600);
    public bool Formatting { get; } = formatting;
}

public sealed class TelegramClient(HttpClient http, AppOptions options)
{
    public async Task CheckAsync(CancellationToken token) => await CallAsync<JsonElement>("getMe", new { }, token);
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken token) =>
        await CallAsync<List<TelegramUpdate>>("getUpdates", new { offset, timeout = 30, limit = 20, allowed_updates = new[] { "message", "callback_query" } }, token) ?? [];
    public async Task<long> SendAsync(long chatId, FormattedMessage message, CancellationToken token,
        InlineButton[][]? buttons = null, long? editMessageId = null)
    {
        var method = editMessageId.HasValue ? "editMessageText" : "sendMessage";
        JsonElement result;
        try
        {
            result = await CallAsync<JsonElement>(method, new { chat_id = chatId, message_id = editMessageId,
                reply_markup = buttons is null ? null : new { inline_keyboard = buttons }, text = message.Html,
                parse_mode = "HTML", link_preview_options = new { is_disabled = true } }, token);
        }
        catch (TelegramException ex) when (ex.Formatting)
        {
            // A parser rejection must not prevent the answer from reaching its owner.
            result = await CallAsync<JsonElement>(method, new { chat_id = chatId, message_id = editMessageId,
                reply_markup = buttons is null ? null : new { inline_keyboard = buttons }, text = message.Plain,
                link_preview_options = new { is_disabled = true } }, token);
        }
        return editMessageId ?? (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0);
    }
    public async Task AnswerCallbackAsync(string id, string text, CancellationToken token) =>
        await CallAsync<JsonElement>("answerCallbackQuery", new { callback_query_id = id, text }, token);

    public const long FileLimit = 10 * 1024 * 1024;
    public async Task DownloadAsync(string fileId, string destination, CancellationToken token)
    {
        var file = await CallAsync<TelegramFile>("getFile", new { file_id = fileId }, token)
            ?? throw new InvalidDataException("Telegram returned no file.");
        if (file.FileSize > FileLimit || string.IsNullOrEmpty(file.FilePath) ||
            file.FilePath.Split('/').Any(p => p is ".." or "." || p.Contains('\\')) ||
            file.FilePath.StartsWith('/')) throw new InvalidDataException("Unsupported attachment path or size.");
        try
        {
            using var response = await http.GetAsync($"https://api.telegram.org/file/bot{options.TelegramBotToken}/{file.FilePath}", HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new TelegramException((int)response.StatusCode);
            if (response.Content.Headers.ContentLength > FileLimit) throw new InvalidDataException("Attachment exceeds 10 MiB.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var buffer = new byte[8192]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                total += count;
                if (total > FileLimit) throw new InvalidDataException("Attachment exceeds 10 MiB.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TelegramException(408); }
        catch (HttpRequestException) { throw new TelegramException(503); }
    }

    public async Task SendDocumentAsync(long chatId, string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > FileLimit) throw new InvalidDataException("File missing or larger than 10 MiB.");
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        body.Add(new StreamContent(File.OpenRead(path)), "document", Path.GetFileName(path));
        try
        {
            using var response = await http.PostAsync($"https://api.telegram.org/bot{options.TelegramBotToken}/sendDocument", body, token);
            var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<JsonElement>>(cancellationToken: token);
            if (!response.IsSuccessStatusCode || envelope?.Ok != true)
                throw new TelegramException(envelope?.ErrorCode ?? (int)response.StatusCode, envelope?.Parameters?.RetryAfter ?? 5);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TelegramException(408); }
        catch (HttpRequestException) { throw new TelegramException(503); }
        catch (JsonException) { throw new TelegramException(502); }
    }
    async Task<T?> CallAsync<T>(string method, object body, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{options.TelegramBotToken}/{method}", body, token);
            var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<T>>(cancellationToken: token);
            if (!response.IsSuccessStatusCode || envelope?.Ok != true)
            {
                if (method == "editMessageText" && envelope?.Description?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true)
                    return default;
                var code = envelope?.ErrorCode ?? (int)response.StatusCode;
                var formatting = code == 400 && (envelope?.Description?.Contains("parse entities", StringComparison.OrdinalIgnoreCase) == true);
                throw new TelegramException(code, envelope?.Parameters?.RetryAfter ?? (code is 401 or 403 or 409 ? 60 : 5), formatting);
            }
            return envelope.Result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TelegramException(408); }
        catch (HttpRequestException) { throw new TelegramException(503); }
        catch (JsonException) { throw new TelegramException(502); }
    }
}
public sealed record TelegramEnvelope<T>(bool Ok, T? Result, string? Description,
    [property: JsonPropertyName("error_code")] int? ErrorCode,
    TelegramParameters? Parameters);
public sealed record TelegramParameters([property: JsonPropertyName("retry_after")] int RetryAfter);
public sealed record TelegramUpdate([property: JsonPropertyName("update_id")] long UpdateId, TelegramMessage? Message,
    [property: JsonPropertyName("callback_query")] TelegramCallback? Callback = null);
public sealed record TelegramMessage(TelegramUser? From, TelegramChat Chat, string? Text,
    [property: JsonPropertyName("message_id")] long MessageId = 0, string? Caption = null,
    TelegramDocument? Document = null, TelegramPhoto[]? Photo = null);
public sealed record TelegramCallback(string Id, TelegramUser From, TelegramMessage? Message, string? Data);
public sealed record InlineButton([property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("callback_data")] string Data);
public sealed record TelegramDocument([property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("mime_type")] string? MimeType,
    [property: JsonPropertyName("file_size")] long FileSize = 0);
public sealed record TelegramPhoto([property: JsonPropertyName("file_id")] string FileId, int Width, int Height,
    [property: JsonPropertyName("file_size")] long FileSize = 0);
public sealed record TelegramFile([property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("file_size")] long FileSize = 0);
public sealed record TelegramUser(long Id);
public sealed record TelegramChat(long Id, string Type);
