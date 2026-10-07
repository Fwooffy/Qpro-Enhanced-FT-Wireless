using System.Drawing.Drawing2D;

namespace QproFaceTracking.Hub;

internal enum HubIcon
{
    None, Setup, Headset, Sliders, Models, Activity, Eyes, LowerFace, Controller, Desktop, Module,
}

// Small decorative cues share a 24-unit grid. Drawing at the destination size
// avoids font dependencies and keeps edges crisp when the monitor DPI changes.
internal static class HubIcons
{
    internal static void Draw(Graphics graphics, HubIcon icon, RectangleF bounds, Color color)
    {
        if (icon == HubIcon.None || bounds.Width <= 0 || bounds.Height <= 0) return;
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 24F, bounds.Height / 24F);
            using var pen = new Pen(color, 1.7F)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
            };
            void Line(float x1, float y1, float x2, float y2) => graphics.DrawLine(pen, x1, y1, x2, y2);
            void Lines(params PointF[] points) => graphics.DrawLines(pen, points);
            switch (icon)
            {
                case HubIcon.Setup:
                    Lines(new(3, 5), new(4.5F, 6.5F), new(7, 3.5F));
                    Lines(new(3, 12), new(4.5F, 13.5F), new(7, 10.5F));
                    Lines(new(3, 19), new(4.5F, 20.5F), new(7, 17.5F));
                    Line(11, 5, 21, 5); Line(11, 12, 21, 12); Line(11, 19, 21, 19);
                    break;
                case HubIcon.Headset:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(3, 8, 6, 5, 18, 5, 21, 8);
                        path.AddLine(21, 8, 20, 16);
                        path.AddBezier(20, 16, 17, 19, 14, 17, 12, 15);
                        path.AddBezier(12, 15, 10, 17, 7, 19, 4, 16);
                        path.CloseFigure();
                        graphics.DrawPath(pen, path);
                    }
                    Line(7, 11, 9, 11); Line(15, 11, 17, 11);
                    break;
                case HubIcon.Sliders:
                    Line(3, 6, 21, 6); Line(3, 12, 21, 12); Line(3, 18, 21, 18);
                    Line(8, 3.5F, 8, 8.5F); Line(16, 9.5F, 16, 14.5F); Line(10, 15.5F, 10, 20.5F);
                    break;
                case HubIcon.Models:
                    Lines(new(3, 7), new(12, 3), new(21, 7), new(12, 11), new(3, 7));
                    Lines(new(3, 12), new(12, 16), new(21, 12));
                    Lines(new(3, 17), new(12, 21), new(21, 17));
                    break;
                case HubIcon.Activity:
                    Lines(new(2, 12), new(6, 12), new(9, 5), new(13, 19), new(17, 9), new(19, 12), new(22, 12));
                    break;
                case HubIcon.Eyes:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(2, 12, 7, 4, 17, 4, 22, 12);
                        path.AddBezier(22, 12, 17, 20, 7, 20, 2, 12);
                        graphics.DrawPath(pen, path);
                    }
                    graphics.DrawEllipse(pen, 9, 9, 6, 6);
                    break;
                case HubIcon.LowerFace:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(4, 5, 4, 13, 6, 20, 12, 21);
                        path.AddBezier(12, 21, 18, 20, 20, 13, 20, 5);
                        graphics.DrawPath(pen, path);
                    }
                    graphics.DrawArc(pen, 8, 10, 8, 6, 0, 180);
                    break;
                case HubIcon.Controller:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(7, 7, 3, 7, 1, 18, 4, 19);
                        path.AddBezier(4, 19, 6, 20, 8, 15, 9, 15);
                        path.AddLine(9, 15, 15, 15);
                        path.AddBezier(15, 15, 16, 15, 18, 20, 20, 19);
                        path.AddBezier(20, 19, 23, 18, 21, 7, 17, 7);
                        path.CloseFigure();
                        graphics.DrawPath(pen, path);
                    }
                    Line(6, 10, 6, 14); Line(4, 12, 8, 12);
                    graphics.DrawEllipse(pen, 15, 10, 1, 1); graphics.DrawEllipse(pen, 18, 12, 1, 1);
                    break;
                case HubIcon.Desktop:
                    graphics.DrawRectangle(pen, 3, 4, 18, 13);
                    Line(12, 17, 12, 21); Line(8, 21, 16, 21);
                    break;
                case HubIcon.Module:
                    graphics.DrawRectangle(pen, 6, 6, 12, 12);
                    graphics.DrawRectangle(pen, 10, 10, 4, 4);
                    foreach (var pin in new[] { 8, 12, 16 })
                    {
                        Line(pin, 3, pin, 6); Line(pin, 18, pin, 21);
                        Line(3, pin, 6, pin); Line(18, pin, 21, pin);
                    }
                    break;
            }
        }
        finally { graphics.Restore(state); }
    }
}

// Retain a normal Label's text, measurement and accessibility. The icon is
// decorative, with explicit padding reserved before the heading text.
internal sealed class HubIconLabel : Label
{
    private HubIcon _icon;

    [System.ComponentModel.DefaultValue(HubIcon.None)]
    public HubIcon Icon
    {
        get => _icon;
        set { _icon = value; FitIconPadding(); Invalidate(); }
    }

    private void FitIconPadding()
    {
        var scale = DeviceDpi / 96F;
        Padding = new Padding(Icon == HubIcon.None ? 0 : (int)Math.Ceiling(30 * scale), 0, 0, 0);
        MinimumSize = new Size(0, Icon == HubIcon.None ? 0 : (int)Math.Ceiling(21 * scale));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        FitIconPadding();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var size = 21 * DeviceDpi / 96F;
        var top = Math.Max(0, (Font.Height - size) / 2);
        HubIcons.Draw(e.Graphics, Icon, new RectangleF(0, top, size, size),
            Enabled ? HubForm.Accent : HubForm.DisabledText);
    }
}
