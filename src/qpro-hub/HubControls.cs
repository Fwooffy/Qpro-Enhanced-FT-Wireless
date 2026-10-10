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

// Buffer each container's background independently. Whole-window compositing
// also buffers native edit controls and can delay their scrolling and input.
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel() => DoubleBuffered = true;
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel() => DoubleBuffered = true;
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel() => DoubleBuffered = true;
}

// Keep standard CheckBox keyboard and accessibility behavior. Only the visual
// treatment changes: a quiet feature row with an explicit on/off switch.
internal sealed class DarkFeatureToggle : CheckBox
{
    public DarkFeatureToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var scale = DeviceDpi / 96F;
        int Px(int value) => Math.Max(1, (int)Math.Ceiling(value * scale));
        var track = new Rectangle(Width - Px(50), (Height - Px(24)) / 2, Px(44), Px(24));
        var caption = new Rectangle(Px(2), 0, Math.Max(1, track.Left - Px(14)), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, caption,
            Enabled ? Color.White : HubForm.DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new Pen(Enabled && Checked ? HubForm.Accent : HubForm.Border);
        using var trackFill = new SolidBrush(Enabled && Checked ? HubForm.Selected : HubForm.Inset);
        using var path = new GraphicsPath();
        path.AddArc(track.Left, track.Top, track.Height, track.Height, 90, 180);
        path.AddArc(track.Right - track.Height, track.Top, track.Height, track.Height, 270, 180);
        path.CloseFigure();
        e.Graphics.FillPath(trackFill, path);
        e.Graphics.DrawPath(outline, path);
        var diameter = Px(16);
        var thumbX = Checked ? track.Right - diameter - Px(4) : track.Left + Px(4);
        using var thumb = new SolidBrush(!Enabled ? HubForm.DisabledText : Checked ? HubForm.Accent : HubForm.Muted);
        e.Graphics.FillEllipse(thumb, thumbX, track.Top + (track.Height - diameter) / 2, diameter, diameter);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2),
                HubForm.Accent, BackColor);
    }
}

internal sealed class DarkProgressBar : Control
{
    private int _value;
    private bool _isIndeterminate;
    private int _animationOffset;

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    [DefaultValue(false)]
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set { _isIndeterminate = value; _animationOffset = 0; Invalidate(); }
    }

    public void AdvanceAnimation()
    {
        if (!_isIndeterminate) return;
        _animationOffset = (_animationOffset + 8) % Math.Max(1, Width + Math.Max(36, Width / 4));
        Invalidate();
    }

    public DarkProgressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        MinimumSize = new Size(120, 16);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(HubForm.Inset);
        var inner = new Rectangle(2, 2, Math.Max(0, Width - 4), Math.Max(0, Height - 4));
        if (_isIndeterminate && inner.Width > 0)
        {
            var blockWidth = Math.Max(36, inner.Width / 4);
            var x = inner.X + _animationOffset - blockWidth;
            using var fill = new LinearGradientBrush(
                new Rectangle(x, inner.Y, blockWidth, Math.Max(1, inner.Height)),
                Color.FromArgb(120, HubForm.Accent),
                HubForm.Accent,
                LinearGradientMode.Horizontal);
            e.Graphics.SetClip(inner);
            e.Graphics.FillRectangle(fill, x, inner.Y, blockWidth, inner.Height);
            e.Graphics.ResetClip();
        }
        else if (_value > 0)
        {
            var fillWidth = (int)Math.Round(inner.Width * (_value / 100.0));
            using var fill = new SolidBrush(_value >= 100 ? HubForm.Good : HubForm.Accent);
            e.Graphics.FillRectangle(fill, inner.X, inner.Y, fillWidth, inner.Height);
        }
        using var border = new Pen(HubForm.Border, 1);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        base.OnPaint(e);
    }
}

