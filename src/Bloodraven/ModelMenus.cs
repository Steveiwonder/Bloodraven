namespace Bloodraven;

// Owner authentication happens in BotWorker. Menu choices additionally bind chat,
// conversation and the settings shown, so old buttons cannot affect a new context.
public sealed class ModelMenus
{
    sealed record Menu(long Chat, string Conversation, ModelSettings Original, bool Models,
        string[] Values, DateTimeOffset Expires);
    readonly Dictionary<string, Menu> menus = [];

    public void Show(JournalData data, long chat, bool models, IReadOnlyList<AvailableModel> catalogue)
    {
        foreach (var id in menus.Where(p => p.Value.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) menus.Remove(id);
        var settings = ModelSettings.For(data, data.ActiveConversation);
        var selected = catalogue.FirstOrDefault(m => m.Id == settings.Model);
        string[] values = models ? catalogue.Select(m => m.Id).Prepend("default").ToArray()
            : selected is not null ? selected.Efforts.Prepend("default").ToArray()
            : ["default", "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
        var note = models ? "Models reported by your installed Codex. Tap a choice or use /model ID."
            : selected is not null ? $"Efforts reported for {selected.Id}. Tap a choice or use /effort LEVEL."
            : "General effort levels; support depends on the effective model. Select an explicit /model to filter this list.";
        foreach (var page in values.Chunk(10))
        {
            while (menus.Count >= 40) menus.Remove(menus.Keys.First());
            var id = Guid.NewGuid().ToString("N");
            menus[id] = new Menu(chat, data.ActiveConversation, settings, models, page, DateTimeOffset.UtcNow.AddMinutes(10));
            var chosen = models ? settings.Model : settings.Effort;
            var buttons = page.Select((value, i) => new[] { new InlineButton((value == (chosen ?? "default") ? "✓ " : "") + value, $"choice:{id}:{i}") }).ToArray();
            data.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), chat,
                $"Conversation: {data.ActiveConversation}\n{settings.Describe()}\n\n{note}\n\n" + string.Join('\n', page) +
                "\n\nDefault removes the override. Choices apply to new tasks. Buttons expire in 10 minutes.", Buttons: buttons, Plain: true));
        }
    }

    public string Select(JournalData data, long chat, string payload)
    {
        var parts = payload.Split(':');
        if (parts.Length != 2 || !menus.TryGetValue(parts[0], out var menu) || menu.Chat != chat ||
            menu.Expires < DateTimeOffset.UtcNow || !int.TryParse(parts[1], out var index) || index < 0 || index >= menu.Values.Length ||
            ModelSettings.For(data, menu.Conversation) != menu.Original)
            return "Menu expired or settings changed. Open /model or /effort again.";
        var value = menu.Values[index] == "default" ? null : menu.Values[index];
        // Choosing a new model resets the effort override to avoid incompatible pairs.
        var settings = menu.Models ? new ModelSettings(value) : menu.Original with { Effort = value };
        data.ModelSettings[menu.Conversation] = settings;
        return $"Saved for {menu.Conversation}.\n{settings.Describe()}\nApplies to newly queued tasks.";
    }
}
