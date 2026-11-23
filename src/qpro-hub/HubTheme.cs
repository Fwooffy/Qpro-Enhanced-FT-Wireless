using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private static Label StatusLabel() => new() { AutoSize = true, Font = new Font(UiFontName, 10F, FontStyle.Bold), Margin = new Padding(8, 0, 25, 8) };
    private static Label SetupStatusLabel() => new() { Text = "○ Waiting", AutoSize = true, Font = new Font(UiFontName, 9.5F, FontStyle.Bold), ForeColor = Muted, Margin = new Padding(0, 4, 0, 8) };
    private static void SetStatus(Label label, StatusKind status, string text)
    {
        label.Text = (status == StatusKind.Neutral ? "○ " : "● ") + text;
        label.ForeColor = status switch { StatusKind.Good => Good, StatusKind.Warning => Warning, StatusKind.Bad => Bad, _ => Muted };
    }
    private static Label SectionTitle(string text, HubIcon icon = HubIcon.None) => new HubIconLabel { Text = text, Icon = icon, AutoSize = true, Font = new Font(UiFontName, 12F, FontStyle.Bold), ForeColor = TextPrimary, Margin = new Padding(0, 0, 0, 12), Tag = "responsive-info" };
    private static Label SectionCaption(string text) => new() { Text = text, AutoSize = true, Font = new Font(UiFontName, 8.5F, FontStyle.Bold), ForeColor = Muted, Margin = new Padding(0, 12, 0, 6) };
    private static Label Info(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(395, 0), ForeColor = Muted, Margin = new Padding(0, 0, 0, 12), Tag = "responsive-info" };
    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Margin = new Padding(0, 3, 12, 3) };
    private static TableLayoutPanel Card() => new BufferedTableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, BackColor = Panel, Padding = new Padding(20, 18, 20, 18), Margin = new Padding(0, 0, 0, 14) };
    private static DarkButton PrimaryButton(string text)
    {
        var button = SecondaryButton(text);
        button.VisualStyle = HubButtonStyle.Primary;
        return button;
    }
    private static DarkButton DisclosureButton(string text)
    {
        var button = SecondaryButton(text);
        button.VisualStyle = HubButtonStyle.Disclosure;
        button.Enabled = true;
        button.Padding = new Padding(24, 3, 6, 3);
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        return button;
    }
    private static DarkButton SecondaryButton(string text)
    {
        var button = new DarkButton { Text = text, AutoSize = true, BackColor = Raised, ForeColor = TextPrimary, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 0, 8, 0), Enabled = false };
        return button;
    }
    private static DarkButton ActionButton(string text, EventHandler action) { var button = SecondaryButton(text); button.Enabled = true; button.Margin = new Padding(0, 4, 8, 4); button.Click += action; return button; }
    private static DarkButton SetupButton(string text) { var button = SecondaryButton(text); button.Enabled = true; button.AutoSize = false; button.Height = 42; button.Dock = DockStyle.Bottom; button.Margin = new Padding(0, 8, 0, 3); return button; }

    private static Control WorkflowCard(string title, string description, ComboBox queue, Label queueStatus, ComboBox recorded, Button capture, Button train, Button delete)
    {
        var card = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 8, BackColor = Raised, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 12) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 9; row++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(UiFontName, 11F, FontStyle.Bold), ForeColor = Accent, Margin = new Padding(0, 0, 0, 8), Tag = "responsive-info" });
        card.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 12), Tag = "responsive-info" });
        card.Controls.Add(new Label { Text = "Recording to train", AutoSize = true, ForeColor = TextPrimary, Margin = new Padding(0, 4, 0, 4) });
        queue.Margin = new Padding(0, 3, 0, 8);
        queue.AccessibleName = "Recording to train";
        card.Controls.Add(queue);
        queueStatus.Margin = new Padding(0, 0, 0, 8);
        card.Controls.Add(queueStatus);
        var actions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        capture.AutoSize = false; train.AutoSize = false; capture.Height = 42; train.Height = 42; capture.Dock = DockStyle.Top; train.Dock = DockStyle.Top;
        capture.Margin = train.Margin = new Padding(0, 0, 0, 8);
        actions.Controls.Add(capture, 0, 0); actions.Controls.Add(train, 0, 1);
        void UpdateTrainingChoice()
        {
            queue.Enabled = queue.Items.Count > 0;
            train.Enabled = queue.SelectedItem is DatasetChoice { Dataset: { Completed: true, SampleCount: > 0 } };
        }
        queue.SelectedIndexChanged += (_, _) => UpdateTrainingChoice();
        UpdateTrainingChoice();
        var fittingActions = false;
        void FitCaptureActions()
        {
            if (fittingActions) return;
            var sideBySide = actions.ClientSize.Width >= (int)Math.Ceiling(520 * actions.DeviceDpi / 96F);
            var columns = sideBySide ? 2 : 1;
            if (actions.ColumnCount == columns) return;
            fittingActions = true;
            actions.SuspendLayout();
            try
            {
                actions.ColumnCount = columns;
                actions.RowCount = sideBySide ? 1 : 2;
                actions.ColumnStyles.Clear(); actions.RowStyles.Clear();
                for (var column = 0; column < columns; column++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
                for (var row = 0; row < actions.RowCount; row++) actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                actions.SetCellPosition(train, new TableLayoutPanelCellPosition(sideBySide ? 1 : 0, sideBySide ? 0 : 1));
                capture.Margin = new Padding(0, 0, sideBySide ? 8 : 0, 8);
            }
            finally { actions.ResumeLayout(true); fittingActions = false; }
        }
        actions.SizeChanged += (_, _) => FitCaptureActions();
        card.Controls.Add(actions);
        var recordings = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty,
            Visible = false };
        recordings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        recordings.Controls.Add(new Label { Text = "Saved recordings (including trained)", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 0, 4) });
        recorded.Margin = new Padding(0, 3, 0, 8);
        recorded.AccessibleName = "Saved recordings (including trained)";
        recordings.Controls.Add(recorded);
        delete.AutoSize = false; delete.Height = 42; delete.Dock = DockStyle.Top; delete.Margin = Padding.Empty;
        if (delete is DarkButton deleteButton) deleteButton.VisualStyle = HubButtonStyle.Destructive;
        recordings.Controls.Add(delete);
        var manage = DisclosureButton("Show saved recordings");
        manage.Enabled = true;
        manage.Margin = new Padding(0, 4, 8, 4);
        manage.Click += (_, _) =>
        {
            recordings.Visible = !recordings.Visible;
            manage.Text = (recordings.Visible ? "Hide" : "Show") + " saved recordings";
        };
        card.Controls.Add(manage);
        card.Controls.Add(recordings);
        return card;
    }

    private static CheckBox FeatureToggle(string text, bool initial)
    {
        var toggle = new DarkFeatureToggle
        {
            Text = text,
            Checked = initial,
            Appearance = Appearance.Button,
            AutoSize = false,
            Height = 44,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextPrimary,
            BackColor = Panel,
            Margin = new Padding(0, 2, 0, 4),
        };
        toggle.SizeChanged += (_, _) => FitFeatureToggle(toggle);
        toggle.TextChanged += (_, _) => FitFeatureToggle(toggle);
        toggle.FontChanged += (_, _) => FitFeatureToggle(toggle);
        return toggle;
    }

    private static void FitFeatureToggle(CheckBox toggle)
    {
        if (toggle.Width <= 0) return;
        var scale = toggle.DeviceDpi / 96F;
        var textWidth = Math.Max(100, toggle.ClientSize.Width - (int)Math.Ceiling(72 * scale));
        var textHeight = TextRenderer.MeasureText(toggle.Text, toggle.Font,
            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        var height = Math.Max((int)Math.Ceiling(42 * scale), textHeight + (int)Math.Ceiling(12 * scale));
        if (toggle.Height != height) toggle.Height = height;
    }

    private static void UpdateToggleStyle(CheckBox toggle)
    {
        toggle.Text = toggle.Text.TrimStart(' ', '◆', '◇');
        toggle.BackColor = Panel;
        toggle.ForeColor = TextPrimary;
        toggle.FlatAppearance.BorderColor = toggle.Checked ? Accent : Border;
        toggle.FlatAppearance.BorderSize = toggle.Checked ? 2 : 1;
        toggle.FlatAppearance.MouseOverBackColor = RaisedHover;
        toggle.Invalidate();
    }

    private static void ConfigureDropDown(ComboBox box)
    {
#if QPRO_REMOTE_CLICKER
        box.FlatStyle = FlatStyle.Flat;
        box.BackColor = Inset;
        box.ForeColor = TextPrimary;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        if (box is KilobyteComboBox styled) styled.UpdateItemHeight();
        else box.ItemHeight = Math.Max(box.LogicalToDeviceUnits(25), box.Font.Height + box.LogicalToDeviceUnits(8));
        box.DrawItem += (_, e) =>
        {
            bool closed = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = !closed && (e.State & DrawItemState.Selected) != 0;
            bool highContrast = SystemInformation.HighContrast;
            Color fillColor = highContrast ? selected ? SystemColors.Highlight : SystemColors.Window
                : closed ? Inset : selected ? NavigationAccent : Panel;
            Color ink = highContrast ? !box.Enabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : SystemColors.WindowText
                : !box.Enabled ? DisabledText : selected ? ShellSurface : TextPrimary;
            using (var fill = new SolidBrush(fillColor)) e.Graphics.FillRectangle(fill, e.Bounds);
            if (e.Index < 0 || e.Index >= box.Items.Count) return;
            int gap = box.LogicalToDeviceUnits(7);
            bool rtl = box.RightToLeft == RightToLeft.Yes;
            Rectangle textBounds = Rectangle.FromLTRB(e.Bounds.Left + gap, e.Bounds.Top,
                e.Bounds.Right - gap, e.Bounds.Bottom);
            if (closed && box is KilobyteComboBox chrome)
            {
                Rectangle arrow = chrome.ArrowBounds;
                if (rtl) textBounds.X = Math.Max(textBounds.Left, arrow.Right + gap);
                else textBounds.Width = Math.Max(0, Math.Min(textBounds.Right, arrow.Left - gap) - textBounds.Left);
                if (rtl) textBounds.Width = Math.Max(0, e.Bounds.Right - gap - textBounds.Left);
            }
            if (box.Items[e.Index] is FileChoice { HasMoustacheIcon: true })
            {
                int iconWidth = box.LogicalToDeviceUnits(24);
                int iconLeft = rtl ? textBounds.Right - iconWidth : textBounds.Left;
                int occupied = DrawMoustacheIcon(e.Graphics, e.Bounds, iconLeft, ink);
                if (rtl) textBounds.Width = Math.Max(0, textBounds.Width - occupied);
                else { textBounds.X += occupied; textBounds.Width = Math.Max(0, textBounds.Width - occupied); }
            }
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, box.Items[e.Index]?.ToString() ?? string.Empty, box.Font, textBounds, ink, flags);
            if (!closed && (e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -2, -2), ink, fillColor);
        };
#else
        box.FlatStyle = FlatStyle.Flat;
        box.BackColor = Raised;
        box.ForeColor = TextPrimary;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 25;
        box.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? Selected : Raised);
            e.Graphics.FillRectangle(fill, e.Bounds);
            var item = box.Items[e.Index]?.ToString() ?? string.Empty;
            var textLeft = e.Bounds.X + 7;
            if (box.Items[e.Index] is FileChoice { HasMoustacheIcon: true })
                textLeft += DrawMoustacheIcon(e.Graphics, e.Bounds, textLeft);
            TextRenderer.DrawText(e.Graphics, item, box.Font, new Rectangle(textLeft, e.Bounds.Y, e.Bounds.Right - textLeft, e.Bounds.Height), box.Enabled ? TextPrimary : DisabledText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
#endif
    }

    private static void ConfigureModelList(ListBox box)
    {
        box.BackColor = Inset;
        box.ForeColor = TextPrimary;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 56;
        box.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? Selected : Inset);
            e.Graphics.FillRectangle(fill, e.Bounds);
            using var border = new Pen(Color.FromArgb(100, Border));
            e.Graphics.DrawLine(border, e.Bounds.X + 12, e.Bounds.Bottom - 1, e.Bounds.Right - 12, e.Bounds.Bottom - 1);
            if (selected)
            {
                using var marker = new SolidBrush(Accent);
                e.Graphics.FillRectangle(marker, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
            }
            var item = box.Items[e.Index]?.ToString() ?? string.Empty;
            var textLeft = e.Bounds.X + 10;
            if (box.Items[e.Index] is FileChoice { HasMoustacheIcon: true })
                textLeft += DrawMoustacheIcon(e.Graphics, e.Bounds, textLeft);
            TextRenderer.DrawText(e.Graphics, item, box.Font, new Rectangle(textLeft, e.Bounds.Y + 4, e.Bounds.Right - textLeft - 12, e.Bounds.Height - 8), box.Enabled ? TextPrimary : DisabledText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    private static int DrawMoustacheIcon(Graphics graphics, Rectangle itemBounds, int left, Color? ink = null)
    {
        var scale = graphics.DpiX / 96F;
        var width = 24F * scale;
        var height = 12F * scale;
        var top = itemBounds.Top + (itemBounds.Height - height) / 2F;
        using var path = new GraphicsPath();
        PointF At(float x, float y) => new(left + x * width, top + y * height);
        // Two curled lobes keep the small icon recognizable at every DPI.
        path.AddBezier(At(0.5F, 0.2F), At(0.25F, -0.15F), At(0.3F, 0.9F), At(0F, 0.45F));
        path.AddBezier(At(0F, 0.45F), At(0.08F, 1.25F), At(0.35F, 1.1F), At(0.5F, 0.65F));
        path.AddBezier(At(0.5F, 0.65F), At(0.65F, 1.1F), At(0.92F, 1.25F), At(1F, 0.45F));
        path.AddBezier(At(1F, 0.45F), At(0.7F, 0.9F), At(0.75F, -0.15F), At(0.5F, 0.2F));
        path.CloseFigure();
        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ink ?? Accent);
        graphics.FillPath(brush, path);
        graphics.SmoothingMode = previousSmoothing;
        return (int)Math.Ceiling(width + 7F * scale);
    }

    private static DarkButton NavigationButton(string text, HubIcon icon)
    {
        var button = new DarkButton
        {
            Text = text,
            Icon = icon,
            AccessibleName = text,
            AutoSize = false,
            ForeColor = TextPrimary,
            BackColor = ShellSurface,
            Tag = "workflow-tab",
        };
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 3, 0, 3);
        button.Height = 36;
        button.Enabled = true;
        return button;
    }

    private static void StyleNavigationButton(DarkButton button, bool selected)
    {
        button.BackColor = selected ? Selected : ShellSurface;
        button.Emphasized = selected;
        button.Invalidate();
    }

    private static void StyleRunButton(Button button, bool nextAction)
    {
        nextAction &= button.Enabled;
        button.BackColor = Raised;
        button.ForeColor = button.Enabled ? TextPrimary : DisabledText;
        button.FlatAppearance.BorderColor = nextAction ? Accent : Border;
        button.FlatAppearance.BorderSize = nextAction ? 2 : 1;
        if (button is DarkButton dark) dark.Emphasized = nextAction;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void EnableDarkTitleBar(IntPtr handle)
    {
        try
        {
            var enabled = 1;
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
#if QPRO_REMOTE_CLICKER
            var caption = ColorTranslator.ToWin32(ShellSurface);
            var captionText = ColorTranslator.ToWin32(Muted);
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
            _ = DwmSetWindowAttribute(handle, 36, ref captionText, sizeof(int)); // DWMWA_TEXT_COLOR
#endif
        }
        catch { }
    }
}
