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
        await CallAsync<List<TelegramUpdate>>("getUpdates", new { offset, timeout = 30, limit = 20, allowed_updates = new[] { "message" } }, token) ?? [];
    public async Task SendAsync(long chatId, FormattedMessage message, CancellationToken token)
    {
        try
        {
            await CallAsync<JsonElement>("sendMessage", new { chat_id = chatId, text = message.Html,
                parse_mode = "HTML", link_preview_options = new { is_disabled = true } }, token);
        }
        catch (TelegramException ex) when (ex.Formatting)
        {
            // A parser rejection must not prevent the answer from reaching its owner.
            await CallAsync<JsonElement>("sendMessage", new { chat_id = chatId, text = message.Plain,
                link_preview_options = new { is_disabled = true } }, token);
        }
    }
    async Task<T?> CallAsync<T>(string method, object body, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{options.TelegramBotToken}/{method}", body, token);
            var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<T>>(cancellationToken: token);
            if (!response.IsSuccessStatusCode || envelope?.Ok != true)
            {
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
public sealed record TelegramUpdate([property: JsonPropertyName("update_id")] long UpdateId, TelegramMessage? Message);
public sealed record TelegramMessage(TelegramUser? From, TelegramChat Chat, string? Text);
public sealed record TelegramUser(long Id);
public sealed record TelegramChat(long Id, string Type);
