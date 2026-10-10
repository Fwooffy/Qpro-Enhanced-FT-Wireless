namespace QproFaceTracking.Hub;

// Release notes are shown as plain text; remote content is never rendered as HTML.
internal sealed class HubUpdateDialog : Form
{
    private readonly DarkButton _install = Button("Update");
    private readonly DarkButton _close = Button("Close");
    private readonly DarkButton _github = Button("View release on GitHub");
    private readonly DarkButton _components = Button("Components");
    private readonly CheckBox _automatic = new() { Text = "Check automatically when the Hub opens", AutoSize = true };
    private readonly CheckBox _prereleases = new() { Text = "Include prereleases (test builds)", AutoSize = true };
    private readonly DarkButton _checkAgain = Button("Check again");
    private readonly Label _title = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10), UseMnemonic = false };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = HubForm.Muted,
        Margin = new Padding(0, 0, 0, 14), UseMnemonic = false };
    private readonly RichTextBox _notes = new() { ReadOnly = true, DetectUrls = false, Dock = DockStyle.Fill,
        BackColor = HubForm.Inset, ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = HubForm.Muted, Margin = new Padding(0, 8, 0, 8) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 12, Visible = false, Maximum = 1000 };
    private readonly Func<HubUpdateRelease, IProgress<HubUpdateProgress>, CancellationToken, Task<HubStagedUpdate>> _stage;
    private readonly Func<bool, CancellationToken, Task<HubUpdateCheck>>? _checkUpdates;
    private readonly Version _current;
    private readonly string? _blocked;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private CancellationTokenSource? _checkRequest;
    private long _checkGeneration;
    private bool _lifetimeReleased;
    private CancellationTokenSource? _download;
    private bool _closeAfterCancel;
    internal bool CheckAutomatically => _automatic.Checked;
    internal bool IncludePrereleases => _prereleases.Checked;
    internal HubUpdateCheck? Check { get; private set; }
    internal HubStagedUpdate? StagedUpdate { get; private set; }
    internal HubUpdateRelease? Release => Check?.Release;
    internal bool OpenComponents { get; private set; }

    internal HubUpdateDialog(HubUpdateCheck check, Version current, bool automatic, string? blocked,
        Func<HubUpdateRelease, IProgress<HubUpdateProgress>, CancellationToken, Task<HubStagedUpdate>> stage,
        bool includePrereleases = false,
        Func<bool, CancellationToken, Task<HubUpdateCheck>>? checkUpdates = null)
    {
        _stage = stage;
        _checkUpdates = checkUpdates; _current = current; _blocked = blocked;
        _lifetimeToken = _lifetime.Token;
        Text = "Qpro updates";
        DoubleBuffered = true;
        BackColor = HubForm.Background; ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(760, 670); MinimumSize = new Size(610, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        _automatic.Checked = automatic;
        _prereleases.Checked = includePrereleases;
        var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _title.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
        layout.Controls.Add(_title, 0, 0);
        layout.Controls.Add(_summary, 0, 1);
        _notes.Font = Font; _notes.AccessibleName = "Release notes";
        layout.Controls.Add(_notes, 0, 2);
        var preferences = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 12, 0, 0) };
        preferences.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        preferences.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        preferences.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        preferences.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _automatic.Margin = new Padding(0, 0, 0, 6);
        _prereleases.Margin = new Padding(0);
        _prereleases.AccessibleDescription = "Also check newer test releases. Downloads and installation still require Update.";
        preferences.Controls.Add(_automatic, 0, 0); preferences.Controls.Add(_prereleases, 0, 1);
        _checkAgain.Anchor = AnchorStyles.Right;
        _checkAgain.Margin = new Padding(12, 0, 0, 0);
        preferences.Controls.Add(_checkAgain, 1, 0); preferences.SetRowSpan(_checkAgain, 2);
        layout.Controls.Add(preferences, 0, 3);
        layout.Controls.Add(_status, 0, 4);
        layout.Controls.Add(_progress, 0, 5);
        var actions = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        actions.Controls.Add(_close); actions.Controls.Add(_install); actions.Controls.Add(_github); actions.Controls.Add(_components);
        layout.Controls.Add(actions, 0, 6);
        Controls.Add(layout);
        ShowCheck(check);
        _prereleases.CheckedChanged += async (_, _) => await CheckAgainAsync();
        _checkAgain.Click += async (_, _) => await CheckAgainAsync();
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
        FormClosed += (_, _) => EndLifetime(releaseSource: false);
        Disposed += (_, _) => EndLifetime(releaseSource: true);
        Shown += (_, _) =>
        {
            var area = Screen.FromHandle(Handle).WorkingArea;
            if (Height > area.Height) { MinimumSize = new Size(MinimumSize.Width, Math.Min(520, area.Height)); Height = area.Height; }
        };
    }

    private void ShowCheck(HubUpdateCheck check)
    {
        Check = check;
        _title.Text = check.Release is { } release ? "QproFaceTracking V" + release.Version +
            (release.PrereleaseSuffix is { } suffix ? "-" + suffix : string.Empty) +
            (release.IsPrerelease ? " · Prerelease" : string.Empty) : "App updates";
        _summary.Text = $"Installed: V{_current}\n{check.Message}\n\n" +
            "Update this app in the same folder. Your models, recordings, options and connections are kept. The Hub restarts to apply it. " +
            "Use Components for Qpro module, runtime and GPU library updates.";
        _notes.Text = check.Release?.Notes ?? (IncludePrereleases
            ? "Stable releases and prereleases are checked on the project's public GitHub releases page."
            : "Stable updates are checked on the project's public GitHub releases page.");
        _status.Text = _blocked ?? (check.State == HubUpdateCheckState.Available
            ? check.Release?.IsPrerelease == true ? "This is a test build and may contain bugs. Click Update to download it."
                : "Ready to download. Nothing is installed until you click Update."
            : check.Message);
        _status.ForeColor = _blocked is not null || check.State == HubUpdateCheckState.ManualDownloadOnly || check.Release?.IsPrerelease == true
            ? HubForm.Warning : check.State == HubUpdateCheckState.Unavailable ? HubForm.Bad : HubForm.Good;
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool idle = _download is null;
        _install.Enabled = idle && _checkRequest is null && _blocked is null &&
            (StagedUpdate is not null || Check?.State == HubUpdateCheckState.Available && Release?.CanInstall == true);
        _automatic.Enabled = idle;
        _prereleases.Enabled = idle && StagedUpdate is null && _checkUpdates is not null;
        _checkAgain.Enabled = _prereleases.Enabled && _checkRequest is null;
        _github.Enabled = _components.Enabled = idle;
    }

    private async Task CheckAgainAsync()
    {
        if (_checkUpdates is null || _download is not null || StagedUpdate is not null || IsDisposed || Disposing) return;
        _checkRequest?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _checkRequest = cancellation;
        long generation = ++_checkGeneration;
        bool includePrereleases = IncludePrereleases;
        Check = null;
        _status.ForeColor = HubForm.Muted;
        _status.Text = includePrereleases ? "Checking stable releases and prereleases…" : "Checking stable releases…";
        _notes.Text = "Checking GitHub for the selected release channel…";
        UpdateActions();
        try
        {
            HubUpdateCheck result = await _checkUpdates(includePrereleases, cancellation.Token);
            if (!IsDisposed && !Disposing && !cancellation.IsCancellationRequested && generation == _checkGeneration)
                ShowCheck(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing && !cancellation.IsCancellationRequested && generation == _checkGeneration)
                ShowCheck(new(HubUpdateCheckState.Unavailable, "Updates could not be checked: " + error.Message));
        }
        finally
        {
            if (generation == _checkGeneration)
            {
                _checkRequest = null;
                if (!IsDisposed && !Disposing) UpdateActions();
            }
        }
    }

    private void EndLifetime(bool releaseSource)
    {
        if (_lifetimeReleased) return;
        if (releaseSource) _lifetimeReleased = true;
        try { _lifetime.Cancel(); }
        finally { if (releaseSource) _lifetime.Dispose(); }
    }

    private async Task InstallAsync()
    {
        if (StagedUpdate is not null) { DialogResult = DialogResult.OK; Close(); return; }
        if (Release is not { } release || !_install.Enabled || _download is not null || _checkRequest is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _download = cancellation;
        UpdateActions();
        _status.ForeColor = HubForm.Muted;
        _close.Text = "Cancel"; _progress.Visible = true;
        var progress = new Progress<HubUpdateProgress>(update =>
        {
            if (IsDisposed || Disposing || !ReferenceEquals(_download, cancellation) || cancellation.IsCancellationRequested) return;
            _status.Text = update.Phase + (update.TotalBytes > 0 ? $" — {update.CompletedBytes / 1048576.0:0.0} / {update.TotalBytes / 1048576.0:0.0} MB" : "…");
            _progress.Style = update.TotalBytes > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (update.TotalBytes > 0) _progress.Value = (int)Math.Clamp(update.CompletedBytes * 1000.0 / update.TotalBytes, 0, 1000);
        });
        try
        {
            StagedUpdate = await _stage(release, progress, cancellation.Token);
            if (IsDisposed || Disposing) return;
            _status.ForeColor = HubForm.Good;
            _status.Text = "Download and file checks passed. Click Restart and update to apply it in this folder.";
            _install.Text = "Restart and update";
        }
        catch (OperationCanceledException)
        { if (!IsDisposed && !Disposing) { _status.ForeColor = HubForm.Warning; _status.Text = "Update canceled. Your current app is unchanged."; } }
        catch (Exception error)
        { if (!IsDisposed && !Disposing) { _status.ForeColor = HubForm.Bad; _status.Text = "Update could not be prepared. Your current app is unchanged.\n" + error.Message; } }
        finally
        {
            _download = null;
            if (!IsDisposed && !Disposing)
            {
                _progress.Visible = false;
                UpdateActions();
                _close.Text = "Close";
                if (_closeAfterCancel) Close();
            }
        }
    }

    private static DarkButton Button(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(6, 0, 0, 0),
        BackColor = HubForm.Raised, ForeColor = Color.WhiteSmoke,
    };
}
