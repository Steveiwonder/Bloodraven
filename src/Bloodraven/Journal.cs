using System.Text.Json;

namespace Bloodraven;

public sealed record Attachment(string FileId, string Name, bool Image, long Size = 0);

public sealed record Job(long Id, long ChatId, string Text, bool Running = false, string Conversation = "default", Attachment[]? Attachments = null, string? ScheduleId = null, long? ProgressMessageId = null, bool ApprovalRequired = false, ModelSettings? Settings = null, string? Sandbox = null, string Engine = "codex", string? ClaudePermission = null);

public sealed record SideQuestion(long Id, long ChatId, string Question, string Conversation, string SessionId, ModelSettings Settings, bool Running = false);

public sealed record Reply(string Id, long ChatId, string Text, int Part = 0, InlineButton[][]? Buttons = null, long? EditMessageId = null, long? ProgressJobId = null, string? DocumentPath = null, bool Plain = false, long? TimingJobId = null, string? TimingKind = null);

public sealed record Schedule(string Id, long ChatId, string Prompt, string Conversation, string Timing, DateTimeOffset NextRun, bool Paused = false);

public sealed record TaskOutcome(long Id, string Conversation, string Status, DateTimeOffset Finished);

public sealed class JournalData
{
    public long Offset
    {
        get; set;
    }
    public List<Job> Jobs { get; set; } = [];
    public List<SideQuestion> SideQuestions { get; set; } = [];
    public List<Reply> Replies { get; set; } = [];
    public string ActiveConversation { get; set; } = "default";
    public List<string> Conversations { get; set; } = ["default"];
    public List<Schedule> Schedules { get; set; } = [];
    public List<TaskOutcome> Outcomes { get; set; } = [];
    public List<TimingReport> Timings { get; set; } = [];
    public bool? Approvals
    {
        get; set;
    } // Legacy default retained for existing installations.
    public Dictionary<string, string> ConversationEngines { get; set; } = [];
    public Dictionary<string, ModelSettings> ClaudeModelSettings { get; set; } = [];
    public Dictionary<string, string> ClaudePermissions { get; set; } = [];

    public string EngineFor(string conversation) => ConversationEngines.GetValueOrDefault(conversation) ?? "codex";

    public Dictionary<string, string> ConversationSandboxes { get; set; } = [];

    public string SandboxFor(string conversation, string fallback) => ConversationSandboxes.GetValueOrDefault(conversation) ?? fallback;

    public Dictionary<string, bool> ConversationApprovals { get; set; } = [];

    public bool ApprovalFor(string conversation, bool fallback) => ConversationApprovals.TryGetValue(conversation, out var enabled) ? enabled : Approvals ?? fallback;
    public Dictionary<string, ModelSettings> ModelSettings { get; set; } = [];
    public long NextScheduledId { get; set; } = -1;
}

// Offset and accepted work commit together; failed writes never advance memory.
public sealed class Journal(AppOptions options)
{
    readonly SemaphoreSlim gate = new(1);
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Capture before inspecting the snapshot so a commit between inspection and
    // waiting cannot be missed. All readers wake only after durable persistence.
    public Task Changed => Volatile.Read(ref changed).Task;

    JournalData data = new();
    string FilePath => Path.Combine(options.StateDirectory, "journal.json");

    public async Task InitializeAsync(CancellationToken token)
    {
        if (File.Exists(FilePath))
        {
            data = JsonSerializer.Deserialize<JournalData>(await File.ReadAllTextAsync(FilePath, token)) ?? throw new InvalidDataException("Invalid journal; restore it from backup instead of discarding work.");
        }

        await ChangeAsync(d =>
        {
            for (var i = 0; i < d.Jobs.Count; i++)
            {
                if (d.Jobs[i].Engine == "codex" && !d.Jobs[i].Running && d.Jobs[i].Sandbox is null && d.ApprovalFor(d.Jobs[i].Conversation, options.ApprovalDefault))
                {
                    d.Jobs[i] = d.Jobs[i] with
                    {
                        ApprovalRequired = true
                    };
                }
            }

            d.Replies.RemoveAll(r => r.Buttons?.SelectMany(row => row).Any(b => b.Data.StartsWith("approve:", StringComparison.Ordinal)) == true);

            foreach (var job in d.Jobs.Where(j => j.Running).ToArray())
            {
                AddReply(d, job.ChatId, $"Task {job.Id} was interrupted by a restart. It may have made changes; review them before sending it again. It has NOT been rerun.");
                d.Jobs.Remove(job);
                d.Outcomes.Add(new TaskOutcome(job.Id, job.Conversation, "Interrupted by restart", DateTimeOffset.UtcNow));
                var timing = d.Timings.FirstOrDefault(t => t.Id == job.Id);

                if (timing is not null)
                {
                    TaskTimings.Save(d, timing with
                    {
                        Status = "Interrupted by restart",
                        AcrossRestart = true
                    });
                }

                if (job.ProgressMessageId is > 0)
                {
                    d.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), job.ChatId, "Interrupted by restart", EditMessageId: job.ProgressMessageId));
                }
            }

