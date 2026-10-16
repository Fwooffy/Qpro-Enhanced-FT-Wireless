namespace QproFaceTracking.Hub;

internal sealed class HubUpdateAvailableDialog : Form
{
    private readonly DarkButton _view = ActionButton("View update");
    private readonly DarkButton _later = ActionButton("Later");
    private readonly Label _heading = new() { Text = "Update available", AutoSize = true, Dock = DockStyle.Fill,
        UseMnemonic = false, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _release = new() { AutoSize = true, Dock = DockStyle.Fill,
        UseMnemonic = false, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = HubForm.Muted,
        UseMnemonic = false, Margin = new Padding(0, 0, 0, 18) };

    internal HubUpdateAvailableDialog(HubUpdateRelease release)
    {
        Text = HubForm.ProductDisplayName + " updates";
        DoubleBuffered = true;
        BackColor = HubForm.Background; ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(540, release.IsPrerelease ? 290 : 250);
        MinimumSize = new Size(500, release.IsPrerelease ? 335 : 290);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        _heading.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
        _release.Text = HubForm.ProductDisplayName + " V" + release.Version +
            (release.PrereleaseSuffix is { } suffix ? "-" + suffix : string.Empty) +
            (release.IsPrerelease ? " · Prerelease" : string.Empty);
        _release.ForeColor = release.IsPrerelease ? HubForm.Warning : HubForm.Good;
        _summary.Text = "Review the changes and choose when to install. Your models and saved settings are kept." +
            (release.IsPrerelease ? "\n\nThis is a test release and may contain bugs." : string.Empty);
        var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24),
            ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_heading, 0, 0); layout.Controls.Add(_release, 0, 1); layout.Controls.Add(_summary, 0, 2);
        var actions = new BufferedFlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0) };
        actions.Controls.Add(_view); actions.Controls.Add(_later); layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        _view.DialogResult = DialogResult.OK; _later.DialogResult = DialogResult.Cancel;
        AcceptButton = _view; CancelButton = _later;
        // Later has initial focus so Enter does not open another dialog by accident.
        Shown += (_, _) => _later.Select();
    }

    private static DarkButton ActionButton(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(6, 0, 0, 0),
        BackColor = HubForm.Raised, ForeColor = Color.WhiteSmoke,
    };
}
