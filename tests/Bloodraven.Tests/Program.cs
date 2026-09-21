using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Bloodraven;
using Microsoft.Extensions.Logging.Abstractions;

var tests = new (string Name, Func<Task> Run)[]
{
    ("formatter bold, lists, escaping and code", Formatting),
    ("formatter links, limits, emoji and fallback text", FormattingLimits),
    ("configuration fails closed and private chat is required", Configuration),
    ("journal preserves queued work and does not replay interrupted work", Recovery),
    ("journal failed writes do not acknowledge updates", FailedWrite),
    ("bounded JSONL input", Bounded),
    ("Codex stdin, secret removal, cwd and resume", Runner),
    ("Codex cancellation kills process tree", Cancellation),
    ("Codex parser failure kills process tree", Malformed),
    ("Codex oversized output and stderr failures are bounded and sanitised", RunnerFailures),
    ("Telegram HTML rejection falls back to plain text", HtmlFallback),
    ("Telegram rate limits and network exceptions are sanitised", TelegramErrors),
    ("worker survives delivery outage and executes work once", WorkerOutage),
    ("progress is bounded, selective, and stops on cancellation", Progress),
    ("worker sends progress before the final answer", WorkerProgress),
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} regression tests passed.");
return failures == 0 ? 0 : 1;

