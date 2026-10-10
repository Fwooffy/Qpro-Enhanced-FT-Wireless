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
        label.Text = "● " + text;
        label.ForeColor = status switch { StatusKind.Good => Good, StatusKind.Warning => Warning, _ => Bad };
    }
    private static Label SectionTitle(string text, HubIcon icon = HubIcon.None) => new HubIconLabel { Text = text, Icon = icon, AutoSize = true, Font = new Font(UiFontName, 14F, FontStyle.Bold), ForeColor = Color.White, Margin = new Padding(0, 0, 0, 10), Tag = "responsive-info" };
    private static Label SectionCaption(string text) => new() { Text = text, AutoSize = true, Font = new Font(UiFontName, 9F, FontStyle.Bold), ForeColor = Accent, Margin = new Padding(0, 10, 0, 5) };
    private static Label Info(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(395, 0), ForeColor = Muted, Margin = new Padding(0, 0, 0, 12), Tag = "responsive-info" };
    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Margin = new Padding(0, 3, 12, 3) };
    private static TableLayoutPanel Card() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, BackColor = Panel, Padding = new Padding(20, 16, 20, 16), Margin = new Padding(0, 0, 0, 14) };
    private static DarkButton PrimaryButton(string text) => SecondaryButton(text);
    private static DarkButton SecondaryButton(string text)
    {
        var button = new DarkButton { Text = text, AutoSize = true, BackColor = Raised, ForeColor = Color.White, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 0, 8, 0), Enabled = false };
        return button;
    }
    private static DarkButton ActionButton(string text, EventHandler action) { var button = SecondaryButton(text); button.Enabled = true; button.Margin = new Padding(0, 4, 8, 4); button.Click += action; return button; }
    private static DarkButton SetupButton(string text) { var button = SecondaryButton(text); button.Enabled = true; button.AutoSize = false; button.Height = 42; button.Dock = DockStyle.Bottom; button.Margin = new Padding(0, 8, 0, 3); return button; }

    private static Control SetupStepCard(string number, string title, string description, Label status, params DarkButton[] buttons)
    {
        if (buttons.Length == 0) throw new ArgumentException("A setup step needs an action", nameof(buttons));
        var card = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 5, ColumnCount = 1, BackColor = Raised, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 12) };
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 54 * buttons.Length));
        card.Controls.Add(new Label { Text = $"STEP {number}", AutoSize = true, ForeColor = Warning, Font = new Font(UiFontName, 8.5F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        card.Controls.Add(new HubIconLabel { Text = title, Icon = number == "2" ? HubIcon.Desktop : HubIcon.Module, AutoSize = true, ForeColor = Color.White, Font = new Font(UiFontName, 11F, FontStyle.Bold), Margin = new Padding(0, 3, 0, 6), Tag = "responsive-info" }, 0, 1);
        card.Controls.Add(status, 0, 2);
        card.Controls.Add(new Label { Text = description, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Muted, Margin = new Padding(0, 0, 0, 8), Tag = "responsive-info" }, 0, 3);
        if (buttons.Length == 1)
            card.Controls.Add(buttons[0], 0, 4);
        else
        {
            var actions = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = buttons.Length, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var index = 0; index < buttons.Length; index++)
            {
                // Divide the outer action row evenly after its DPI height is set.
                actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / buttons.Length));
                actions.Controls.Add(buttons[index], 0, index);
            }
            card.Controls.Add(actions, 0, 4);
        }
        return card;
    }

    private static Control WorkflowCard(string title, string description, ComboBox queue, Label queueStatus, ComboBox recorded, Button capture, Button train, Button delete)
    {
        var card = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 8, BackColor = Raised, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 12) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 9; row++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(UiFontName, 11F, FontStyle.Bold), ForeColor = Accent, Margin = new Padding(0, 0, 0, 8), Tag = "responsive-info" });
        card.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 12), Tag = "responsive-info" });
        card.Controls.Add(new Label { Text = "Recording to train", AutoSize = true, ForeColor = Color.White, Margin = new Padding(0, 4, 0, 4) });
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
        recordings.Controls.Add(delete);
        var manage = SecondaryButton("Show saved recordings");
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
            ForeColor = Color.White,
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
        toggle.ForeColor = Color.White;
        toggle.FlatAppearance.BorderColor = toggle.Checked ? Accent : Border;
        toggle.FlatAppearance.BorderSize = toggle.Checked ? 2 : 1;
        toggle.FlatAppearance.MouseOverBackColor = RaisedHover;
        toggle.Invalidate();
    }

    private static void ConfigureDropDown(ComboBox box)
    {
        box.FlatStyle = FlatStyle.Flat;
        box.BackColor = Raised;
        box.ForeColor = Color.White;
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
            TextRenderer.DrawText(e.Graphics, item, box.Font, new Rectangle(textLeft, e.Bounds.Y, e.Bounds.Right - textLeft, e.Bounds.Height), box.Enabled ? Color.White : DisabledText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    private static void ConfigureModelList(ListBox box)
    {
        box.BackColor = Inset;
        box.ForeColor = Color.White;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 38;
        box.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? Selected : Inset);
            e.Graphics.FillRectangle(fill, e.Bounds);
            using var border = new Pen(selected ? Accent : Border, selected ? 2 : 1);
            e.Graphics.DrawRectangle(border, e.Bounds.X + 1, e.Bounds.Y + 1, e.Bounds.Width - 3, e.Bounds.Height - 3);
            var item = box.Items[e.Index]?.ToString() ?? string.Empty;
            var textLeft = e.Bounds.X + 10;
            if (box.Items[e.Index] is FileChoice { HasMoustacheIcon: true })
                textLeft += DrawMoustacheIcon(e.Graphics, e.Bounds, textLeft);
            TextRenderer.DrawText(e.Graphics, (selected ? "●  " : "○  ") + item, box.Font, new Rectangle(textLeft, e.Bounds.Y, e.Bounds.Right - textLeft - 8, e.Bounds.Height), box.Enabled ? Color.White : DisabledText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
    }

    private static int DrawMoustacheIcon(Graphics graphics, Rectangle itemBounds, int left)
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
        using var brush = new SolidBrush(Accent);
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
            ForeColor = Color.White,
            BackColor = Panel,
            Tag = "workflow-tab",
        };
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2);
        button.Height = 36;
        button.Enabled = true;
        return button;
    }

    private static void StyleNavigationButton(DarkButton button, bool selected)
    {
        button.BackColor = selected ? Selected : Panel;
        button.Emphasized = selected;
        button.Invalidate();
    }

    private static void StyleRunButton(Button button, bool nextAction)
    {
        button.BackColor = Raised;
        button.ForeColor = button.Enabled ? Color.White : DisabledText;
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
        }
        catch { }
    }
}
