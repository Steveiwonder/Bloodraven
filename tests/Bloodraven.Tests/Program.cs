using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Bloodraven;
using Microsoft.Extensions.Logging.Abstractions;

var tests = new (string Name, Func<Task> Run)[]
{
    ("tables become readable Telegram sections", Tables),
    ("model settings persist per conversation and pin scheduled jobs", ModelPreferences),
    ("model settings reach new and resumed runners", ModelRunners),
    ("worker authenticates buttons and reports health", WorkerControls),
    ("named conversations and queue controls preserve task identity", ConversationsAndQueue),
    ("durable schedules coalesce missed runs and handle timezones", Schedules),
    ("approval requests gate execution and fail closed", Approvals),
    ("attachments validate limits and confine exports", Attachments),
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
    ("Telegram payloads omit absent fields and preserve keyboards and edits", TelegramPayloads),
    ("Telegram rate limits and network exceptions are sanitised", TelegramErrors),
    ("worker survives delivery outage and executes work once", WorkerOutage),
    ("progress is bounded, selective, and stops on cancellation", Progress),
    ("worker sends progress before the final answer", WorkerProgress),
    ("progress tracks real command state and redacts excerpts", DetailedProgress),
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
static async Task ModelPreferences()
{
    using var fixture = new Fixture();
    var journal = new Journal(fixture.Options);
    await journal.InitializeAsync(default);
    await journal.ChangeAsync(d =>
    {
        Assert(BotCommands.Apply(d, 123, "/model example-model", false));
        BotCommands.Apply(d, 123, "/preset thorough", false);
        Assert(ModelSettings.For(d, "default") == new ModelSettings("example-model", "high"));
        d.Schedules.Add(new Schedule("test", 123, "check", "default", "every 1m", DateTimeOffset.UtcNow.AddMinutes(-1)));
        Scheduling.EnqueueDue(d, DateTimeOffset.UtcNow, false);
        BotCommands.Apply(d, 123, "/reasoning low", false);
        Assert(d.Jobs.Single().Settings == new ModelSettings("example-model", "high"));
        BotCommands.Apply(d, 123, "/conversation other", false);
        Assert(ModelSettings.For(d, "other") == new ModelSettings());
        BotCommands.Apply(d, 123, "/model other-model", false);
        foreach (var invalid in new[] { "/model --help", "/model bad\"value", "/model x y", "/model " + new string('x', 129), "/reasoning invalid", "/preset invalid" })
            BotCommands.Apply(d, 123, invalid, false);
        Assert(ModelSettings.For(d, "other") == new ModelSettings("other-model"));
    }, default);
    var snapshot = await journal.SnapshotAsync(default);
    snapshot.ModelSettings.Clear();
    Assert((await journal.SnapshotAsync(default)).ModelSettings.Count == 2, "Snapshot mutated live settings");
    var restarted = new Journal(fixture.Options);
    await restarted.InitializeAsync(default);
    var state = await restarted.SnapshotAsync(default);
    Assert(ModelSettings.For(state, "default") == new ModelSettings("example-model", "low"));
    Assert(state.Jobs.Single().Settings == new ModelSettings("example-model", "high"));
    BotCommands.Apply(state, 123, "/conversation default", false);
    BotCommands.Apply(state, 123, "/model default", false);
    BotCommands.Apply(state, 123, "/reasoning default", false);
    Assert(ModelSettings.For(state, "default") == new ModelSettings());
    foreach (var pair in new[] { ("fast", "low"), ("balanced", "medium"), ("thorough", "high") })
    {
        BotCommands.Apply(state, 123, "/preset " + pair.Item1, false);
        Assert(ModelSettings.For(state, "default").Effort == pair.Item2);
    }
    var legacy = JsonSerializer.Deserialize<JournalData>("{\"Jobs\":[{\"Id\":1,\"ChatId\":123,\"Text\":\"test\"}]}")!;
    Assert(legacy.ModelSettings.Count == 0 && legacy.Jobs.Single().Settings is null);
}
static async Task ModelRunners()
{
    using var fixture = new Fixture();
    var runner = new CodexRunner(fixture.Options, new SessionStore(fixture.Options));
    foreach (var settings in new[] { new ModelSettings("example-model", "high"), new ModelSettings("second-model", "low"), new ModelSettings() })
    {
        using var json = JsonDocument.Parse(await runner.RunAsync("test", default, settings: settings));
        var args = json.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert(args.Contains("model=\"" + settings.Model + "\"") == (settings.Model is not null));
        Assert(args.Contains("model_reasoning_effort=\"" + settings.Effort + "\"") == (settings.Effort is not null));
        Assert(args.Contains("--sandbox") && args.Contains(fixture.Options.Sandbox));
    }
    Environment.SetEnvironmentVariable("TEST_CODEX_MODE", "model-settings");
    foreach (var name in new[] { "start", "resume" })
        Assert(await runner.RunAsync(name, default, approve: (_, _, _) => Task.FromResult(false),
            settings: new ModelSettings("example-model", "high")) == "decline");
    await Throws<InvalidDataException>(() => runner.RunAsync("invalid", default, settings: new ModelSettings("x\";bad")));
}
static Task Tables()
{
    var parts = TelegramFormatter.Format("Intro\n\n| Addition | Benefits |\n|---|---|\n| [Uptime Kuma](https://example.com/) | Monitor **Plex** & servers |\n| Backups | Alert on failures |\n\nEnd");
    var html = string.Concat(parts.Select(p => p.Html));
    var plain = string.Concat(parts.Select(p => p.Plain));
    Assert(html.Contains("<b><a href=\"https://example.com/\">Uptime Kuma</a></b>"));
    Assert(plain.Contains("Uptime Kuma\nMonitor Plex & servers\n\nBackups\nAlert on failures"));
    Assert(!plain.Contains("|---") && plain.StartsWith("Intro") && plain.EndsWith("End"));
    var multi = TelegramFormatter.Format("Host | Status | Command\n:---|---:|:---:\nserver02 | Healthy | `echo a|b`\nother | A\\|B | done");
    Assert(multi[0].Plain.Contains("server02\nStatus: Healthy\nCommand: echo a|b"));
    Assert(multi[0].Plain.Contains("Status: A|B"));
    var code = "| A | B |\n|---|---|\n| x | y |";
    Assert(string.Concat(TelegramFormatter.Format("```\n" + code + "\n```").Select(p => p.Plain)).TrimEnd() == code);
    var malformed = "a|b\nnot a divider\nx|y";
    Assert(TelegramFormatter.Format(malformed)[0].Plain == malformed);
    var longTable = "A|B\n---|---\n[Link](https://example.com/)|" + string.Concat(Enumerable.Repeat("😀<tag>& ", 1000));
    foreach (var p in TelegramFormatter.Format(longTable, 100))
    {
        Assert(p.Plain.Length <= 100 && !char.IsHighSurrogate(p.Plain[^1]));
        _ = XElement.Parse("<root>" + p.Html + "</root>");
        Assert(!p.Html.Contains("<tag>"));
    }
    foreach (var p in parts.Concat(multi)) _ = XElement.Parse("<root>" + p.Html + "</root>");
    return Task.CompletedTask;
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
    var activity = new TaskProgress();
    using var result = JsonDocument.Parse(await runner.RunAsync("--help\n$(not-a-shell)", default, activity.Observe));
    Assert(!activity.TakeUpdate().Contains("prompt"), "Answer leaked into progress");
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
        using var parsed = JsonDocument.Parse(bodies[^1]);
        Assert(!parsed.RootElement.TryGetProperty("reply_markup", out _));
        Assert(!parsed.RootElement.TryGetProperty("message_id", out _));
        return bodies.Count == 1 ? Response(400, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: can't parse entities\"}")
            : Response(200, "{\"ok\":true,\"result\":{}}");
    }));
    await new TelegramClient(http, fixture.Options).SendAsync(123, new FormattedMessage("<b>hi</b>", "hi"), default);
    Assert(bodies[0].Contains("parse_mode") && !bodies[1].Contains("parse_mode"));
}
static async Task TelegramPayloads()
{
    using var fixture = new Fixture();
    var bodies = new List<JsonElement>();
    using var http = new HttpClient(new Handler(async (request, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = body.RootElement;
        // Match Telegram's parser: JSON null is not an absent keyboard.
        if (root.TryGetProperty("reply_markup", out var markup) && markup.ValueKind != JsonValueKind.Object)
            return Response(400, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: object expected as reply markup\"}");
        if (request.RequestUri!.AbsolutePath.EndsWith("sendMessage"))
            Assert(!root.TryGetProperty("message_id", out _), "New messages must omit the edit-only message ID");
        bodies.Add(root.Clone());
        return Response(200, "{\"ok\":true,\"result\":{\"message_id\":42}}");
    }));
    var telegram = new TelegramClient(http, fixture.Options);
    var message = new FormattedMessage("Hello", "Hello");
    Assert(await telegram.SendAsync(123, message, default) == 42);
    InlineButton[][] buttons = [[new("Next", "front:1")]];
    await telegram.SendAsync(123, message, default, buttons);
    await telegram.SendAsync(123, message, default, editMessageId: 42);
    await telegram.SendAsync(123, message, default, buttons, 42);
    Assert(bodies.Count == 4 && !bodies[0].TryGetProperty("reply_markup", out _));
    Assert(bodies[1].GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString() == "front:1");
    Assert(bodies[2].GetProperty("message_id").GetInt64() == 42);
    Assert(bodies[2].GetProperty("reply_markup").GetProperty("inline_keyboard").GetArrayLength() == 0, "Edits must still clear stale buttons");
    Assert(bodies[3].GetProperty("reply_markup").GetProperty("inline_keyboard").GetArrayLength() == 1);
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
    var progress = new TaskProgress();
    void Observe(string kind, string text)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "item.completed", item = new { type = kind, text } }));
        progress.Observe(json.RootElement);
    }
    Observe("reasoning", "private reasoning");
    Assert(!progress.TakeUpdate().Contains("private reasoning"));
    Observe("agent_message", "Checking **services** secret-token\u001b" + new string('x', 900));
    var update = progress.TakeUpdate();
    Assert(!update.Contains("Checking **services**"), "Agent message leaked into progress");
    Assert(!update.Contains("secret-token") && !update.Contains('\u001b') && update.Length < 800);
    Assert(!progress.TakeUpdate().Contains("Checking"), "Repeated old activity");
    Observe("command_execution", "sensitive raw output");
    update = progress.TakeUpdate();
    Assert(update.Contains("A command finished") && !update.Contains("sensitive"));
    foreach (var eventType in new[] { "item.started", "item.updated", "item.completed" })
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        { type = eventType, item = new { type = "agent_message", text = "Final answer" } }));
        progress.Observe(json.RootElement);
        Assert(!progress.TakeUpdate().Contains("Final answer"));
    }
    var count = 0;
    using var stop = new CancellationTokenSource();
    await progress.RunAsync(TimeSpan.Zero, (_, _) => { count++; return Task.CompletedTask; }, stop.Token);
    Assert(count == 0);
    var loop = progress.RunAsync(TimeSpan.FromMilliseconds(10), (_, _) =>
    { if (++count == 2) stop.Cancel(); return Task.CompletedTask; }, stop.Token);
    await Throws<OperationCanceledException>(() => loop.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert(count == 2);
}

