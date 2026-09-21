using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bloodraven;

public sealed record FormattedMessage(string Html, string Plain);

// Conservative Markdown subset. Unknown syntax remains readable text; raw HTML is never trusted.
public static class TelegramFormatter
{
    sealed record Span(string Text, string Open = "", string Close = "");
    static readonly Regex Inline = new(@"`([^`\n]+)`|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|(?<!\w)\*([^*\n]+)\*(?!\w)|(?<!\w)_([^_\n]+)_(?!\w)|\[([^\]\n]+)\]\((https?://[^\s)]+)\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static IReadOnlyList<FormattedMessage> Format(string source, int limit = 3500)
    {
        if (limit < 2 || limit > 3900) throw new ArgumentOutOfRangeException(nameof(limit));
        if (string.IsNullOrWhiteSpace(source)) source = "Codex finished without a response.";
        List<Span> spans;
        try { spans = Parse(source.Replace("\r\n", "\n")); }
        catch (RegexMatchTimeoutException) { spans = [new Span(source)]; }
        var parts = new List<FormattedMessage>();
        var html = new StringBuilder();
        var plain = new StringBuilder();
        var entities = 0;
        void Flush()
        {
            if (plain.Length == 0) return;
            // Telegram rejects whitespace-only messages; preserve them with a zero-width marker.
            if (string.IsNullOrWhiteSpace(plain.ToString())) { plain.Append('\u200b'); html.Append('\u200b'); }
            parts.Add(new FormattedMessage(html.ToString(), plain.ToString()));
            html.Clear(); plain.Clear(); entities = 0;
        }
        foreach (var span in spans)
        {
            var position = 0;
            while (position < span.Text.Length)
            {
                if (plain.Length >= limit || entities >= 70) Flush();
                var length = Math.Min(limit - plain.Length, span.Text.Length - position);
                if (position + length < span.Text.Length && char.IsHighSurrogate(span.Text[position + length - 1]) &&
                    char.IsLowSurrogate(span.Text[position + length])) length--;
                if (length == 0) { Flush(); continue; }
                var piece = span.Text.Substring(position, length);
                html.Append(span.Open).Append(Escape(piece)).Append(span.Close);
                plain.Append(piece);
                if (span.Open.Length > 0) entities++;
                position += length;
            }
        }
        Flush();
        return parts;
    }
    static List<Span> Parse(string source)
    {
        var spans = new List<Span>();
        var fenced = false;
        var lines = source.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trim = line.TrimStart();
            if (trim.StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            var newline = index + 1 < lines.Length ? "\n" : "";
            if (fenced) { spans.Add(new Span(line + newline, "<pre>", "</pre>")); continue; }
            var heading = Regex.Match(line, @"^#{1,6}\s+(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            if (heading.Success) { spans.Add(new Span(heading.Groups[1].Value, "<b>", "</b>")); spans.Add(new Span(newline)); continue; }
            line = Regex.Replace(line, @"^(\s*)[-*+]\s+", "$1• ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            var cursor = 0;
            foreach (Match match in Inline.Matches(line))
            {
                if (match.Index > cursor) spans.Add(new Span(line[cursor..match.Index]));
                var group = Enumerable.Range(1, 7).First(n => match.Groups[n].Success);
                var tag = group switch { 1 => "code", 2 or 3 => "b", 4 => "s", 5 or 6 => "i", _ => "a" };
                if (group == 7 && Uri.TryCreate(match.Groups[8].Value, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https")
                    spans.Add(new Span(match.Groups[7].Value, $"<a href=\"{Escape(uri.AbsoluteUri)}\">", "</a>"));
                else if (group == 7) spans.Add(new Span(match.Value));
                else spans.Add(new Span(match.Groups[group].Value, $"<{tag}>", $"</{tag}>"));
                cursor = match.Index + match.Length;
            }
            spans.Add(new Span(line[cursor..] + newline));
        }
        if (spans.All(s => string.IsNullOrWhiteSpace(s.Text))) return [new Span(source)];
        return spans;
    }
    static string Escape(string text) => WebUtility.HtmlEncode(text);
}