            foreach (var question in d.SideQuestions.Where(q => q.Running).ToArray())
            {
                AddReply(d, question.ChatId, $"BTW · {question.Conversation}\nSide question interrupted by restart. Send /btw again to retry.");
                d.SideQuestions.Remove(question);
            }

            if (d.Outcomes.Count > 50)
            {
                d.Outcomes.RemoveRange(0, d.Outcomes.Count - 50);
            }
        }, token);
    }

    public async Task<JournalData> SnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);

        try
        {
            return Clone(data);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task ChangeAsync(Action<JournalData> change, CancellationToken token) => ChangeWithAsync(d =>
    {
        change(d);
        return Task.CompletedTask;
    }, token);

    public async Task ChangeWithAsync(Func<JournalData, Task> change, CancellationToken token)
    {
        await gate.WaitAsync(token);

        try
        {
            var next = Clone(data);

            await change(next);
            await AtomicFile.WriteAsync(FilePath, JsonSerializer.Serialize(next), token);
            data = next;
            Interlocked.Exchange(ref changed, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
        finally
        {
            gate.Release();
        }
    }

    // Records/strings are immutable; copying the lists avoids repeatedly serialising large replies just to inspect the queue.
    static JournalData Clone(JournalData value) => new()
    {
        Offset = value.Offset,
        Jobs = [.. value.Jobs],
        Replies = [.. value.Replies],
        SideQuestions = [.. value.SideQuestions],
        ActiveConversation = value.ActiveConversation,
        Conversations = [.. value.Conversations],
        Schedules = [.. value.Schedules],
        Outcomes = [.. value.Outcomes],
        Timings = [.. value.Timings],
        Approvals = value.Approvals,
        ConversationApprovals = new(value.ConversationApprovals),
        ConversationSandboxes = new(value.ConversationSandboxes),
        ConversationEngines = new(value.ConversationEngines),
        ClaudeModelSettings = new(value.ClaudeModelSettings),
        ClaudePermissions = new(value.ClaudePermissions),
        NextScheduledId = value.NextScheduledId,
        ModelSettings = new(value.ModelSettings)
    };

    public static void AddReply(JournalData data, long chatId, string text) => data.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), chatId, text));
}

public static class AtomicFile
{
    public static async Task WriteAsync(string path, string text, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";

        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), token);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, true);
    }
}

public sealed class SessionStore(AppOptions options)
{
    readonly SemaphoreSlim gate = new(1);
    static readonly System.Text.RegularExpressions.Regex NamePattern = new("^[a-z0-9][a-z0-9_-]{0,31}\\z");

    public static bool ValidName(string name) => NamePattern.IsMatch(name);

    string FilePath(string name, bool approved, string engine = "codex")
    {
        if (!ValidName(name))
        {
            throw new InvalidDataException("Conversation names use 1–32 lowercase letters, digits, underscores or hyphens.");
        }

        Engines.Validate(engine);
        if (engine == "claude")
        {
            return Path.Combine(options.StateDirectory, "sessions", name + ".claude.txt");
        }

        return name == "default" && !approved ? Path.Combine(options.StateDirectory, "session.txt") : Path.Combine(options.StateDirectory, "sessions", name + (approved ? ".approved" : "") + ".txt");
    }

    public async Task<string?> GetAsync(CancellationToken token, string name = "default", bool approved = false, string engine = "codex")
    {
        await gate.WaitAsync(token);

        try
        {
            var path = FilePath(name, approved, engine);

            if (!File.Exists(path))
            {
                return null;
            }

            var id = (await File.ReadAllTextAsync(path, token)).Trim();

            return ValidId(id) ? id : throw new InvalidDataException("Invalid saved session ID. Use /new to reset it.");
        }
        finally
        {
            gate.Release();
        }
    }

    static bool ValidId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}\\z");

    public async Task SetAsync(string id, CancellationToken token, string name = "default", bool approved = false, string engine = "codex")
    {
        if (!ValidId(id))
        {
            throw new InvalidDataException("CLI returned an invalid session ID.");
        }

        await gate.WaitAsync(token);

        try
        {
            await AtomicFile.WriteAsync(FilePath(name, approved, engine), id, token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken token, string name = "default", string engine = "codex")
    {
        await gate.WaitAsync(token);

        try
        {
            foreach (var path in new[]
            {
                FilePath(name, false, engine),
                FilePath(name, true, engine)
            })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
