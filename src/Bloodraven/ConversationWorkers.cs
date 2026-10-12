namespace Bloodraven;

// Each conversation has one sequential consumer. New conversations start without
// waiting for existing work; all consumers are supervised and drained on shutdown.
public static class ConversationWorkers
{
    public static async Task RunAsync(Journal journal, Func<string, CancellationToken, Task> consume, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workers = new Dictionary<string, Task>();

        try
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var changed = journal.Changed;
                var state = await journal.SnapshotAsync(lifetime.Token);

                foreach (var name in state.Conversations.Concat(state.Jobs.Select(j => j.Conversation)).Concat(state.SideQuestions.Select(q => q.Conversation)).Distinct())
                {
                    if (!workers.ContainsKey(name))
                    {
                        workers.Add(name, consume(name, lifetime.Token));
                    }
                }

                var finished = await Task.WhenAny(workers.Values.Append(changed.WaitAsync(lifetime.Token)));

                await finished; // A failed runner stops the service; never leave it unobserved.
                if (workers.Values.Contains(finished))
                {
                    throw new InvalidOperationException("A conversation worker stopped unexpectedly.");
                }
            }
        }
        finally
        {
            lifetime.Cancel();

            try
            {
                await Task.WhenAll(workers.Values);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }
}
