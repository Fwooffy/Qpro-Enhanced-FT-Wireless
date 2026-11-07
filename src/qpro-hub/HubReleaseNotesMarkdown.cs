using System.Net;
using System.Text;

namespace QproFaceTracking.Hub;

[Flags]
internal enum HubReleaseNotesStyle { None = 0, Bold = 1, Italic = 2, Code = 4, Heading = 8, Quote = 16, Rule = 32 }
internal sealed record HubReleaseNotesSpan(int Start, int Length, HubReleaseNotesStyle Style, int HeadingLevel = 0);
internal sealed record HubReleaseNotesLink(int Start, int Length, Uri Target);
internal sealed record HubReleaseNotesParagraph(int Start, int Length, int Indent, bool Hanging);
internal sealed record HubReleaseNotesDocument(string Text, IReadOnlyList<HubReleaseNotesSpan> Spans,
    IReadOnlyList<HubReleaseNotesLink> Links, IReadOnlyList<HubReleaseNotesParagraph> Paragraphs, bool WasTruncated);

// This is a text formatter, not an HTML or RTF interpreter. Unsupported syntax
// stays readable and images never become network requests or embedded objects.
internal static class HubReleaseNotesMarkdown
{
    internal const int MaximumCharacters = 32_768;
    private const string Escapable = @"\`*_{}[]()#+-.!>~|";

