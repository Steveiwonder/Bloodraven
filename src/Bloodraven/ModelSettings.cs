using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Bloodraven;

// Immutable so a queued task keeps its submitted settings through later edits/restarts.
public sealed record ModelSettings(string? Model = null, string? Effort = null)
{
    public static ModelSettings For(JournalData data, string conversation) =>
        data.ModelSettings.GetValueOrDefault(conversation) ?? new();
    public static bool ValidModel(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}\z");
    public static bool ValidEffort(string value) => value is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra";
    public string Describe() => $"Model: {Model ?? "Codex default"}\nReasoning: {Effort ?? "Codex default"}";
    public void Validate()
    {
        if (Model is not null && !ValidModel(Model) || Effort is not null && !ValidEffort(Effort))
            throw new InvalidDataException("Invalid saved model settings. Use /model default and /reasoning default.");
    }
    public void AddArguments(ProcessStartInfo start)
    {
        Validate();
        // ArgumentList bypasses shell parsing; restricted values are safe TOML strings.
        if (Model is not null) { start.ArgumentList.Add("-c"); start.ArgumentList.Add($"model=\"{Model}\""); }
        if (Effort is not null) { start.ArgumentList.Add("-c"); start.ArgumentList.Add($"model_reasoning_effort=\"{Effort}\""); }
    }
}
