using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bloodraven;

public sealed record TimingPoint(string Name, DateTimeOffset At, double Milliseconds);
public sealed record TimingReport(long Id, long ChatId, string Conversation, DateTimeOffset Received,
    long TelegramDate, string Model, string Effort, string Mode, string Status,
    TimingPoint[] Points, double SendWaitMs = 0, double SendHttpMs = 0, int Attempts = 0,
    int Parts = 0, double ApprovalMs = 0, bool AcrossRestart = false, string Version = "unknown");

// Live event recording is in memory. Snapshots ride the existing journal writes;
// timing every event must not add disk flushes to the work being measured.
public sealed class TaskTimings(TimingReport initial)
{
    readonly object gate = new();
    readonly long origin = Stopwatch.GetTimestamp();
    readonly double offset = initial.Points.LastOrDefault()?.Milliseconds ?? 0;
    TimingReport report = initial;
    public TimingReport Snapshot() { lock (gate) return report with { Points = [.. report.Points] }; }
    public void Mark(string name, bool first = false)
    {
        lock (gate)
        {
            if (first && report.Points.Any(p => p.Name == name)) return;
            var point = new TimingPoint(name, DateTimeOffset.UtcNow, offset + Stopwatch.GetElapsedTime(origin).TotalMilliseconds);
            report = report with { Points = [.. report.Points.Where(p => p.Name != name), point] };
        }
    }
    public void Status(string value) { lock (gate) report = report with { Status = value }; }
    public void Delivery(double wait, double http, bool success)
    {
        lock (gate) report = report with { SendWaitMs = report.SendWaitMs + wait, SendHttpMs = report.SendHttpMs + http,
            Attempts = report.Attempts + 1, Parts = report.Parts + (success ? 1 : 0) };
    }
    public void Approval(double ms) { lock (gate) report = report with { ApprovalMs = report.ApprovalMs + ms }; }
    public static void Save(JournalData data, TimingReport report)
    {
        data.Timings.RemoveAll(t => t.Id == report.Id);
        data.Timings.Add(report);
        // Keep the latest 50 reports plus any still needed by pending work/replies.
        foreach (var old in data.Timings.OrderByDescending(t => t.Received).Skip(50).ToArray())
            if (!data.Jobs.Any(j => j.Id == old.Id) && !data.Replies.Any(r => r.TimingJobId == old.Id)) data.Timings.Remove(old);
    }
    public static string Format(TimingReport r)
    {
        string Duration(double ms) => ms < 1000 ? $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms"
            : $"{(ms / 1000).ToString("0.000", CultureInfo.InvariantCulture)} s";
        TimingPoint? Point(string name) => r.Points.FirstOrDefault(p => p.Name == name);
        var b = new StringBuilder($"Timings · task {r.Id}\nConversation: {r.Conversation}\nStatus: {r.Status}\nBloodraven {r.Version} · {r.Mode}\nModel: {r.Model} · effort: {r.Effort}\n\n");
        DateTimeOffset? sent = r.TelegramDate is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(r.TelegramDate) : null;
        b.AppendLine($"Telegram timestamp: {(sent is null ? "unavailable" : sent.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))}");
        b.AppendLine($"Bridge received: {r.Received:HH:mm:ss.fff} UTC");
        if (sent is not null) b.AppendLine($"Telegram → bridge: ~{Duration((r.Received - sent.Value).TotalMilliseconds)}");
        void Span(string label, string from, string to)
        {
            var a = Point(from); var z = Point(to);
            if (a is not null && z is not null) b.AppendLine($"{label}: {Duration(Math.Max(0, z.Milliseconds - a.Milliseconds))}");
        }
        b.AppendLine("\nMeasured stages:");
        Span("Receive → queue accepted", "Received", "Queued");
        Span("Queue wait (includes saving queue)", "Queued", "Started");
        Span("Task setup", "Started", "Attachments started");
        Span("Attachments/download", "Attachments started", "Attachments ready");
        Span("Runner/session setup", "Attachments ready", "Process started");
        Span("CLI startup → first JSON event", "Process started", "First event");
        Span("App-server initialization", "Process started", "Initialized");
        Span("App-server session setup", "Initialized", "Session ready");
        Span("Prompt submission", "Session ready", "Prompt sent");
        Span("Prompt sent → final answer event", "Prompt sent", "Final answer");
        Span("Final answer → runner stopped", "Final answer", "Runner stopped");
        Span("Runner cleanup → reply queued", "Runner stopped", "Reply ready");
        Span("Reply queue wait", "Reply ready", "Delivery started");
        Span("Full final reply delivery", "Delivery started", "Delivered");
        b.AppendLine($"Final delivery pacing/lock waits: {Duration(r.SendWaitMs)}");
        b.AppendLine($"Final Telegram send requests: {Duration(r.SendHttpMs)} ({r.Attempts} attempts, {r.Parts} parts accepted; includes any format fallback)");
        if (r.ApprovalMs > 0) b.AppendLine($"Approval waits: {Duration(r.ApprovalMs)} (included in Codex time)");
        b.AppendLine("\nTimeline (offset from bridge receipt):");
        foreach (var p in r.Points.OrderBy(p => p.Milliseconds)) b.AppendLine($"+{Duration(p.Milliseconds)} · {p.Name}");
        if (Point("Delivered") is { } end)
        {
            b.AppendLine($"\nTelegram accepted final reply: {end.At:HH:mm:ss.fff} UTC");
            b.AppendLine($"Bridge total: {Duration(end.Milliseconds)}");
            if (sent is not null) b.AppendLine($"Telegram timestamp → final acceptance: ~{Duration((end.At - sent.Value).TotalMilliseconds)}");
        }
        else b.AppendLine("\nIncomplete: final delivery has not been recorded.");
        b.AppendLine("\nTelegram's timestamp has 1-second precision; cross-server timing assumes synchronized clocks. Acceptance is not phone display/read time. Codex time includes network, model and tools; those cannot be separated from CLI events. Missing stages were not observed.");
        if (r.AcrossRestart) b.AppendLine("Includes a restart: the gap across restart uses the host clock.");
        return b.ToString();
    }
}