    internal static HubReleaseNotesDocument Parse(string? markdown)
    {
        string text = (markdown ?? string.Empty).TrimStart('\uFEFF');
        bool truncated = text.Length > MaximumCharacters;
        if (truncated)
        {
            int length = MaximumCharacters;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            text = text[..length];
        }
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            text = new string(text.Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? '�' : c).ToArray());
        var writer = new Writer();
        string[] lines = text.Split('\n');
        char fence = '\0'; int fenceLength = 0;
        int budget = MaximumCharacters * 12;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (Fence(trimmed, out char marker, out int count, out string rest))
            {
                if (fence == '\0') { fence = marker; fenceLength = count; continue; }
                if (marker == fence && count >= fenceLength && rest.Length == 0) { fence = '\0'; continue; }
            }
            int start = writer.Length;
            if (fence != '\0') writer.Append(line, HubReleaseNotesStyle.Code);
            else if (IsRule(trimmed)) writer.Append("────────────", HubReleaseNotesStyle.Rule);
            else
            {
                int level = 0, indent = 0; bool hanging = false;
                HubReleaseNotesStyle style = HubReleaseNotesStyle.None;
                string content = line;
                while (level < Math.Min(trimmed.Length, 6) && trimmed[level] == '#') level++;
                if (level > 0 && level < trimmed.Length && char.IsWhiteSpace(trimmed[level]))
                {
                    content = trimmed[(level + 1)..].TrimEnd();
                    // Closing ATX hashes are optional, but only after whitespace.
                    int last = content.Length;
                    while (last > 0 && content[last - 1] == '#') last--;
                    if (last < content.Length && last > 0 && char.IsWhiteSpace(content[last - 1])) content = content[..last].TrimEnd();
                    style = HubReleaseNotesStyle.Bold | HubReleaseNotesStyle.Heading;
                }
                else
                {
                    level = 0;
                    if (trimmed.Length > 0 && index + 1 < lines.Length && Setext(lines[index + 1].Trim(), out int setextLevel))
                    {
                        level = setextLevel; style = HubReleaseNotesStyle.Bold | HubReleaseNotesStyle.Heading;
                        content = trimmed; index++;
                    }
                    else if (TryList(line, out string prefix, out content, out indent))
                    { writer.Append(prefix, style); hanging = true; }
                    else if (trimmed.StartsWith('>'))
                    { content = trimmed[1..].TrimStart(); writer.Append("│ ", HubReleaseNotesStyle.Quote); style = HubReleaseNotesStyle.Quote; }
                }
                Inline(content, 0, content.Length, style, level, 0, writer, ref budget);
                if ((indent > 0 || hanging) && writer.Paragraphs.Count < 2048)
                    writer.Paragraphs.Add(new(start, writer.Length - start, indent, hanging));
            }
            writer.NewLine();
        }
        writer.RemoveLastNewLine();
        if (truncated) writer.Append("\n\nRead the complete notes on GitHub.", HubReleaseNotesStyle.None);
        return writer.Document(truncated);
    }

    private static void Inline(string text, int start, int length, HubReleaseNotesStyle style, int heading,
        int depth, Writer writer, ref int budget)
    {
        int end = start + length;
        for (int index = start; index < end;)
        {
            if (--budget < 0 || depth >= 8) { writer.Append(text[index..end], style, heading); return; }
            char current = text[index];
            if (current == '\\' && index + 1 < end && Escapable.Contains(text[index + 1]))
            { writer.Append(text[index + 1].ToString(), style, heading); index += 2; continue; }
            if (current == '`')
            {
                int count = 1; while (index + count < end && text[index + count] == '`') count++;
                int close = Closing(text, index + count, end, new string('`', count), ref budget);
                if (close >= 0)
                { writer.Append(text[(index + count)..close], style | HubReleaseNotesStyle.Code, heading); index = close + count; continue; }
                writer.Append(text.Substring(index, count), style, heading); index += count; continue;
            }
            bool image = current == '!' && index + 1 < end && text[index + 1] == '[';
            if (current == '[' || image)
            {
                int labelStart = index + (image ? 2 : 1);
                if (TryInlineLink(text, labelStart, end, out int labelEnd, out int linkEnd, out string target, ref budget))
                {
                    int labelPosition = writer.Length;
                    Inline(text, labelStart, labelEnd - labelStart, style, heading, depth + 1, writer, ref budget);
                    if (!image && SafeLink(target, out Uri? uri) && writer.Length > labelPosition)
                        writer.AddLink(labelPosition, writer.Length - labelPosition, uri!);
                    index = linkEnd; continue;
                }
            }
            if (current == '<')
            {
                int close = Closing(text, index + 1, end, ">", ref budget);
                if (close >= 0 && SafeLink(text[(index + 1)..close], out Uri? uri))
                {
                    int position = writer.Length;
                    writer.Append(WebUtility.HtmlDecode(text[(index + 1)..close]), style, heading);
                    writer.AddLink(position, writer.Length - position, uri!); index = close + 1; continue;
                }
            }
            if (current is '*' or '_')
            {
                // Underscores inside file_names and ordinary identifiers are literal.
                bool wordInternal = current == '_' && index > start && index + 1 < end
                    && char.IsLetterOrDigit(text[index - 1]) && char.IsLetterOrDigit(text[index + 1]);
                int count = index + 1 < end && text[index + 1] == current ? 2 : 1;
                if (!wordInternal && index + count < end && !char.IsWhiteSpace(text[index + count]))
                {
                    string delimiter = new(current, count);
                    int close = Closing(text, index + count, end, delimiter, ref budget);
                    if (close > index + count && !char.IsWhiteSpace(text[close - 1]))
                    {
                        Inline(text, index + count, close - index - count,
                            style | (count == 2 ? HubReleaseNotesStyle.Bold : HubReleaseNotesStyle.Italic), heading, depth + 1, writer, ref budget);
                        index = close + count; continue;
                    }
                }
            }
            if (current == '&')
            {
                int maximum = Math.Min(end, index + 32), semicolon = text.IndexOf(';', index, maximum - index);
                if (semicolon >= 0)
                {
                    string encoded = text[index..(semicolon + 1)], decoded = WebUtility.HtmlDecode(encoded);
                    if (encoded != decoded) { writer.Append(decoded, style, heading); index = semicolon + 1; continue; }
                }
            }
            writer.Append(char.IsControl(current) && current != '\t' ? "�" : current.ToString(), style, heading);
            index++;
        }
    }

    private static int Closing(string text, int start, int end, string delimiter, ref int budget)
    {
        for (int index = start; index <= end - delimiter.Length; index++)
        {
            if (--budget < 0) return -1;
            if (text[index] == '\\') { index++; continue; }
            if (text.AsSpan(index, delimiter.Length).SequenceEqual(delimiter.AsSpan())) return index;
        }
        return -1;
    }

    private static bool TryInlineLink(string text, int start, int end, out int labelEnd,
        out int linkEnd, out string target, ref int budget)
    {
        labelEnd = linkEnd = 0; target = string.Empty;
        int nesting = 0, index = start;
        for (; index < end; index++)
        {
            if (--budget < 0) return false;
            if (text[index] == '\\') { index++; continue; }
            if (text[index] == '[') nesting++;
            else if (text[index] == ']' && nesting-- == 0) break;
            if (nesting > 8) return false;
        }
        if (index + 1 >= end || text[index + 1] != '(') return false;
        labelEnd = index; int urlStart = index + 2; nesting = 0;
        bool angle = false; char titleQuote = '\0'; bool destinationStarted = false;
        for (index = urlStart; index < end; index++)
        {
            if (--budget < 0) return false;
            if (text[index] == '\\') { index++; continue; }
            if (angle) { if (text[index] == '>') angle = false; continue; }
            if (titleQuote != '\0') { if (text[index] == titleQuote) titleQuote = '\0'; continue; }
            if (!destinationStarted && !char.IsWhiteSpace(text[index]))
            { destinationStarted = true; if (text[index] == '<') { angle = true; continue; } }
            if (nesting == 0 && text[index] is '\'' or '"' && index > urlStart && char.IsWhiteSpace(text[index - 1]))
            { titleQuote = text[index]; continue; }
            if (text[index] == '(') nesting++;
            else if (text[index] == ')' && nesting-- == 0)
            { target = Destination(text[urlStart..index].Trim()); linkEnd = index + 1; return true; }
            if (nesting > 8) return false;
        }
        return false;
    }

    internal static bool SafeLink(string target, out Uri? uri)
    {
        string value = WebUtility.HtmlDecode(target);
        uri = null;
        return value.Length is > 0 and <= 2048 && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"')
            && Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Host.Length > 0
            && uri.Scheme is "https" or "http";
    }

    private static string Destination(string text)
    {
        int end = text.StartsWith('<') ? text.IndexOf('>') : text.IndexOfAny([' ', '\t']);
        if (end < 0) return text.StartsWith('<') ? string.Empty : text;
        string remainder = text[(end + 1)..].Trim();
        bool title = remainder.Length >= 2 && remainder[0] is '\'' or '"' && remainder[^1] == remainder[0];
        if (remainder.Length != 0 && !title) return string.Empty;
        return text.StartsWith('<') ? text[1..end] : text[..end];
    }

    private static bool Fence(string text, out char marker, out int count, out string rest)
    {
        marker = text.Length > 0 ? text[0] : '\0'; count = 0; rest = string.Empty;
        if (marker is not ('`' or '~')) return false;
        while (count < text.Length && text[count] == marker) count++;
        rest = text[count..].Trim(); return count >= 3;
    }
    private static bool Setext(string text, out int level)
    {
        level = text.StartsWith('=') ? 1 : 2;
        return text.Length >= 3 && (text.All(c => c == '=') || text.All(c => c == '-'));
    }
    private static bool IsRule(string text)
    {
        char marker = text.Length > 0 ? text[0] : '\0';
        return marker is '-' or '*' or '_' && text.Count(c => c == marker) >= 3
            && text.All(c => c == marker || c == ' ');
    }
    private static bool TryList(string line, out string prefix, out string content, out int indent)
    {
        string text = line.TrimStart(); indent = Math.Min(6, (line.Length - text.Length) / 2);
        prefix = string.Empty; content = line;
        if (text.Length > 1 && text[0] is '-' or '*' or '+' && char.IsWhiteSpace(text[1]))
        { prefix = "• "; content = text[2..].TrimStart(); return true; }
        int digits = 0; while (digits < Math.Min(text.Length, 9) && char.IsAsciiDigit(text[digits])) digits++;
        if (digits > 0 && digits + 1 < text.Length && text[digits] is '.' or ')' && char.IsWhiteSpace(text[digits + 1]))
        { prefix = text[..(digits + 1)] + " "; content = text[(digits + 2)..].TrimStart(); return true; }
        return false;
    }

    private sealed class Writer
    {
        private readonly StringBuilder _text = new();
        internal readonly List<HubReleaseNotesSpan> Spans = [];
        internal readonly List<HubReleaseNotesLink> Links = [];
        internal readonly List<HubReleaseNotesParagraph> Paragraphs = [];
        internal int Length => _text.Length;
        internal void Append(string text, HubReleaseNotesStyle style, int heading = 0)
        {
            if (text.Length == 0) return;
            // Entity decoding can introduce controls after input normalization.
            // RichEdit treats NUL/CR specially, so normalize before recording spans.
            if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
                text = new string(text.Select(c => c == '\r' ? '\n'
                    : char.IsControl(c) && c is not ('\n' or '\t') ? '�' : c).ToArray());
            int start = Length; _text.Append(text);
            if (style == HubReleaseNotesStyle.None) return;
            if (Spans.Count > 0 && Spans[^1] is { } previous && previous.Start + previous.Length == start
                && previous.Style == style && previous.HeadingLevel == heading)
                Spans[^1] = previous with { Length = previous.Length + text.Length };
            else if (Spans.Count < 2048) Spans.Add(new(start, text.Length, style, heading));
        }
        internal void AddLink(int start, int length, Uri target)
        { if (Links.Count < 256) Links.Add(new(start, length, target)); }
        internal void NewLine() => _text.Append('\n');
        internal void RemoveLastNewLine() { if (_text.Length > 0) _text.Length--; }
        internal HubReleaseNotesDocument Document(bool truncated) => new(_text.ToString(), Spans, Links, Paragraphs, truncated);
    }
}