static void Assert(bool condition, string message = "Assertion failed")
{ if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static Task Formatting()
{
    var parts = TelegramFormatter.Format("Yes—**all containers are running**.\n- **comfyui, ollama** — stopped.\n`a < b && c`\n<script>x</script>\n```cs\nvar x = 1 < 2;\n```");
    var html = string.Concat(parts.Select(p => p.Html));
    Assert(html.Contains("<b>all containers are running</b>"));
    Assert(html.Contains("• <b>comfyui, ollama</b>"));
    Assert(html.Contains("<code>a &lt; b &amp;&amp; c</code>"));
    Assert(!html.Contains("<script>"));
    Assert(html.Contains("<pre>"));
    foreach (var p in parts) _ = XElement.Parse("<root>" + p.Html + "</root>");
    return Task.CompletedTask;
}
static Task FormattingLimits()
{
    var source = "**" + new string('a', 3499) + "😀" + new string('b', 4000) + "**";
    var parts = TelegramFormatter.Format(source);
    Assert(string.Concat(parts.Select(p => p.Plain)) == source[2..^2]);
    foreach (var part in parts)
    {
        Assert(part.Plain.Length <= 3500);
        Assert(!char.IsHighSurrogate(part.Plain[^1]) && !char.IsLowSurrogate(part.Plain[0]));
        _ = XElement.Parse("<root>" + part.Html + "</root>");
    }
    Assert(TelegramFormatter.Format("[docs](https://example.com/?a=1&b=2)")[0].Html.Contains("<a href="));
    Assert(!TelegramFormatter.Format("[bad](javascript:alert(1))")[0].Html.Contains("<a"));
    Assert(TelegramFormatter.Format("   ").Count == 1);
    Assert(TelegramFormatter.Format("unclosed **bold")[0].Plain == "unclosed **bold");
    Assert(BotWorker.Command("/status@my_bot\nanything") == "/status");
    return Task.CompletedTask;
}
static async Task Configuration()
{
    using var fixture = new Fixture();
    var o = fixture.Options;
    Assert(o.ProgressIntervalSeconds == 30);
    foreach (var value in new[] { "0", "10", "3600" })
    {
        Environment.SetEnvironmentVariable("BLOODRAVEN_PROGRESS_INTERVAL_SECONDS", value);
        Assert(new AppOptions().ProgressIntervalSeconds == int.Parse(value));
    }
    foreach (var value in new[] { "-1", "1", "3601", "oops" })
    {
        Environment.SetEnvironmentVariable("BLOODRAVEN_PROGRESS_INTERVAL_SECONDS", value);
        await Throws<InvalidOperationException>(() => { _ = new AppOptions(); return Task.CompletedTask; });
    }
    Environment.SetEnvironmentVariable("BLOODRAVEN_PROGRESS_INTERVAL_SECONDS", null);
    Assert(o.Accepts(new TelegramMessage(new TelegramUser(123), new TelegramChat(123, "private"), "hello")));
    Assert(!o.Accepts(new TelegramMessage(new TelegramUser(123), new TelegramChat(123, "group"), "hello")));
    Assert(!o.Accepts(new TelegramMessage(new TelegramUser(999), new TelegramChat(123, "private"), "hello")));
    Environment.SetEnvironmentVariable("BLOODRAVEN_ALLOWED_CHAT_ID", "typo");
    await Throws<InvalidOperationException>(() => { _ = new AppOptions(); return Task.CompletedTask; });
}
static async Task Recovery()
{
    using var fixture = new Fixture();
    var journal = new Journal(fixture.Options);
    await journal.InitializeAsync(default);
    await journal.ChangeAsync(d => { d.Offset = 11; d.Jobs.Add(new Job(9, 123, "running", true)); d.Jobs.Add(new Job(10, 123, "queued")); }, default);
    var recovered = new Journal(fixture.Options);
    await recovered.InitializeAsync(default);
    var state = await recovered.SnapshotAsync(default);
    Assert(state.Offset == 11 && state.Jobs.Count == 1 && state.Jobs[0].Id == 10);
    Assert(state.Replies.Single().Text.Contains("NOT been rerun"));
    var again = new Journal(fixture.Options);
    await again.InitializeAsync(default);
    Assert((await again.SnapshotAsync(default)).Replies.Count == 1);
    var sessions = new SessionStore(fixture.Options);
    await sessions.SetAsync("11111111-1111-1111-1111-111111111111", default);
    Assert(await new SessionStore(fixture.Options).GetAsync(default) is not null);
    await sessions.ClearAsync(default);
    Assert(await sessions.GetAsync(default) is null);
}
static async Task FailedWrite()
{
    using var fixture = new Fixture();
    var journal = new Journal(fixture.Options);
    await journal.InitializeAsync(default);
    Directory.CreateDirectory(Path.Combine(fixture.Options.StateDirectory, "journal.json.tmp"));
    await Throws<UnauthorizedAccessException>(() => journal.ChangeAsync(d => d.Offset = 99, default));
    Assert((await journal.SnapshotAsync(default)).Offset == 0);
}
static async Task Bounded()
{
    await Throws<InvalidDataException>(async () =>
    { await foreach (var line in BoundedLines.ReadAsync(new StringReader(new string('x', 101)), 100, default)) _ = line; });
    var lines = new List<string>();
    await foreach (var line in BoundedLines.ReadAsync(new StringReader("one\r\ntwo"), 100, default)) lines.Add(line);
    Assert(lines.SequenceEqual(new[] { "one", "two" }));
}
static async Task Runner()
{
    using var fixture = new Fixture();
    var runner = new CodexRunner(fixture.Options, new SessionStore(fixture.Options));
    var activity = new TaskProgress(fixture.Options.TelegramBotToken);
    using var result = JsonDocument.Parse(await runner.RunAsync("--help\n$(not-a-shell)", default, activity.Observe));
    Assert(activity.TakeUpdate().Contains("prompt"));
    Assert(result.RootElement.GetProperty("prompt").GetString() == "--help\n$(not-a-shell)");
    Assert(result.RootElement.GetProperty("secret").ValueKind == JsonValueKind.Null);
    Assert(result.RootElement.GetProperty("cwd").GetString() == fixture.Root);
    using var resumed = JsonDocument.Parse(await runner.RunAsync("follow up", default));
    Assert(resumed.RootElement.GetProperty("args").EnumerateArray().Any(a => a.GetString() == "resume"));
    Assert(!runner.IsRunning);
}
static Task Cancellation() => TreeFailure("sleep", true);
static Task Malformed() => TreeFailure("malformed", false);
static async Task TreeFailure(string mode, bool cancel)
{
    using var fixture = new Fixture();
    Environment.SetEnvironmentVariable("TEST_CODEX_MODE", mode);
    var pidFile = Path.Combine(fixture.Root, "pids");
    Environment.SetEnvironmentVariable("TEST_PID_FILE", pidFile);
    var runner = new CodexRunner(fixture.Options, new SessionStore(fixture.Options));
    var task = runner.RunAsync("test", default);
    for (var i = 0; i < 100 && !File.Exists(pidFile); i++) await Task.Delay(25);
    Assert(File.Exists(pidFile));
    if (cancel) { runner.Cancel(); await Throws<OperationCanceledException>(() => task); }
    else await Throws<InvalidOperationException>(() => task);
    Assert(!runner.IsRunning);
    foreach (var pid in File.ReadAllText(pidFile).Split(' ').Select(int.Parse))
    {
        var stat = $"/proc/{pid}/stat";
        Assert(!File.Exists(stat) || File.ReadAllText(stat).Split(' ')[2] == "Z", "Child process survived cleanup");
    }
}
static async Task RunnerFailures()
{
    using var fixture = new Fixture();
    foreach (var mode in new[] { "oversized", "failure" })
    {
        Environment.SetEnvironmentVariable("TEST_CODEX_MODE", mode);
        var runner = new CodexRunner(fixture.Options, new SessionStore(fixture.Options));
        await Throws<InvalidOperationException>(() => runner.RunAsync("test", default));
        Assert(!runner.IsRunning);
    }
}
static async Task HtmlFallback()
{
    using var fixture = new Fixture();
    var bodies = new List<string>();
    using var http = new HttpClient(new Handler(async (request, token) =>
    {
        bodies.Add(await request.Content!.ReadAsStringAsync(token));
        return bodies.Count == 1 ? Response(400, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: can't parse entities\"}")
            : Response(200, "{\"ok\":true,\"result\":{}}");
    }));
    await new TelegramClient(http, fixture.Options).SendAsync(123, new FormattedMessage("<b>hi</b>", "hi"), default);
    Assert(bodies[0].Contains("parse_mode") && !bodies[1].Contains("parse_mode"));
}
static async Task TelegramErrors()
{
    using var fixture = new Fixture();
    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(429,
        "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":7}}"))));
    try { await new TelegramClient(http, fixture.Options).CheckAsync(default); throw new Exception("Expected rate limit"); }
    catch (TelegramException ex) { Assert(ex.RetryAfter == 7 && ex.Status == 429); }
    using var broken = new HttpClient(new Handler((_, _) => throw new HttpRequestException("url-with-private-token")));
    try { await new TelegramClient(broken, fixture.Options).CheckAsync(default); }
    catch (TelegramException ex) { Assert(!ex.ToString().Contains("private-token")); return; }
    throw new Exception("Expected network error");
}
static async Task WorkerOutage()
{
    using var fixture = new Fixture();
    using (var git = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "init", "--quiet", fixture.Root } })!)
        await git.WaitForExitAsync();
    var sent = 0;
    var delivered = new List<string>();
    using var http = new HttpClient(new Handler(async (request, token) =>
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        if (request.RequestUri!.AbsolutePath.EndsWith("getUpdates"))
        {
            using var json = JsonDocument.Parse(body);
            await Task.Delay(50, token);
            return Response(200, json.RootElement.GetProperty("offset").GetInt64() == 0
                ? "{\"ok\":true,\"result\":[{\"update_id\":1,\"message\":{\"from\":{\"id\":123},\"chat\":{\"id\":123,\"type\":\"private\"},\"text\":\"hello\"}}]}"
                : "{\"ok\":true,\"result\":[]}");
        }
        if (++sent == 1) return Response(429, "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":1}}");
        lock (delivered) delivered.Add(body);
        return Response(200, "{\"ok\":true,\"result\":{}}");
    }));
    var journal = new Journal(fixture.Options);
    var sessions = new SessionStore(fixture.Options);
    using var worker = new BotWorker(new TelegramClient(http, fixture.Options), new CodexRunner(fixture.Options, sessions),
        sessions, journal, fixture.Options, NullLogger<BotWorker>.Instance);
    await worker.StartAsync(default);
    try
    {
        for (var i = 0; i < 200; i++)
        {
            lock (delivered) { if (delivered.Count >= 3) break; }
            await Task.Delay(50);
        }
        lock (delivered) Assert(delivered.Count == 3, "Replies were not recovered after the outage");
        Assert(worker.ExecuteTask?.IsCompleted == false);
        var state = await journal.SnapshotAsync(default);
        Assert(state.Offset == 2 && state.Jobs.Count == 0 && state.Replies.Count == 0);
    }
    finally { await worker.StopAsync(default); }
}
static HttpResponseMessage Response(int status, string json) => new((HttpStatusCode)status)
{ Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

static async Task Progress()
{
    var progress = new TaskProgress("secret-token");
    void Observe(string kind, string text)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "item.completed", item = new { type = kind, text } }));
        progress.Observe(json.RootElement);
    }
    Observe("reasoning", "private reasoning");
    Assert(!progress.TakeUpdate().Contains("private reasoning"));
    Observe("agent_message", "Checking **services** secret-token\u001b" + new string('x', 900));
    var update = progress.TakeUpdate();
    Assert(update.Contains("Checking **services**") && update.Contains("[redacted]"));
    Assert(!update.Contains("secret-token") && !update.Contains('\u001b') && update.Length < 800);
    Assert(!progress.TakeUpdate().Contains("Checking"), "Repeated old activity");
    Observe("command_execution", "sensitive raw output");
    Assert(!progress.TakeUpdate().Contains("sensitive"));
    var count = 0;
    using var stop = new CancellationTokenSource();
    await progress.RunAsync(TimeSpan.Zero, (_, _) => { count++; return Task.CompletedTask; }, stop.Token);
    Assert(count == 0);
    var loop = progress.RunAsync(TimeSpan.FromMilliseconds(10), (_, _) =>
    { if (++count == 2) stop.Cancel(); return Task.CompletedTask; }, stop.Token);
    await Throws<OperationCanceledException>(() => loop.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert(count == 2);
}

