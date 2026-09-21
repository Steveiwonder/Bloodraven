using System.Collections.Concurrent;
using System.Text.Json;

namespace Bloodraven;

public sealed class ApprovalBroker(Journal journal)
{
    readonly ConcurrentDictionary<string, Pending> pending = new();
    sealed record Pending(long ChatId, TaskCompletionSource<bool> Answer);
    public int Count => pending.Count;

    public bool Decide(string nonce, long chatId, bool accept) =>
        pending.TryGetValue(nonce, out var item) && item.ChatId == chatId && item.Answer.TrySetResult(accept);

    public async Task<bool> RequestAsync(long chatId, string conversation, string method, JsonElement details, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[nonce] = new Pending(chatId, answer);
        try
        {
            // Do not truncate the proposed command: approval must describe the complete operation.
            var description = details.ToString();
            if (description.Length > 2600)
            {
                await journal.ChangeAsync(d => Journal.AddReply(d, chatId,
                    "Approval declined: the proposed operation is too large to show completely in Telegram. Split the task into smaller operations."), token);
                return false;
            }
            await journal.ChangeAsync(d => d.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), chatId,
                $"Approval required · {conversation}\n{method}\n\n{description}\n\nApprove this operation once? Expires in 5 minutes. /cancel stops the task.",
                Buttons: [[new("Approve once", "approve:" + nonce), new("Decline", "decline:" + nonce)]], Plain: true)), token);
            try { return await answer.Task.WaitAsync(TimeSpan.FromMinutes(5), token); }
            catch (TimeoutException)
            {
                await journal.ChangeAsync(d => Journal.AddReply(d, chatId, "Approval expired; the proposed operation was declined."), token);
                return false;
            }
        }
        finally
        {
            pending.TryRemove(nonce, out _);
            // An unsent prompt must not outlive the operation it could approve.
            await journal.ChangeAsync(d => d.Replies.RemoveAll(r =>
                r.Buttons?.SelectMany(row => row).Any(b => b.Data == "approve:" + nonce) == true), CancellationToken.None);
        }
    }
}