static Task DetailedProgress()
{
    var progress = new TaskProgress("bridge-secret");
    void Event(string type, string id, string command, string output, int? exit = null)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { type,
            item = new { type = "command_execution", id, command, aggregated_output = output, exit_code = exit } }));
        progress.Observe(json.RootElement);
    }
    Event("item.started", "a", "docker inspect plex", "");
    var update = progress.TakeUpdate();
    Assert(update.Contains("0 completed, 1 running") && update.Contains("docker inspect plex") && update.Contains("observed for"));
    Event("item.updated", "a", "docker inspect plex", "healthy\nTOKEN=hidden PASSWORD=\"two words\"\nBearer hidden-auth bridge-secret");
    update = progress.TakeUpdate();
    Assert(update.Contains("healthy") && update.Contains("[redacted]"));
    Assert(!update.Contains("hidden") && !update.Contains("two words") && !update.Contains("bridge-secret"));
    Assert(progress.TakeUpdate().Contains("No new command output"));
    Event("item.completed", "a", "docker inspect plex", "done", 0);
    Event("item.completed", "a", "docker inspect plex", "done", 0);
    Event("item.started", "b", "sleep 10", "");
    update = progress.TakeUpdate();
    Assert(update.Contains("1 completed, 1 running") && update.Contains("exit 0") && update.Contains("sleep 10"));
    Assert(!progress.TakeUpdate().Contains("A command finished"), "Old completion repeated");
    Event("item.completed", "b", "sleep 10", "", null);
    Assert(progress.TakeUpdate().Contains("exit code unavailable"));
    var fakeCredential = Guid.NewGuid().ToString("N"); // Generated test data, never a real credential.
    Event("item.started", "c", $"curl --token {fakeCredential}", "-----BEGIN PRIVATE KEY-----\nsecret material");
    update = progress.TakeUpdate();
    Assert(!update.Contains(fakeCredential) && !update.Contains("secret material"));
    for (var i = 0; i < 8; i++) Event("item.updated", "c", new string('x', 2000), new string('y', 5000) + i);
    update = progress.TakeUpdate();
    Assert(update.Length < 3500 && TelegramFormatter.Format(update).Count == 1);
    return Task.CompletedTask;
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
    var methods = new List<string>();
    using var http = new HttpClient(new Handler(async (request, token) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("getUpdates"))
        { await Task.Delay(50, token); return Response(200, "{\"ok\":true,\"result\":[]}"); }
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        lock (delivered)
        {
            delivered.Add(body.RootElement.GetProperty("text").GetString()!);
            methods.Add(request.RequestUri.AbsolutePath.Split('/').Last());
            if (methods[^1] == "editMessageText") Assert(body.RootElement.GetProperty("message_id").GetInt64() == 42);
        }
        return Response(200, "{\"ok\":true,\"result\":{\"message_id\":42}}");
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
            Assert(methods.SequenceEqual(new[] { "sendMessage", "editMessageText", "editMessageText", "sendMessage" }));
            Assert(delivered.Count == 4, string.Join(";", delivered));
            Assert(delivered[0].Contains("Working"));
            Assert(delivered[1].Contains("Still working") && delivered[1].Contains("A command finished"));
            Assert(delivered[2].Contains("Completed"));
            Assert(delivered[3].Contains("Task complete"));
            Assert(delivered.Count(t => t.Contains("Task complete")) == 1, "Final answer delivered twice");
        }
        Assert(worker.ExecuteTask?.IsCompleted == false);
    }
    finally { await worker.StopAsync(default); }
    Assert((await journal.SnapshotAsync(default)).Replies.All(r => !r.Text.Contains("Still working")));
}

