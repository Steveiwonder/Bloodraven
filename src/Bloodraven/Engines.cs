namespace Bloodraven;

public static class Engines
{
    public static void Validate(string engine)
    {
        if (engine is not ("codex" or "claude"))
        {
            throw new InvalidDataException("Invalid saved engine. Use /engine codex or /engine claude.");
        }
    }

    public static string Label(string engine) => engine == "claude" ? "Claude" : "Codex";

    // Claude has permission modes, not Codex's OS sandbox. Never translate a
    // Codex Full Access preference into bypassPermissions implicitly.
    public static string ClaudePermissionFor(JournalData data, string conversation) => data.ClaudePermissions.GetValueOrDefault(conversation) ?? "dontAsk";

    public static bool ValidClaudePermission(string value) => value is "dontAsk" or "acceptEdits" or "plan" or "bypassPermissions";

    public static bool Apply(JournalData data, long chat, string command, string argument)
    {
        var conversation = data.ActiveConversation;
        var engine = data.EngineFor(conversation);

        void Reply(string text) => Journal.AddReply(data, chat, text);

        if (command == "/engine")
        {
            if (argument.Length == 0)
            {
                Reply($"Engine · {conversation}: {engine}\nUse /engine codex or /engine claude. Each engine keeps separate sessions, models and permissions. The repository is shared.");
                return true;
            }

            if (argument is not ("codex" or "claude"))
            {
                Reply("Use /engine codex or /engine claude.");
                return true;
            }

            data.ConversationEngines[conversation] = argument;
            Reply($"Engine · {conversation}: {argument}. New tasks and future scheduled runs use this engine. Running and already queued tasks keep theirs. No history is transferred; switching back resumes that engine's saved session.\n" + (argument == "claude" ? "Claude must be installed and signed in as the service user. Use /permissions to inspect its separate permission mode." : "Codex settings and session restored."));
            return true;
        }

        if (engine != "claude")
        {
            return false;
        }

        if (command is "/btw" or "/approvals")
        {
            Reply($"{command} currently supports Codex only. Claude tasks use /permissions; the running task has not been changed.");
            return true;
        }

        if (command == "/permissions")
        {
            var mode = argument switch
            {
                "full" => "bypassPermissions",
                "default" => "dontAsk",
                _ => argument
            };

            if (argument.Length == 0)
            {
                Reply($"Claude permissions · {conversation}: {ClaudePermissionFor(data, conversation)}\n/permissions dontAsk — deny tools needing approval\n/permissions acceptEdits — auto-approve file edits\n/permissions plan — planning mode\n/permissions full — bypass permission checks\n/permissions default — dontAsk\nThese are Claude permission modes, not an OS sandbox. Claude configuration still applies. Telegram approval buttons are not supported. Changes affect new tasks only.");
                return true;
            }

            if (!ValidClaudePermission(mode))
            {
                Reply("Use /permissions dontAsk|acceptEdits|plan|full|default for Claude. Codex sandbox modes do not map to Claude permission modes.");
                return true;
            }

            data.ClaudePermissions[conversation] = mode;
            Reply($"Claude permissions saved for {conversation}: {mode}. Applies only to new tasks and future scheduled runs; running and queued tasks keep their settings.");
            return true;
        }

        if (command is not ("/model" or "/effort" or "/reasoning" or "/preset"))
        {
            return false;
        }

        var settings = ModelSettings.For(data, conversation);
        if (argument.Length == 0)
        {
            Reply($"Claude · {conversation}\n{settings.Describe(engine)}\n/model sonnet|opus|haiku|MODEL_ID|default\n/effort low|medium|high|xhigh|max|default\n/preset fast|balanced|thorough|default\nAvailability depends on your installed Claude CLI and account.");
            return true;
        }

        if (command == "/model")
        {
            if (argument != "default" && !ModelSettings.ValidModel(argument))
            {
                Reply("Invalid model ID. Use /model MODEL_ID or /model default.");
                return true;
            }

            settings = new ModelSettings(argument == "default" ? null : argument);
        }
        else
        {
            var effort = command == "/preset" ? argument switch
            {
                "fast" => "low",
                "balanced" => "medium",
                "thorough" => "high",
                "default" => "default",
                _ => "invalid"
            } : argument;

            if (effort is not ("default" or "low" or "medium" or "high" or "xhigh" or "max"))
            {
                Reply("Use /effort low|medium|high|xhigh|max|default for Claude.");
                return true;
            }

            settings = settings with { Effort = effort == "default" ? null : effort };
        }

        ModelSettings.Save(data, conversation, settings);
        Reply($"Saved for Claude · {conversation}.\n{settings.Describe(engine)}\nApplies to new tasks and future scheduled runs only.");
        return true;
    }
}