static async Task WorkerProgress()
{
    using var fixture = new Fixture();
    Environment.SetEnvironmentVariable("BLOODRAVEN_PROGRESS_INTERVAL_SECONDS", "10");
    Environment.SetEnvironmentVariable("TEST_CODEX_MODE", "progress");
    var options = new AppOptions();
    using (var git = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "init", "--quiet", fixture.Root } })!)
        await git.WaitForExitAsync();
    var delivered = new List<string>();
    using var http = new HttpClient(new Handler(async (request, token) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("getUpdates"))
        { await Task.Delay(50, token); return Response(200, "{\"ok\":true,\"result\":[]}"); }
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        lock (delivered) delivered.Add(body.RootElement.GetProperty("text").GetString()!);
        return Response(200, "{\"ok\":true,\"result\":{}}");
    }));
    var journal = new Journal(options);
    var sessions = new SessionStore(options);
    using var worker = new BotWorker(new TelegramClient(http, options), new CodexRunner(options, sessions),
        sessions, journal, options, NullLogger<BotWorker>.Instance);
    await worker.StartAsync(default);
    try
    {
        for (var i = 0; i < 100 && !File.Exists(Path.Combine(options.StateDirectory, "ready")); i++) await Task.Delay(50);
        await journal.ChangeAsync(d => d.Jobs.Add(new Job(1, 123, "test progress")), default);
        for (var i = 0; i < 400; i++)
        {
            lock (delivered) { if (delivered.Any(t => t.Contains("Task complete"))) break; }
            await Task.Delay(50);
        }
        lock (delivered)
        {
            Assert(delivered.Count == 3, string.Join(";", delivered));
            Assert(delivered[0].Contains("Working"));
            Assert(delivered[1].Contains("Still working") && delivered[1].Contains("Checking services"));
            Assert(delivered[2].Contains("Task complete"));
        }
        Assert(worker.ExecuteTask?.IsCompleted == false);
    }
    finally { await worker.StopAsync(default); }
    Assert((await journal.SnapshotAsync(default)).Replies.All(r => !r.Text.Contains("Still working")));
}

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
}
sealed class Fixture : IDisposable
{
    readonly Dictionary<string, string?> saved = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bloodraven-test-" + Guid.NewGuid().ToString("N"));
    public AppOptions Options { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Root);
        var fake = Path.Combine(AppContext.BaseDirectory, "fake-codex.py");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var values = new Dictionary<string, string?>
        {
            ["BLOODRAVEN_TELEGRAM_BOT_TOKEN"] = "test-secret", ["BLOODRAVEN_ALLOWED_USER_ID"] = "123",
            ["BLOODRAVEN_ALLOWED_CHAT_ID"] = "123", ["BLOODRAVEN_WORKING_DIRECTORY"] = Root,
            ["BLOODRAVEN_STATE_DIRECTORY"] = Path.Combine(Root, "state"), ["BLOODRAVEN_CODEX_EXECUTABLE"] = fake,
            ["BLOODRAVEN_CODEX_SANDBOX"] = "workspace-write", ["BLOODRAVEN_TASK_TIMEOUT_SECONDS"] = "30",
            ["TEST_CODEX_MODE"] = "normal", ["TEST_PID_FILE"] = null,
            ["BLOODRAVEN_PROGRESS_INTERVAL_SECONDS"] = null,
        };
        foreach (var (key, value) in values) { saved[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, value); }
        Options = new AppOptions();
    }
    public void Dispose()
    {
        foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value);
        Directory.Delete(Root, true);
    }
}