static async Task WorkerControls()
{
    using var fixture = new Fixture();
    using (var git = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "init", "--quiet", fixture.Root } })!) await git.WaitForExitAsync();
    var replies = new List<string>();
    using var http = new HttpClient(new Handler(async (request, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        if (request.RequestUri!.AbsolutePath.EndsWith("getUpdates"))
        {
            await Task.Delay(20, token);
            object Callback(int id, int user, string data) => new { update_id = id, callback_query = new { id = id.ToString(), from = new { id = user }, data,
                message = new { from = new { id = 456 }, chat = new { id = 123, type = "private" }, text = "button", message_id = 70 } } };
            object[] updates = body.RootElement.GetProperty("offset").GetInt64() == 0 ? [Callback(1, 999, "conv:hijack"), Callback(2, 123, "conv:homelab"),
                new { update_id = 3, message = new { from = new { id = 123 }, chat = new { id = 123, type = "private" }, text = "/health" } }] : [];
            return Response(200, JsonSerializer.Serialize(new { ok = true, result = updates }));
        }
        if (request.RequestUri.AbsolutePath.EndsWith("sendMessage"))
            lock (replies) replies.Add(body.RootElement.GetProperty("text").GetString()!);
        return Response(200, "{\"ok\":true,\"result\":{\"message_id\":42}}");
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
            lock (replies) { if (replies.Any(r => r.Contains("Recent failures"))) break; }
            await Task.Delay(30);
        }
        var state = await journal.SnapshotAsync(default);
        Assert(state.Offset == 4 && state.ActiveConversation == "homelab" && !state.Conversations.Contains("hijack"));
        Assert(state.Jobs.Count == 0);
        lock (replies) Assert(replies.Any(r => r.Contains("Bloodraven " + BotWorker.Version) && r.Contains("Repository:") && r.Contains("Recent failures")));
        Assert(worker.ExecuteTask?.IsCompleted == false);
    }
    finally { await worker.StopAsync(default); }
}

