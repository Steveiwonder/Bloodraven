namespace Bloodraven;

public static class BotCommands
{
    public const string Help = "Bloodraven — task commands apply to the selected conversation.\n/engine codex|claude — select engine for new tasks\n/conversation name — create or switch conversation\n/conversations — list conversations\n/delete name — delete an idle conversation\n/new — reset the current conversation\n/model — list and choose a model\n/effort — list and choose reasoning effort\n/reasoning — alias for /effort\n/preset fast|balanced|thorough|default — reasoning shortcuts\n/queue — inspect, remove or move pending tasks\n/clear — remove pending tasks in this conversation\n/btw question — ask a side question while work continues\n/cancel — stop this conversation’s running task\n/schedule — recurring task help\n/permissions — choose permissions for this conversation\n/approvals on|off — native Codex approval buttons\n/health — diagnostics\n/status — current work\n/timings [task ID] — last prompt timing breakdown\n/file relative/path — download a repository file\nSend a photo or document with an optional caption.";

    public static bool Apply(JournalData data, long chat, string text, bool defaultApproval)
    {
        var command = BotWorker.Command(text);
        var conversation = data.ActiveConversation;
        var jobs = data.Jobs.Where(j => j.Conversation == conversation).ToArray();
        var argument = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.Trim() ?? "";

        void Reply(string value, InlineButton[][]? buttons = null) => data.Replies.Add(new Reply(Guid.NewGuid().ToString("N"), chat, value, Buttons: buttons));

        if (Engines.Apply(data, chat, command, argument))
        {
            return true;
        }

        switch (command)
        {
            case "/start":
            case "/help":
                Reply(Help);
                return true;

            case "/model":
            case "/reasoning":
            case "/effort":
            case "/preset":
                var settings = ModelSettings.For(data, data.ActiveConversation);

                if (argument.Length == 0)
                {
                    Reply($"Conversation: {data.ActiveConversation}\n{settings.Describe()}\n/model MODEL_ID or /model default\n/reasoning none|minimal|low|medium|high|xhigh|max|ultra|default\n/preset fast|balanced|thorough|default\nUse a model ID supported by your Codex login. Effort support varies by model.");
                    return true;
                }

                if (command == "/model")
                {
                    if (argument != "default" && !ModelSettings.ValidModel(argument))
                    {
                        Reply("Invalid model ID. Use /model MODEL_ID or /model default.");
                        return true;
                    }

                    settings = settings with
                    {
                        Model = argument == "default" ? null : argument
                    };
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

                    if (effort != "default" && !ModelSettings.ValidEffort(effort))
                    {
                        Reply(command == "/preset" ? "Use /preset fast|balanced|thorough|default." : "Use /reasoning none|minimal|low|medium|high|xhigh|max|ultra|default. Support varies by model.");
                        return true;
                    }

                    settings = settings with
                    {
                        Effort = effort == "default" ? null : effort
                    };
                }

                ModelSettings.Save(data, data.ActiveConversation, settings);
                Reply($"Saved for {data.ActiveConversation}.\n{settings.Describe()}\nApplies to newly queued tasks, including future scheduled runs. Running and queued tasks keep their settings. Defaults remove Bloodraven overrides; Codex controls inherited settings. Unsupported choices may be rejected by Codex.");
                return true;

            case "/conversation":
                if (!SessionStore.ValidName(argument))
                {
                    Reply("Use /conversation name (1–32 lowercase letters, digits, underscores or hyphens).");
                    return true;
                }

                if (!data.Conversations.Contains(argument))
                {
                    if (data.Conversations.Count >= 30)
                    {
                        Reply("Maximum 30 conversations reached.");
                        return true;
                    }

                    data.Conversations.Add(argument);
                }

                data.ActiveConversation = argument;
                Reply($"Active conversation: {argument}. Existing queued tasks keep their original conversation.");
                return true;

            case "/conversations":
                Reply("Conversations (tap to switch):\n" + string.Join('\n', data.Conversations.Select(n => (n == data.ActiveConversation ? "• " : "  ") + n + $" · {(data.Jobs.Any(j => j.Conversation == n && j.Running) ? "working" : "idle")} · {data.Jobs.Count(j => j.Conversation == n && !j.Running)} waiting")), data.Conversations.Select(n => new[] { new InlineButton(n, "conv:" + n) }).ToArray());
                return true;

            case "/queue":
                Reply(jobs.Length == 0 ? $"The queue for {conversation} is empty." : string.Join('\n', jobs.Select(j => $"{j.Id}: {j.Conversation} · {(j.Running ? "running" : "waiting")} · {Preview(j.Text)}")), jobs.Where(j => !j.Running).Select(j => new[] { new InlineButton($"{j.Id} → next", "front:" + j.Id), new InlineButton($"Remove {j.Id}", "remove:" + j.Id) }).ToArray());
                return true;

            case "/remove":
            case "/front":
                if (!long.TryParse(argument, out var id))
                {
                    Reply("Invalid task ID.");
                    return true;
                }

                var job = data.Jobs.FirstOrDefault(j => j.Id == id && j.Conversation == conversation && !j.Running);

                if (job is null)
                {
                    Reply("That task is not pending in the selected conversation.");
                    return true;
                }

                data.Jobs.Remove(job);
                if (command == "/front")
                {
                    data.Jobs.Insert(0, job);
                }

                Reply(command == "/front" ? $"Task {id} moved to next." : $"Task {id} removed.");
                return true;

            case "/clear":
                var removed = data.Jobs.RemoveAll(j => !j.Running && j.Conversation == conversation);

                Reply($"Removed {removed} pending task(s). The current task continues; schedules remain enabled.");
                return true;

            case "/approvals":
                if (argument is not ("on" or "off"))
                {
                    Reply($"Approvals: {((data.ApprovalFor(conversation, defaultApproval)) ? "on" : "off")}. Use /approvals on or /approvals off.");
                    return true;
                }

                data.ConversationApprovals[conversation] = argument == "on";
                if (argument == "on")
                {
                    for (var i = 0; i < data.Jobs.Count; i++)
                    {
                        if (data.Jobs[i].Engine == "codex" && !data.Jobs[i].Running && data.Jobs[i].Conversation == conversation)
                        {
                            data.Jobs[i] = data.Jobs[i] with
                            {
                                ApprovalRequired = true
                            };
                        }
                    }
                }

                Reply($"Approvals {argument} for {conversation}. Running work keeps its current policy. Disabling applies only to newly queued tasks. Approval mode uses a separate conversation history and a read-only Codex sandbox.");
                return true;

            case "/schedule":
                try
                {
                    ScheduleCommand(data, chat, argument, Reply);
                }
                catch (ArgumentException ex)
                {
                    Reply(ex.Message);
                }

                return true;

            default:
                return false;
        }
    }

