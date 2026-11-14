using System.Diagnostics;
using System.Reflection;
using QproFaceTracking.Hub;

internal static class ReleaseNotesTests
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Control.CheckForIllegalCrossThreadCalls = true;
            CheckDocumentFormatting();
            CheckLiteralAndUntrustedContent();
            CheckBounds();
            CheckPublishedNotes(args.FirstOrDefault());
            CheckNativeView();
            Console.WriteLine($"PASS: {_checks} release-note formatting checks. No visible app, network, browser, downloads or installed settings changed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckDocumentFormatting()
    {
        const string source = "\uFEFF# Release notes\r\n\r\n- **Fixed** a _small_ issue 🐾\r\n1. Keep `C:\\Qpro\\file_name.md`\r\n\r\n[Setup guide](https://example.test/setup?q=1&view=all#help)\r\n";
        var document = HubReleaseNotesMarkdown.Parse(source);
        Check(source.StartsWith('\uFEFF') && source.Contains("**Fixed**"), "Parsing modified the source string.");
        Check(!document.Text.Contains('\uFEFF') && !document.Text.Contains('\r'), "BOM or Windows line endings leaked into the displayed text.");
        Check(document.Text.Contains("Release notes") && !document.Text.Contains("# Release notes"), "The heading marker is displayed literally.");
        Check(HasStyle(document, "Release notes", "Heading"), "The heading lost its semantic style.");
        Check(document.Text.Contains("• Fixed") && !document.Text.Contains("**Fixed**"), "The bullet/strong text is not readable.");
        Check(HasStyle(document, "Fixed", "Bold") && HasStyle(document, "small", "Italic"), "Inline emphasis lost its styles.");
        Check(document.Text.Contains("1. Keep") && document.Text.Contains("🐾"), "Numbered lists or Unicode were lost.");
        Check(document.Text.Contains(@"C:\Qpro\file_name.md") && HasStyle(document, @"C:\Qpro\file_name.md", "Code"), "Inline code altered a Windows path.");
        Check(document.Links.Count == 1 && document.Links[0].Target.AbsoluteUri == "https://example.test/setup?q=1&view=all#help", "The safe link target changed.");
        Check(document.Text.Substring(document.Links[0].Start, document.Links[0].Length) == "Setup guide", "The link label is not clean display text.");
        Check(!document.Text.Contains("](https:"), "Raw Markdown link syntax is still displayed.");
        CheckRanges(document);
        foreach (var sample in new[]
        {
            ("[Docs](https://example.test/docs \"Guide title\")", "https://example.test/docs"),
            ("[Docs](https://example.test/docs 'Guide title')", "https://example.test/docs"),
            ("[Docs](https://example.test/docs \"A title with ) punctuation\")", "https://example.test/docs"),
            ("[Docs](<https://example.test/a)b> \"A title with ) punctuation\")", "https://example.test/a)b"),
            ("[Docs](https://example.test/a%20b)", "https://example.test/a%20b"),
        })
        {
            var titled = HubReleaseNotesMarkdown.Parse(sample.Item1);
            Check(titled.Text == "Docs" && titled.Links.Count == 1 && titled.Links[0].Target == new Uri(sample.Item2),
                "A valid destination/title changed the visible label or target.");
        }
        foreach (string malformed in new[]
        {
            "[Docs](https://example.test/docs unquoted title)",
            "[Docs](https://example.test/docs \"unterminated)",
            "[Docs](<https://example.test/docs)",
            "[Docs](<https://example.test/docs> junk)",
            "[Docs](https://example.test/a\"quoted)",
            "[Docs](https://example.test/docs \"title\" junk)",
        })
            Check(HubReleaseNotesMarkdown.Parse(malformed).Links.Count == 0, "A malformed destination/title became an active link.");

        var nested = HubReleaseNotesMarkdown.Parse("**A _small_ fix** and an unmatched **marker");
        Check(HasStyle(nested, "small", "Bold") && HasStyle(nested, "small", "Italic"), "Nested emphasis lost a style.");
        Check(nested.Text.EndsWith("unmatched **marker", StringComparison.Ordinal), "An unmatched marker was silently removed.");
        var escaped = HubReleaseNotesMarkdown.Parse(@"\*literal\* C:\Users\Example\file_name.md");
        Check(escaped.Text == @"*literal* C:\Users\Example\file_name.md", "Escaping removed path characters or literal markers.");
        Check(!HasStyle(escaped, "literal", "Italic"), "Escaped emphasis became active.");
        var secondaryHeading = HubReleaseNotesMarkdown.Parse("Release\n=======\n\n> Keep your saved models.\n\n---");
        Check(HasStyle(secondaryHeading, "Release", "Heading"), "Setext heading was not recognised.");
        Check(secondaryHeading.Text.Contains("Keep your saved models.") && HasStyle(secondaryHeading, "Keep your saved models.", "Quote"), "The quote text or style was lost.");
        Check(!secondaryHeading.Text.Contains("=======") && !secondaryHeading.Text.Contains("\n---"), "Heading/rule syntax leaked into the text.");
    }

    private static void CheckLiteralAndUntrustedContent()
    {
        const string code = "```powershell\n# literal\n**not bold** C:\\Qpro\\data\n```\n\nUse `**literal**` here.";
        var document = HubReleaseNotesMarkdown.Parse(code);
        Check(document.Text.Contains("# literal") && document.Text.Contains(@"C:\Qpro\data"), "Fenced code was interpreted as prose.");
        Check(document.Text.Contains("**not bold**") && !document.Text.Contains("```powershell"), "Fenced code or its marker was mishandled.");
        Check(HasStyle(document, "**not bold**", "Code") && !HasStyle(document, "**not bold**", "Bold"), "Code content gained prose formatting.");
        Check(document.Text.Contains("**literal**") && HasStyle(document, "**literal**", "Code"), "Inline code markers were parsed as emphasis.");
        Check(document.Links.Count == 0, "Literal code created a link.");

        const string untrusted = "[script](javascript:alert(1)) [file](file:///C:/private.txt) [data](data:text/html,hello) ![chart](https://example.test/chart.png) <script>alert(1)</script> {\\rtf1\\ansi \\b injected}";
        var unsafeDocument = HubReleaseNotesMarkdown.Parse(untrusted);
        Check(unsafeDocument.Links.Count == 0, "An unsafe target or remote image was exposed as a link.");
        Check(unsafeDocument.Text.Contains("chart"), "An image's useful alternative text disappeared.");
        Check(unsafeDocument.Text.Contains(@"{\rtf1\ansi \b injected}"), "Literal RTF was interpreted or removed.");
        CheckRanges(unsafeDocument);
    }

    private static void CheckBounds()
    {
        var empty = HubReleaseNotesMarkdown.Parse(null);
        Check(empty.Text.Length == 0 && empty.Spans.Count == 0 && empty.Links.Count == 0, "Empty notes created content or targets.");
        var timer = Stopwatch.StartNew();
        var malformed = HubReleaseNotesMarkdown.Parse(new string('[', HubReleaseNotesMarkdown.MaximumCharacters));
        timer.Stop();
        Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Unclosed link markers made parsing unexpectedly slow.");
        Check(malformed.Text.Length <= HubReleaseNotesMarkdown.MaximumCharacters + 128, "Malformed input expanded without a bound.");
        Check(malformed.Links.Count == 0, "Unclosed links acquired targets.");
        string ticks = "prefix " + new string('`', HubReleaseNotesMarkdown.MaximumCharacters - 7);
        timer.Restart();
        var unmatchedCode = HubReleaseNotesMarkdown.Parse(ticks);
        timer.Stop();
        Check(timer.Elapsed < TimeSpan.FromSeconds(5) && unmatchedCode.Text == ticks, "A long unmatched code marker run was slow or lost literal text.");
        var dense = HubReleaseNotesMarkdown.Parse(string.Concat(Enumerable.Repeat("[x](https://e.test) **b**\n", 900)));
        Check(dense.Links.Count <= 256 && dense.Spans.Count <= 2048 && dense.Paragraphs.Count <= 2048, "Dense input exceeded the formatting resource budget.");
        CheckRanges(dense);
        var large = HubReleaseNotesMarkdown.Parse(new string('a', HubReleaseNotesMarkdown.MaximumCharacters + 4096));
        Check(large.WasTruncated && large.Text.Length <= HubReleaseNotesMarkdown.MaximumCharacters + 128, "Oversized notes were not bounded.");
        var boundary = HubReleaseNotesMarkdown.Parse(new string('a', HubReleaseNotesMarkdown.MaximumCharacters - 1) + "🐾");
        Check(!boundary.Text.Any(char.IsSurrogate) || PairedSurrogates(boundary.Text), "Truncation split a Unicode character.");
        CheckRanges(large);
        CheckRanges(boundary);
    }

    private static void CheckPublishedNotes(string? requested)
    {
        string? path = requested is not null && File.Exists(requested) ? requested : null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); path is null && directory is not null; directory = directory.Parent)
        {
            path = directory.EnumerateFiles("RELEASE_NOTES_V*.md")
                .Select(file => (file.FullName, Number: Version.TryParse(file.Name[15..^3], out var version) ? version : null))
                .Where(file => file.Number is not null)
                .OrderByDescending(file => file.Number)
                .Select(file => file.FullName).FirstOrDefault();
        }
        Check(path is not null, "The real release-note fixture could not be found.");
        string markdown = File.ReadAllText(path!);
        var document = HubReleaseNotesMarkdown.Parse(markdown);
        Check(document.Text.Contains("Before you install") && document.Text.Contains("Qpro Companion"), "The release's meaningful content was lost.");
        Check(!document.Text.Contains("## Before you install") && !document.Text.Contains("**"), "The real changelog still displays formatting markers.");
        Check(document.Links.Count > 0 && document.Links.All(link => link.Target.Scheme is "https" or "http"), "The published links were lost or unsafe.");
        if (markdown.Contains("Checks & Updates"))
            Check(document.Text.Contains("Checks & Updates"), "An ampersand in the actual UI name was altered.");
        CheckRanges(document);
    }

    private static void CheckNativeView()
    {
        using var font = new Font("Segoe UI", 10F);
        using var view = new HubReleaseNotesView { Size = new Size(600, 400), Font = font };
        _ = view.Handle;
        var opened = new List<Uri>();
        view.OpenLinkRequested += opened.Add;
        view.SetMarkdown("# Release\n\n**Fixed** and `literal`\n\n[Setup guide](https://example.test/setup)");
        Check(opened.Count == 0 && view.ReadOnly && !view.DetectUrls, "Rendering triggered a link or left editable/automatic URL content.");
        int heading = view.Text.IndexOf("Release", StringComparison.Ordinal);
        view.Select(heading, "Release".Length);
        Check(view.SelectionFont is { Bold: true } && view.SelectionFont.Size > view.Font.Size, "The native heading is not visibly distinct.");
        view.Select(view.Text.IndexOf("Fixed", StringComparison.Ordinal), "Fixed".Length);
        Check(view.SelectionFont is { Bold: true }, "Strong text is not formatted in the native control.");
        view.Width = 260;
        foreach (var item in new[] { ("- ", 2), ("12. ", 4) })
        {
            view.SetMarkdown(item.Item1 + string.Join(' ', Enumerable.Repeat("wrapping text", 20)));
            int wrapped = view.GetFirstCharIndexFromLine(1);
            Check(wrapped > item.Item2 && Math.Abs(view.GetPositionFromCharIndex(item.Item2).X -
                view.GetPositionFromCharIndex(wrapped).X) <= 2, "A wrapped list line does not align with its label text.");
        }
        view.Width = 600;
        view.SetMarkdown("# Release\n\n**Fixed** and `literal`\n\n[Setup guide](https://example.test/setup)");
        int link = view.Text.IndexOf("Setup guide", StringComparison.Ordinal);
        Check(view.LinkAt(link)?.AbsoluteUri == "https://example.test/setup" && view.LinkAt(link + "Setup guide".Length) is null, "Native link hit ranges are wrong.");
        view.Select(link, 0);
        typeof(HubReleaseNotesView).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object[] { new KeyEventArgs(Keys.Control | Keys.Enter) });
        Check(opened.Count == 1 && opened[0].AbsoluteUri == "https://example.test/setup", "Explicit keyboard link activation was not routed.");
        view.SetMarkdown("[Docs](https://example.test/docs)");
        Mouse(view, "OnMouseDown", view.Width - 4, view.Height - 4);
        Mouse(view, "OnMouseUp", view.Width - 4, view.Height - 4);
        Check(opened.Count == 1, "Clicking blank space below a link opened the nearest text target.");
        view.SetMarkdown("[**Bold** label](https://example.test/mixed)");
        view.Select(0, "Bold".Length);
        Check(view.SelectionFont is { Bold: true, Underline: true }, "Adding a link underline removed emphasis within its label.");
        view.SetMarkdown("No links in the next release.");
        Check(opened.Count == 1 && view.LinkAt(link) is null && view.Text == "No links in the next release.", "Refreshing retained an old target or triggered it.");
        view.SetMarkdown(@"{\rtf1\ansi \b plain literal}");
        Check(view.Text == @"{\rtf1\ansi \b plain literal}", "The native view interpreted remote text as RTF.");
        view.SetMarkdown("Before &#0; after\n\n```\nraw\0tail\n```");
        Check(view.Text.Contains("after") && view.Text.Contains("tail"), "A control character truncated the remaining notes in the native control.");
        view.SetMarkdown("&#0; &#13; &#x1f; [Docs](https://example.test/controls)");
        Check(!view.Text.Contains('\0') && !view.Text.Contains('\r') && !view.Text.Contains('\u001f') &&
            view.LinkAt(view.Text.IndexOf("Docs", StringComparison.Ordinal))?.AbsoluteUri == "https://example.test/controls",
            "Decoded control characters changed native text length or link hit ranges.");
        for (int refresh = 0; refresh < 30; refresh++) view.SetMarkdown(refresh % 2 == 0 ? "**Current**" : "[Docs](https://example.test/current)");
        Check(opened.Count == 1 && view.Text == "Docs" && view.LinkAt(0)?.AbsoluteUri == "https://example.test/current", "Repeated refresh accumulated targets or events.");
        view.SetMarkdown(string.Empty);
        Check(view.Text.Length == 0 && view.LinkAt(0) is null, "Clearing the view retained content or links.");
    }

    private static bool HasStyle(HubReleaseNotesDocument document, string text, string style)
    {
        int index = document.Text.IndexOf(text, StringComparison.Ordinal);
        return index >= 0 && document.Spans.Any(span => span.Start <= index && span.Start + span.Length >= index + text.Length &&
            span.Style.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries).Contains(style));
    }

    private static void Mouse(HubReleaseNotesView view, string method, int x, int y) =>
        typeof(HubReleaseNotesView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });

    private static void CheckRanges(HubReleaseNotesDocument document)
    {
        Check(document.Spans.All(span => span.Start >= 0 && span.Length > 0 && span.Start <= document.Text.Length - span.Length), "A style range leaves the displayed text.");
        Check(document.Links.All(link => link.Start >= 0 && link.Length > 0 && link.Start <= document.Text.Length - link.Length), "A link range leaves the displayed text.");
        Check(document.Paragraphs.All(paragraph => paragraph.Start >= 0 && paragraph.Length >= 0 && paragraph.Start <= document.Text.Length - paragraph.Length), "A paragraph range leaves the displayed text.");
    }

    private static bool PairedSurrogates(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index])) { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        _checks++;
    }
}