static async Task ConversationsAndQueue()
{
    using var fixture = new Fixture();
    var sessions = new SessionStore(fixture.Options);
    await sessions.SetAsync("legacy-session", default);
    await sessions.SetAsync("other-session", default, "homelab");
    await sessions.SetAsync("approved-session", default, "homelab", true);
    Assert(await sessions.GetAsync(default) == "legacy-session");
    Assert(await sessions.GetAsync(default, "homelab") == "other-session");
    Assert(await sessions.GetAsync(default, "homelab", true) == "approved-session");
    await sessions.ClearAsync(default, "homelab");
    Assert(await sessions.GetAsync(default, "homelab") is null && await sessions.GetAsync(default, "homelab", true) is null);
    await Throws<InvalidDataException>(() => sessions.SetAsync("x", default, "../escape"));
    var data = new JournalData();
    data.Jobs.Add(new Job(1, 123, "running", true));
    data.Jobs.Add(new Job(2, 123, "waiting"));
    data.Jobs.Add(new Job(3, 123, "next"));
    BotCommands.Apply(data, 123, "/conversation homelab", false);
    Assert(data.ActiveConversation == "homelab" && data.Jobs.All(j => j.Conversation == "default"));
    BotCommands.Apply(data, 123, "/front 3", false);
    Assert(data.Jobs.Select(j => j.Id).SequenceEqual(new long[] { 1, 3, 2 }));
    BotCommands.Apply(data, 123, "/remove 1", false);
    Assert(data.Jobs.Count == 3, "Removed running work");
    BotCommands.Apply(data, 123, "/approvals on", false);
    BotCommands.Apply(data, 123, "/approvals off", false);
    Assert(data.Jobs.Skip(1).All(j => j.ApprovalRequired), "Queued approval policy weakened");
    BotCommands.Apply(data, 123, "/clear", false);
    Assert(data.Jobs.Count == 1 && data.Jobs[0].Running);
    var legacy = JsonSerializer.Deserialize<JournalData>("{\"Offset\":4,\"Jobs\":[{\"Id\":4,\"ChatId\":123,\"Text\":\"hello\"}],\"Replies\":[]}")!;
    Assert(legacy.ActiveConversation == "default" && legacy.Jobs[0].Conversation == "default" && legacy.Schedules.Count == 0);
}