    // Called while the journal lock is held, so consumers cannot start work
    // between the running check, session unlinking and queue removal.
    public static async Task DeleteAsync(JournalData data, long chat, string text, SessionStore sessions, CancellationToken token)
    {
        var name = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.Trim() ?? "";

        void Reply(string message) => Journal.AddReply(data, chat, message);

        if (!SessionStore.ValidName(name))
        {
            Reply("Use /delete name.");
            return;
        }

        if (name == "default")
        {
            Reply("The default conversation cannot be deleted. Select it and use /new to reset its history.");
            return;
        }

        if (!data.Conversations.Contains(name))
        {
            Reply($"Conversation {name} not found.");
            return;
        }

        if (data.Jobs.Any(j => j.Conversation == name && j.Running) || data.SideQuestions.Any(q => q.Conversation == name && q.Running))
        {
            Reply($"Conversation {name} is busy. Wait for its task and BTW answer to finish, or select it and use /cancel for its main task, then retry /delete {name}.");
            return;
        }

        await sessions.ClearAsync(token, name);
        await sessions.ClearAsync(token, name, "claude");
        data.Jobs.RemoveAll(j => j.Conversation == name);
        data.SideQuestions.RemoveAll(q => q.Conversation == name);
        data.Schedules.RemoveAll(s => s.Conversation == name);
        data.Outcomes.RemoveAll(o => o.Conversation == name);
        data.Timings.RemoveAll(t => t.Conversation == name);
        data.ModelSettings.Remove(name);
        data.ClaudeModelSettings.Remove(name);
        data.ClaudePermissions.Remove(name);
        data.ConversationEngines.Remove(name);
        data.ConversationApprovals.Remove(name);
        data.ConversationSandboxes.Remove(name);
        data.Conversations.Remove(name);
        if (data.ActiveConversation == name)
        {
            data.ActiveConversation = "default";
        }

        Reply($"Deleted conversation {name}, its pending tasks, schedules and settings. Active conversation: {data.ActiveConversation}. CLI transcript files and repository files are retained.");
    }

