namespace QproFaceTracking.Hub;

// Release notes are shown as plain text; remote content is never rendered as HTML.
internal sealed class HubUpdateDialog : Form
{
    private readonly DarkButton _install = Button("Update");
    private readonly DarkButton _close = Button("Close");
    private readonly DarkButton _github = Button("View release on GitHub");
    private readonly DarkButton _components = Button("Components");
    private readonly CheckBox _automatic = new() { Text = "Check automatically when the Hub opens", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = HubForm.Muted, Margin = new Padding(0, 8, 0, 8) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 12, Visible = false, Maximum = 1000 };
    private readonly Func<HubUpdateRelease, IProgress<HubUpdateProgress>, CancellationToken, Task<HubStagedUpdate>> _stage;
    private CancellationTokenSource? _download;
    private bool _closeAfterCancel;
    internal bool CheckAutomatically => _automatic.Checked;
    internal HubStagedUpdate? StagedUpdate { get; private set; }
    internal HubUpdateRelease? Release { get; }
    internal bool OpenComponents { get; private set; }

    internal HubUpdateDialog(HubUpdateCheck check, Version current, bool automatic, string? blocked,
        Func<HubUpdateRelease, IProgress<HubUpdateProgress>, CancellationToken, Task<HubStagedUpdate>> stage)
    {
        _stage = stage;
        Release = check.Release;
        Text = "Qpro updates";
        BackColor = HubForm.Background; ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(760, 670); MinimumSize = new Size(610, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        _automatic.Checked = automatic;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label { Text = check.Release is { } release ? "QproFaceTracking V" + release.Version : "App updates",
            AutoSize = true, Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10), UseMnemonic = false };
        layout.Controls.Add(title, 0, 0);
        var summary = new Label { Text = $"Installed: V{current}\n{check.Message}\n\n" +
            "Update this app in the same folder. Your models, recordings, options and connections are kept. The Hub restarts to apply it. " +
            "Use Components for Qpro module, runtime and GPU library updates.",
            Dock = DockStyle.Fill, AutoSize = true, ForeColor = HubForm.Muted, Margin = new Padding(0, 0, 0, 14), UseMnemonic = false };
        layout.Controls.Add(summary, 0, 1);
        var notes = new RichTextBox { Text = check.Release?.Notes ?? "Stable updates are checked on the project's public GitHub releases page.",
            ReadOnly = true, DetectUrls = false, Dock = DockStyle.Fill, BackColor = HubForm.Inset,
            ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical, Font = Font };
        notes.AccessibleName = "Release notes";
        layout.Controls.Add(notes, 0, 2);
        _automatic.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(_automatic, 0, 3);
        _status.Text = blocked ?? (check.State == HubUpdateCheckState.Available ? "Ready to download. Nothing is installed until you click Update." : check.Message);
        layout.Controls.Add(_status, 0, 4);
        layout.Controls.Add(_progress, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        actions.Controls.Add(_close); actions.Controls.Add(_install); actions.Controls.Add(_github); actions.Controls.Add(_components);
        layout.Controls.Add(actions, 0, 6);
        Controls.Add(layout);
        _install.Enabled = blocked is null && check.State == HubUpdateCheckState.Available && check.Release?.CanInstall == true;
        _install.Click += async (_, _) => await InstallAsync();
        _components.Click += (_, _) => { OpenComponents = true; Close(); };
        _close.Click += (_, _) => { if (_download is not null) { _closeAfterCancel = true; _download.Cancel(); } else Close(); };
        _github.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                (Release?.ReleasePage ?? HubUpdateReleaseParser.ReleasesPage).AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { _status.Text = "Could not open GitHub: " + error.Message; }
        };
        FormClosing += (_, e) =>
        {
            if (_download is null) return;
            e.Cancel = true; _closeAfterCancel = true; _download.Cancel();
            _status.Text = "Canceling the download… Your current app has not changed.";
        };
        Shown += (_, _) =>
        {
            var area = Screen.FromHandle(Handle).WorkingArea;
            if (Height > area.Height) { MinimumSize = new Size(MinimumSize.Width, Math.Min(520, area.Height)); Height = area.Height; }
        };
    }

    private async Task InstallAsync()
    {
        if (StagedUpdate is not null) { DialogResult = DialogResult.OK; Close(); return; }
        if (Release is null || _download is not null) return;
        using var cancellation = new CancellationTokenSource();
        _download = cancellation;
        _install.Enabled = _automatic.Enabled = _github.Enabled = _components.Enabled = false;
        _close.Text = "Cancel"; _progress.Visible = true;
        var progress = new Progress<HubUpdateProgress>(update =>
        {
            if (IsDisposed || Disposing || _download is null) return;
            _status.Text = update.Phase + (update.TotalBytes > 0 ? $" — {update.CompletedBytes / 1048576.0:0.0} / {update.TotalBytes / 1048576.0:0.0} MB" : "…");
            _progress.Style = update.TotalBytes > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (update.TotalBytes > 0) _progress.Value = (int)Math.Clamp(update.CompletedBytes * 1000.0 / update.TotalBytes, 0, 1000);
        });
        try
        {
            StagedUpdate = await _stage(Release, progress, cancellation.Token);
            _status.Text = "Download and file checks passed. Click Restart and update to apply it in this folder.";
            _install.Text = "Restart and update";
        }
        catch (OperationCanceledException) { _status.Text = "Update canceled. Your current app is unchanged."; }
        catch (Exception error) { _status.Text = "Update could not be prepared. Your current app is unchanged.\n" + error.Message; }
        finally
        {
            _download = null;
            _progress.Visible = false;
            _install.Enabled = _automatic.Enabled = _github.Enabled = _components.Enabled = true;
            _close.Text = "Close";
            if (_closeAfterCancel) Close();
        }
    }

    private static DarkButton Button(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(6, 0, 0, 0),
        BackColor = HubForm.Raised, ForeColor = Color.WhiteSmoke,
    };
}