static Task Schedules()
{
    var now = DateTimeOffset.Parse("2026-09-21T12:00:00Z");
    Assert(Scheduling.Next("every 30m", now) == now.AddMinutes(30));
    Assert(Scheduling.Next("daily 08:00 Europe/London", now) == DateTimeOffset.Parse("2026-09-22T07:00:00Z"));
    Assert(Scheduling.Next("weekly sat 08:00 UTC", now) == DateTimeOffset.Parse("2026-09-26T08:00:00Z"));
    // A repeated local hour runs once; a nonexistent local time is skipped.
    Assert(Scheduling.Next("daily 01:30 Europe/London", DateTimeOffset.Parse("2026-10-25T00:45:00Z")) == DateTimeOffset.Parse("2026-10-26T01:30:00Z"));
    Assert(Scheduling.Next("daily 01:30 Europe/London", DateTimeOffset.Parse("2026-03-28T12:00:00Z")) == DateTimeOffset.Parse("2026-03-30T00:30:00Z"));
    var data = new JournalData { Approvals = true };
    data.Schedules.Add(new Schedule("s", 123, "check", "homelab", "every 30m", now.AddDays(-4)));
    Scheduling.EnqueueDue(data, now, false);
    Assert(data.Jobs.Count == 1 && data.Jobs[0].Conversation == "homelab" && data.Jobs[0].ApprovalRequired);
    Assert(data.Schedules[0].NextRun == now.AddMinutes(30));
    Scheduling.EnqueueDue(data, now.AddHours(2), false);
    Assert(data.Jobs.Count == 1, "Schedule overlapped itself");
    BotCommands.Apply(data, 123, "/schedule pause s", false);
    Assert(data.Jobs.Count == 0 && data.Schedules[0].Paused);
    var roundtrip = JsonSerializer.Deserialize<JournalData>(JsonSerializer.Serialize(data))!;
    Assert(roundtrip.Schedules[0].Paused && roundtrip.NextScheduledId == -2);
    return Task.CompletedTask;
}

