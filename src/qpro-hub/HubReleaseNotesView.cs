namespace QproFaceTracking.Hub;

internal sealed class HubReleaseNotesView : RichTextBox
{
    private IReadOnlyList<HubReleaseNotesLink> _links = [];
    private IReadOnlyList<HubReleaseNotesSpan> _spans = [];
    private string _renderedText = string.Empty;
    private HubReleaseNotesLink? _pressedLink;
    private readonly ToolTip _linkTip = new();
    private Uri? _hoveredLink;
    internal event Action<Uri>? OpenLinkRequested;

    internal HubReleaseNotesView()
    {
        ReadOnly = true; DetectUrls = false; BorderStyle = BorderStyle.None;
        ScrollBars = RichTextBoxScrollBars.Vertical; WordWrap = true;
        AccessibleDescription = "Formatted release notes. Select a link and press Ctrl+Enter to open it.";
    }

    internal void SetMarkdown(string markdown)
    {
        HubReleaseNotesDocument document = HubReleaseNotesMarkdown.Parse(markdown);
        _links = document.Links; _spans = document.Spans; _renderedText = document.Text;
        _pressedLink = null; _hoveredLink = null;
        _linkTip.SetToolTip(this, string.Empty); Cursor = Cursors.IBeam;
        SuspendLayout();
        try
        {
            Text = document.Text;
            SelectAll(); SelectionFont = Font; SelectionColor = ForeColor;
            SelectionBackColor = BackColor; SelectionIndent = 0; SelectionHangingIndent = 0;
            foreach (var paragraph in document.Paragraphs)
            {
                Select(paragraph.Start, paragraph.Length);
                SelectionIndent = paragraph.Indent * 18 + (paragraph.Hanging ? 4 : 0);
                int prefixLength = paragraph.Hanging ? document.Text.IndexOf(' ', paragraph.Start,
                    Math.Min(paragraph.Length, 12)) - paragraph.Start + 1 : 0;
                SelectionHangingIndent = prefixLength > 0
                    ? TextRenderer.MeasureText(document.Text.Substring(paragraph.Start, prefixLength), Font,
                        Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width : 0;
            }
            foreach (var span in document.Spans)
            {
                Select(span.Start, span.Length);
                FontStyle style = FontStyle.Regular;
                if (span.Style.HasFlag(HubReleaseNotesStyle.Bold)) style |= FontStyle.Bold;
                if (span.Style.HasFlag(HubReleaseNotesStyle.Italic)) style |= FontStyle.Italic;
                bool code = span.Style.HasFlag(HubReleaseNotesStyle.Code);
                float size = span.HeadingLevel switch { 1 => Font.Size * 1.5F, 2 => Font.Size * 1.3F, > 2 => Font.Size * 1.12F, _ => Font.Size };
                using var styled = new Font(code ? "Consolas" : Font.FontFamily.Name, size, style);
                SelectionFont = styled;
                if (span.Style.HasFlag(HubReleaseNotesStyle.Heading)) SelectionColor = Color.FromArgb(124, 216, 213);
                else if (span.Style.HasFlag(HubReleaseNotesStyle.Quote) || span.Style.HasFlag(HubReleaseNotesStyle.Rule))
                    SelectionColor = Color.FromArgb(166, 188, 195);
                if (code) SelectionBackColor = Color.FromArgb(29, 53, 61);
            }
            foreach (var link in _links)
            {
                // A label may mix bold, code and ordinary text. Underline each
                // existing run instead of replacing a mixed SelectionFont.
                int end = link.Start + link.Length;
                int[] boundaries = _spans.Where(span => span.Start < end && span.Start + span.Length > link.Start)
                    .SelectMany(span => new[] { Math.Max(link.Start, span.Start), Math.Min(end, span.Start + span.Length) })
                    .Append(link.Start).Append(end).Distinct().Order().ToArray();
                for (int index = 0; index + 1 < boundaries.Length; index++)
                {
                    Select(boundaries[index], boundaries[index + 1] - boundaries[index]);
                    using var current = SelectionFont;
                    using var underlined = new Font(current ?? Font, (current?.Style ?? Font.Style) | FontStyle.Underline);
                    SelectionFont = underlined; SelectionColor = Color.FromArgb(124, 216, 213);
                }
            }
            Select(0, 0); ScrollToCaret();
        }
        finally { ResumeLayout(); }
    }

    internal Uri? LinkAt(int index) => _links.FirstOrDefault(link => index >= link.Start && index < link.Start + link.Length)?.Target;

    private HubReleaseNotesLink? HitLink(Point point)
    {
        if (!ClientRectangle.Contains(point) || TextLength == 0) return null;
        int index = GetCharIndexFromPosition(point);
        var link = _links.FirstOrDefault(value => index >= value.Start && index < value.Start + value.Length);
        if (link is null || index >= _renderedText.Length || _renderedText[index] is '\r' or '\n') return null;
        // RichEdit clamps an empty-area hit to the nearest character. Check the
        // glyph rectangle too so clicking below/after a link cannot activate it.
        Point origin = GetPositionFromCharIndex(index), next = GetPositionFromCharIndex(Math.Min(index + 1, TextLength));
        var span = _spans.FirstOrDefault(value => index >= value.Start && index < value.Start + value.Length);
        FontStyle style = FontStyle.Regular;
        if (span?.Style.HasFlag(HubReleaseNotesStyle.Bold) == true) style |= FontStyle.Bold;
        if (span?.Style.HasFlag(HubReleaseNotesStyle.Italic) == true) style |= FontStyle.Italic;
        float size = span?.HeadingLevel switch { 1 => Font.Size * 1.5F, 2 => Font.Size * 1.3F, > 2 => Font.Size * 1.12F, _ => Font.Size };
        using var font = new Font(span?.Style.HasFlag(HubReleaseNotesStyle.Code) == true ? "Consolas" : Font.FontFamily.Name, size, style);
        int glyphLength = char.IsHighSurrogate(_renderedText[index]) && index + 1 < _renderedText.Length && char.IsLowSurrogate(_renderedText[index + 1]) ? 2 : 1;
        Size measured = TextRenderer.MeasureText(_renderedText.Substring(index, glyphLength), font,
            Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        int width = next.Y == origin.Y && next.X > origin.X ? next.X - origin.X : measured.Width;
        return new Rectangle(origin, new Size(Math.Max(width, 1), measured.Height)).Contains(point) ? link : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Uri? link = HitLink(e.Location)?.Target;
        Cursor = link is null ? Cursors.IBeam : Cursors.Hand;
        if (link != _hoveredLink) { _hoveredLink = link; _linkTip.SetToolTip(this, link?.AbsoluteUri ?? string.Empty); }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressedLink = e.Button == MouseButtons.Left ? HitLink(e.Location) : null;
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressedLink; _pressedLink = null;
        if (e.Button == MouseButtons.Left && pressed is not null && SelectionLength == 0
            && ReferenceEquals(HitLink(e.Location), pressed))
            OpenLinkRequested?.Invoke(pressed.Target);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Enter && LinkAt(SelectionStart) is { } link)
        { e.Handled = e.SuppressKeyPress = true; OpenLinkRequested?.Invoke(link); }
        base.OnKeyDown(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        { _linkTip.Dispose(); _links = []; _spans = []; _renderedText = string.Empty; _pressedLink = null; _hoveredLink = null; OpenLinkRequested = null; }
        base.Dispose(disposing);
    }
}
