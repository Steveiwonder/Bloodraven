using Bloodraven;

var builder = Host.CreateApplicationBuilder(args);
// Telegram authenticates in the URL path: never log request URLs.
builder.Services.AddHttpClient<TelegramClient>().RemoveAllLoggers();
builder.Services.AddSingleton<AppOptions>();
builder.Services.AddSingleton<Journal>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<CodexRunner>();
builder.Services.AddHostedService<BotWorker>();
using var host = builder.Build();
if (args.Contains("--check"))
{
    await Preflight.CheckAsync(host.Services.GetRequiredService<AppOptions>(), CancellationToken.None);
    await host.Services.GetRequiredService<TelegramClient>().CheckAsync(CancellationToken.None);
    Console.WriteLine("Configuration, repository, Codex login and Telegram authentication verified.");
    return;
}
await host.RunAsync();