static async Task Approvals()
{
    using var fixture = new Fixture();
    var journal = new Journal(fixture.Options);
    await journal.InitializeAsync(default);
    var broker = new ApprovalBroker(journal);
    var sessions = new SessionStore(fixture.Options);
    var runner = new CodexRunner(fixture.Options, sessions);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var run = runner.RunAsync("test", stop.Token, approve: (m, d, ct) => broker.RequestAsync(123, "default", m, d, ct));
    JournalData state;
    do { await Task.Delay(20); state = await journal.SnapshotAsync(default); } while (state.Replies.Count == 0 && !run.IsCompleted);
    Assert(!run.IsCompleted && !File.Exists(Path.Combine(fixture.Root, "approved.txt")), "Command ran before approval");
    var nonce = state.Replies[0].Buttons![0][0].Data.Split(':')[1];
    Assert(!broker.Decide(nonce, 999, true), "Wrong chat could approve");
    Assert(broker.Decide(nonce, 123, true));
    Assert(!broker.Decide(nonce, 123, true), "Approval replay accepted");
    Assert(await run == "accept" && File.Exists(Path.Combine(fixture.Root, "approved.txt")));
    Assert(await sessions.GetAsync(default) is null && await sessions.GetAsync(default, approved: true) == "thr_test");
    File.Delete(Path.Combine(fixture.Root, "approved.txt"));
    Assert(await runner.RunAsync("decline", stop.Token, approve: (_, _, _) => Task.FromResult(false)) == "decline");
    Assert(!File.Exists(Path.Combine(fixture.Root, "approved.txt")));
    Environment.SetEnvironmentVariable("TEST_CODEX_MODE", "fileapproval");
    var sawDiff = false;
    await runner.RunAsync("file", stop.Token, approve: (_, d, _) => { sawDiff = d.GetProperty("proposal").GetProperty("changes")[0].GetProperty("diff").GetString() == "+hello"; return Task.FromResult(false); });
    Assert(sawDiff, "File approval omitted the proposed patch");
    Environment.SetEnvironmentVariable("TEST_CODEX_MODE", "unsupported");
    Assert(await runner.RunAsync("unknown", stop.Token, approve: (_, _, _) => throw new Exception("Unknown request was approved")) == "decline");
    Assert(!File.Exists(Path.Combine(fixture.Root, "approved.txt")));
    Environment.SetEnvironmentVariable("TEST_CODEX_MODE", "normal");
    using var cancelled = new CancellationTokenSource();
    var blocked = runner.RunAsync("cancel approval", cancelled.Token, approve: (m, d, ct) => broker.RequestAsync(123, "default", m, d, ct));
    for (var i = 0; i < 200 && broker.Count == 0; i++) await Task.Delay(10);
    Assert(broker.Count == 1);
    cancelled.Cancel();
    await Throws<OperationCanceledException>(() => blocked);
    Assert(broker.Count == 0 && !File.Exists(Path.Combine(fixture.Root, "approved.txt")));
    Assert((await journal.SnapshotAsync(default)).Replies.All(r => r.Buttons is null));
    using var oversized = JsonDocument.Parse(JsonSerializer.Serialize(new { command = new string('x', 3000) }));
    Assert(!await broker.RequestAsync(123, "default", "command", oversized.RootElement, stop.Token));
    Environment.SetEnvironmentVariable("TEST_CODEX_MODE", "reject-thread");
    try
    {
        await runner.RunAsync("test protocol error", stop.Token, conversation: "reject",
            approve: (_, _, _) => throw new Exception("Rejected setup must never reach an approval callback"));
        throw new Exception("Expected a stage-specific protocol error");
    }
    catch (AppServerException ex)
    {
        Assert(ex.Stage == "thread/start" && ex.RpcCode == -32602);
        Assert(!ex.ToString().Contains("private upstream diagnostic"));
    }
}