internal sealed class DarkButton : Button
{
    private bool _hovered;
    private bool _pressed;
    private bool _emphasized;
    private Color _outlineColor = Color.Empty;
    private int _outlineWidth = 1;
    private HubIcon _icon;
    [DefaultValue(HubIcon.None)]
    public HubIcon Icon { get => _icon; set { if (_icon == value) return; _icon = value; Invalidate(); } }
    [DefaultValue(false)]
    public bool Emphasized { get => _emphasized; set { if (_emphasized == value) return; _emphasized = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public Color OutlineColor { get => _outlineColor; set { if (_outlineColor == value) return; _outlineColor = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public int OutlineWidth
    {
        get => _outlineWidth;
        set
        {
            var width = Math.Clamp(value, 1, 4);
            if (_outlineWidth == width) return;
            _outlineWidth = width;
            Invalidate();
        }
    }

    public DarkButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left && Enabled; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { _pressed = false; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && e.KeyCode == Keys.Space && e.Modifiers == Keys.None)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_pressed ? HubForm.Accent : _hovered && Enabled ? HubForm.RaisedHover : BackColor);
        var borderColor = OutlineColor.IsEmpty ? (Emphasized ? HubForm.Accent : HubForm.Border) : OutlineColor;
        var borderWidth = Emphasized ? Math.Max(2, OutlineWidth) : OutlineWidth;
        using var border = new Pen(borderColor, borderWidth);
        var inset = borderWidth > 1 ? 1 : 0;
        e.Graphics.DrawRectangle(border, inset, inset, Width - (inset * 2 + 1), Height - (inset * 2 + 1));
        var textColor = !Enabled ? HubForm.DisabledText : _pressed ? HubForm.Background : Color.White;
        var textBounds = ClientRectangle;
        var textAlignment = TextFormatFlags.HorizontalCenter;
        if (Icon != HubIcon.None)
        {
            var scale = DeviceDpi / 96F;
            var iconSize = 18 * scale;
            HubIcons.Draw(e.Graphics, Icon,
                new RectangleF(12 * scale, (Height - iconSize) / 2, iconSize, iconSize),
                !Enabled || _pressed ? textColor : Emphasized ? HubForm.Accent : HubForm.Muted);
            var textLeft = (int)Math.Ceiling(40 * scale);
            textBounds = new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - (int)Math.Ceiling(10 * scale)), Height);
            textAlignment = TextFormatFlags.Left;
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, textColor,
            textAlignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        // User-painted buttons must draw their own keyboard focus cue.
        if (Focused && ShowFocusCues && Width > 12 && Height > 12)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), textColor, BackColor);
    }
}

internal sealed class TextPromptDialog : Form
{
    private readonly TextBox _input;
    public string Value => _input.Text;

    public TextPromptDialog(string title, string prompt, string initial, string fontName, Color background, Color panel, Color raised, Color border, Color accent)
    {
        Text = title;
        Size = new Size(520, 235);
        MinimumSize = new Size(440, 220);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = background;
        ForeColor = Color.White;
        Font = new Font(fontName, 10F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(20), BackColor = panel };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(450, 0), ForeColor = Color.White, Margin = new Padding(0, 0, 0, 12) }, 0, 0);
        _input = new TextBox { Text = initial, Dock = DockStyle.Top, BackColor = raised, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, MaxLength = 80, Margin = Padding.Empty };
        layout.Controls.Add(_input, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 16, 0, 0) };
        var save = new DarkButton { Text = "Save name", DialogResult = DialogResult.OK, AutoSize = true, Enabled = true, Emphasized = true, BackColor = raised, ForeColor = Color.White, Padding = new Padding(14, 7, 14, 7), Margin = new Padding(8, 0, 0, 0) };
        var cancel = new DarkButton { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Enabled = true, BackColor = raised, ForeColor = Color.White, Padding = new Padding(14, 7, 14, 7), Margin = new Padding(8, 0, 0, 0) };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => { _input.SelectAll(); _input.Focus(); };
    }
}

internal sealed class DarkSlider : Control
{
    private int _value;
    [DefaultValue(0)]
    public int Minimum { get; set; }
    [DefaultValue(100)]
    public int Maximum { get; set; } = 100;
    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, Minimum, Maximum);
            if (_value == next) return;
            _value = next;
            Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }

    public DarkSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    // The painted slider needs a value and role for screen readers as well as
    // arrow-key input. Its accessible value uses the same clamped setting.
    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibleObject(this);

    private sealed class SliderAccessibleObject(DarkSlider owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Slider;
        public override string? Value
        {
            get => owner.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            set
            {
                if (int.TryParse(value, out var parsed)) owner.Value = parsed;
            }
        }
        public override string? Description => owner.AccessibleDescription
            ?? $"Range {owner.Minimum} to {owner.Maximum}. Use the arrow keys to adjust.";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96F;
        int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
        var left = Px(8);
        var right = Math.Max(left + 1, Width - Px(8));
        var center = Height / 2;
        var range = Math.Max(1, Maximum - Minimum);
        var ratio = (Value - Minimum) / (float)range;
        var thumbX = left + (int)Math.Round((right - left) * ratio);
        using var track = new Pen(HubForm.Border, Px(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(HubForm.Accent, Px(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(track, left, center, right, center);
        e.Graphics.DrawLine(active, left, center, thumbX, center);
        using var thumb = new SolidBrush(Enabled ? Color.White : HubForm.DisabledText);
        e.Graphics.FillEllipse(thumb, thumbX - Px(7), center - Px(7), Px(14), Px(14));
        if (Focused && ShowFocusCues && Width > 12 && Height > 12)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3),
                HubForm.Accent, BackColor);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down
        || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        SetFromX(e.X);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (e.Button == MouseButtons.Left) SetFromX(e.X); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int change = e.KeyCode switch
        {
            Keys.Left or Keys.Down => -1,
            Keys.Right or Keys.Up => 1,
            _ => 0
        };
        if (change == 0) return;
        Value += change;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }
    private void SetFromX(int x)
    {
        if (!Enabled) return;
        var inset = (int)Math.Ceiling(8 * DeviceDpi / 96F);
        var ratio = Math.Clamp((x - (float)inset) / Math.Max(1, Width - 2 * inset), 0f, 1f);
        Value = Minimum + (int)Math.Round((Maximum - Minimum) * ratio);
    }
}