    static string Preview(string text) => text.Replace('\n', ' ').Replace('\r', ' ') is var one && one.Length > 70 ? one[..70] + "…" : one;

    static void ScheduleCommand(JournalData data, long chat, string argument, Action<string, InlineButton[][]?> reply)
    {
        var schedules = data.Schedules.Where(s => s.Conversation == data.ActiveConversation).ToArray();
        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var action = parts.FirstOrDefault() ?? "help";

        if (action == "add")
        {
            var fields = parts.ElementAtOrDefault(1)?.Split('|', 2, StringSplitOptions.TrimEntries);

            if (fields is not { Length: 2 } || string.IsNullOrWhiteSpace(fields[1]) || fields[1].Length > 16384)
            {
                throw new ArgumentException("Example: /schedule add every 30m | Check container health");
            }

            if (fields[1].StartsWith('/'))
            {
                throw new ArgumentException("Schedule a Codex prompt, not a Bloodraven slash command.");
            }

            if (data.Schedules.Count >= 20)
            {
                throw new ArgumentException("Maximum 20 schedules reached.");
            }

            var next = Scheduling.Next(fields[0], DateTimeOffset.UtcNow);
            var id = Guid.NewGuid().ToString("N")[..12];

            data.Schedules.Add(new Schedule(id, chat, fields[1], data.ActiveConversation, fields[0], next));
            reply($"Schedule {id} added for {data.ActiveConversation}. Next: {next:yyyy-MM-dd HH:mm} UTC.", null);
        }
        else
        {
            if (action == "list")
            {
                reply(schedules.Length == 0 ? "No schedules." : string.Join("\n\n", schedules.Select(s => $"{s.Id} · {s.Conversation}\n{s.Timing} · {(s.Paused ? "paused" : $"next {s.NextRun:yyyy-MM-dd HH:mm} UTC")}\n{Preview(s.Prompt)}")), schedules.Select(s => new[] { new InlineButton(s.Paused ? "Resume " + s.Id : "Pause " + s.Id, (s.Paused ? "schedresume:" : "schedpause:") + s.Id), new InlineButton("Delete", "scheddelete:" + s.Id) }).ToArray());
            }
            else
            {
                if (action is "pause" or "resume" or "delete")
                {
                    var index = data.Schedules.FindIndex(s => s.Id == parts.ElementAtOrDefault(1) && s.Conversation == data.ActiveConversation);

                    if (index < 0)
                    {
                        throw new ArgumentException("Schedule not found. Use /schedule list.");
                    }

                    var schedule = data.Schedules[index];

                    if (action == "delete")
                    {
                        data.Schedules.RemoveAt(index);
                    }
                    else
                    {
                        data.Schedules[index] = schedule with
                        {
                            Paused = action == "pause",
                            NextRun = Scheduling.Next(schedule.Timing, DateTimeOffset.UtcNow)
                        };
                    }

                    if (action != "resume")
                    {
                        data.Jobs.RemoveAll(j => j.ScheduleId == schedule.Id && !j.Running);
                    }

                    reply($"Schedule {schedule.Id}: {action}. Any running task is unaffected.", null);
                }
                else
                {
                    reply("/schedule add every 30m | prompt\n/schedule add daily 08:00 Europe/London | prompt\n/schedule add weekly sat 08:00 Europe/London | prompt\n/schedule list\n/schedule pause ID\n/schedule resume ID\n/schedule delete ID\nTimes use the named timezone. Missed runs are coalesced; a schedule cannot overlap itself.", null);
                }
            }
        }
    }
}