static async Task Attachments()
{
    using var fixture = new Fixture();
    var message = new TelegramMessage(new TelegramUser(123), new TelegramChat(123, "private"), null,
        Document: new TelegramDocument("id", "report.txt", "text/plain", 10));
    Assert(AttachmentStore.Read(message).Single().Name == "report.txt");
    await Throws<ArgumentException>(() => { AttachmentStore.Read(message with { Document = message.Document! with { FileSize = TelegramClient.FileLimit + 1 } }); return Task.CompletedTask; });
    await Throws<ArgumentException>(() => { AttachmentStore.Read(message with { Document = message.Document! with { FileName = "program.exe" } }); return Task.CompletedTask; });
    await File.WriteAllTextAsync(Path.Combine(fixture.Root, "report.txt"), "test report");
    Assert(AttachmentStore.ResolveExport(fixture.Root, "report.txt").EndsWith("report.txt"));
    await Throws<ArgumentException>(() => { AttachmentStore.ResolveExport(fixture.Root, "../escape"); return Task.CompletedTask; });
    File.CreateSymbolicLink(Path.Combine(fixture.Root, "link.txt"), Path.Combine(fixture.Root, "report.txt"));
    await Throws<ArgumentException>(() => { AttachmentStore.ResolveExport(fixture.Root, "link.txt"); return Task.CompletedTask; });
    using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("getFile")
        ? Response(200, "{\"ok\":true,\"result\":{\"file_path\":\"documents/file.txt\",\"file_size\":11}}")
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("test report") })));
    var store = new AttachmentStore(fixture.Options, new TelegramClient(http, fixture.Options));
    var prepared = await store.PrepareAsync(new Job(1, 123, "inspect", Attachments: AttachmentStore.Read(message)), default);
    Assert(prepared.Prompt.Contains("untrusted input") && prepared.Images.Length == 0);
    using (var fifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { Path.Combine(fixture.Root, "pipe") } })!) await fifo.WaitForExitAsync();
    await Throws<ArgumentException>(() => store.ExportAsync("pipe", default));
    var exported = await store.ExportAsync("report.txt", default);
    Assert(await File.ReadAllTextAsync(exported) == "test report");
    store.Cleanup(1);
    Assert(!Directory.Exists(Path.Combine(fixture.Options.StateDirectory, "attachments", "1")));
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
            ["BLOODRAVEN_APPROVALS"] = null,
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
