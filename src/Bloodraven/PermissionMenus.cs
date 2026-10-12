namespace Bloodraven;

public sealed class PermissionMenus
{
    sealed record Menu(long Chat, string Conversation, string? Sandbox, bool Approvals, DateTimeOffset Expires);
    readonly Dictionary<string, Menu> menus = [];
    static readonly string[] Values = ["read-only", "workspace-write", "full", "approvals", "default"];
    static readonly string[] Labels = ["Read only", "Workspace write", "Full Access", "Ask first", "Installation default"];

    public static bool ValidSandbox(string value) => value is "read-only" or "workspace-write" or "danger-full-access";

    public static string Describe(string sandbox, bool approvals) => approvals
        ? "Ask first (read-only sandbox, Telegram approval buttons)"
        : $"{sandbox} (no interactive approvals)";

    public void Apply(JournalData data, long chat, string argument, AppOptions options)
    {
        var conversation = data.ActiveConversation;

        if (argument.Length == 0)
        {
            Show(data, chat, options);
            return;
        }

        if (argument == "danger-full-access")
        {
            argument = "full";
        }

        if (!Values.Contains(argument))
        {
            Journal.AddReply(data, chat, "Use /permissions, or /permissions read-only|workspace-write|full|approvals|default.");
            return;
        }

        if (argument == "default")
        {
            data.ConversationSandboxes.Remove(conversation);
            data.ConversationApprovals.Remove(conversation);
        }
        else
        {
            if (argument == "approvals")
            {
                data.ConversationApprovals[conversation] = true;
            }
            else
            {
                data.ConversationSandboxes[conversation] = argument == "full" ? "danger-full-access" : argument;
                data.ConversationApprovals[conversation] = false;
            }
        }

        var mode = Describe(data.SandboxFor(conversation, options.Sandbox), data.ApprovalFor(conversation, options.ApprovalDefault));

        Journal.AddReply(data, chat, $"Saved permissions for {conversation}: {mode}.\nApplies to newly submitted tasks and future scheduled runs. Already queued and running tasks keep their submitted settings. BTW remains read-only.\nThis does not change an active Codex process. To recover a restricted running task, use /cancel, wait for it to stop, then send a continuation. Do not resend an operation that may already have completed.\nAsk first uses a separate approval-mode conversation history.");
    }

    public void Show(JournalData data, long chat, AppOptions options)
    {
        foreach (var key in menus.Where(p => p.Value.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray())
        {
            menus.Remove(key);
        }

        while (menus.Count >= 40)
        {
            menus.Remove(menus.Keys.First());
        }

        var conversation = data.ActiveConversation;
        var sandbox = data.SandboxFor(conversation, options.Sandbox);
        var approvals = data.ApprovalFor(conversation, options.ApprovalDefault);
        var running = data.Jobs.FirstOrDefault(j => j.Conversation == conversation && j.Running);
        var id = Guid.NewGuid().ToString("N");

        menus[id] = new Menu(chat, conversation, data.ConversationSandboxes.GetValueOrDefault(conversation), approvals, DateTimeOffset.UtcNow.AddMinutes(10));
        var text = $"Permissions · {conversation}\nNew tasks: {Describe(sandbox, approvals)}\nInstallation default: {Describe(options.Sandbox, data.Approvals ?? options.ApprovalDefault)}\nRunning task submitted mode: {(running is null ? "none" : Describe(running.Sandbox ?? options.Sandbox, running.ApprovalRequired))}\n\nRead only: inspect files.\nWorkspace write: edit the repository within Codex's workspace sandbox.\nFull Access: network and files available to the service user, without interactive approval.\nAsk first: Telegram approval buttons and separate conversation history.\nInstallation default: remove this conversation's overrides.\n\nThese are submitted settings, not a live permission probe. Changes apply to new tasks, not queued or running work. Codex configuration, MCP tools and host restrictions still apply. BTW remains read-only. Buttons expire in 10 minutes.";
        var buttons = Values.Select((value, i) => new[] { new InlineButton(Labels[i], $"perm:{id}:{value}") }).ToArray();

        data.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), chat, text, Buttons: buttons, Plain: true));
    }

    public string Select(JournalData data, long chat, string payload, AppOptions options)
    {
        var parts = payload.Split(':');

        if (parts.Length != 2 || !menus.TryGetValue(parts[0], out var menu) || menu.Chat != chat || menu.Conversation != data.ActiveConversation || menu.Expires < DateTimeOffset.UtcNow || !Values.Contains(parts[1]) || menu.Sandbox != data.ConversationSandboxes.GetValueOrDefault(menu.Conversation) || menu.Approvals != data.ApprovalFor(menu.Conversation, options.ApprovalDefault))
        {
            return "Menu expired or conversation/settings changed. Open /permissions again.";
        }

        menus.Remove(parts[0]);
        Apply(data, chat, parts[1], options);
        return "Permissions saved for new tasks.";
    }
}
