using System.Text.Json;

namespace Bloodraven;

public sealed record Job(long Id, long ChatId, string Text, bool Running = false);
public sealed record Reply(string Id, long ChatId, string Text, int Part = 0);
public sealed class JournalData
{
    public long Offset { get; set; }
    public List<Job> Jobs { get; set; } = [];
    public List<Reply> Replies { get; set; } = [];
}

// Offset and accepted work commit together; failed writes never advance memory.
public sealed class Journal(AppOptions options)
{
    readonly SemaphoreSlim gate = new(1);
    JournalData data = new();
    string FilePath => Path.Combine(options.StateDirectory, "journal.json");
    public async Task InitializeAsync(CancellationToken token)
    {
        if (File.Exists(FilePath))
            data = JsonSerializer.Deserialize<JournalData>(await File.ReadAllTextAsync(FilePath, token))
                ?? throw new InvalidDataException("Invalid journal; restore it from backup instead of discarding work.");
        await ChangeAsync(d =>
        {
            foreach (var job in d.Jobs.Where(j => j.Running).ToArray())
            {
                AddReply(d, job.ChatId, $"Task {job.Id} was interrupted by a restart. It may have made changes; review them before sending it again. It has NOT been rerun.");
                d.Jobs.Remove(job);
            }
        }, token);
    }
    public async Task<JournalData> SnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Clone(data); }
        finally { gate.Release(); }
    }
    public async Task ChangeAsync(Action<JournalData> change, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var next = Clone(data);
            change(next);
            await AtomicFile.WriteAsync(FilePath, JsonSerializer.Serialize(next), token);
            data = next;
        }
        finally { gate.Release(); }
    }
    // Records/strings are immutable; copying the lists avoids repeatedly serialising large replies just to inspect the queue.
    static JournalData Clone(JournalData value) => new() { Offset = value.Offset, Jobs = [.. value.Jobs], Replies = [.. value.Replies] };
    public static void AddReply(JournalData data, long chatId, string text) =>
        data.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), chatId, text));
}

public static class AtomicFile
{
    public static async Task WriteAsync(string path, string text, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), token);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }
}

public sealed class SessionStore(AppOptions options)
{
    readonly SemaphoreSlim gate = new(1);
    string FilePath => Path.Combine(options.StateDirectory, "session.txt");
    public async Task<string?> GetAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!File.Exists(FilePath)) return null;
            var id = (await File.ReadAllTextAsync(FilePath, token)).Trim();
            return Guid.TryParse(id, out _) ? id : throw new InvalidDataException("Invalid saved session ID. Use /new to reset it.");
        }
        finally { gate.Release(); }
    }
    public async Task SetAsync(string id, CancellationToken token)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("Codex returned an invalid session ID.");
        await gate.WaitAsync(token);
        try { await AtomicFile.WriteAsync(FilePath, id, token); }
        finally { gate.Release(); }
    }
    public async Task ClearAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { File.Delete(FilePath); }
        finally { gate.Release(); }
    }
}
