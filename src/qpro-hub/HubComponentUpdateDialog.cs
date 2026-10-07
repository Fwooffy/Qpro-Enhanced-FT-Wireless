namespace QproFaceTracking.Hub;

internal sealed class HubComponentUpdateDialog : Form
{
    private readonly DarkButton _update = new() { Text = "Update components", AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    private readonly DarkButton _close = new() { Text = "Close", AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false };
    private readonly Func<HubComponentUpdate, Action<string>, Task<string?>> _apply;
    private readonly HubComponentPlan _plan;
    private readonly RichTextBox _detail;
    private readonly HubActivityClassifier _classifier = new();
    private bool _running;

    internal HubComponentUpdateDialog(HubComponentPlan plan, Func<HubComponentUpdate, Action<string>, Task<string?>> apply)
    {
        _plan = plan; _apply = apply;
        Text = "Qpro component updates";
        Font = new Font("Segoe UI", 10F);
        ForeColor = Color.WhiteSmoke; BackColor = HubForm.Background;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(720, 560); MinimumSize = new Size(560, 430);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Keep tracking components up to date", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 12) }, 0, 0);
        layout.Controls.Add(new Label { Text = "This release's checked requirements are used. Close VRCFaceTracking for a module update and stop Qpro tracking or training. Models, captures, settings and your streaming app are kept.", AutoSize = true, Dock = DockStyle.Fill, ForeColor = HubForm.Muted, Margin = new Padding(0, 0, 0, 14) }, 0, 1);
        _detail = new RichTextBox { ReadOnly = true, DetectUrls = false, Dock = DockStyle.Fill,
            BackColor = HubForm.Inset, ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None,
            Font = Font, AccessibleName = "Component update plan" };
        _detail.Text = string.Join("\n\n", plan.Updates.Select(item => item.Name + "\n" + item.Detail).Concat(plan.Notes));
        if (_detail.Text.Length == 0) _detail.Text = "Installed components match this release. Components that are not installed remain optional in First-time setup.";
        layout.Controls.Add(_detail, 0, 2);
        SetStatus(plan.Updates.Count == 0 ? plan.Notes.Count > 0 ? "Some components could not be checked. Read the guidance above." : "No component updates are needed."
            : "Ready. Large GPU packages may take several minutes to download. Nothing changes until you click Update components.",
            plan.Notes.Count > 0 ? HubForm.Warning : HubForm.Good);
        _status.Margin = new Padding(0, 14, 0, 14);
        layout.Controls.Add(_status, 0, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(_close); actions.Controls.Add(_update); layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        _update.Enabled = plan.Updates.Count > 0;
        _update.Click += async (_, _) => await ApplyAsync();
        _close.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true;
            SetStatus("Keep this window open until the current component finishes. Download and verification progress appears above.", HubForm.Warning);
        };
        Shown += (_, _) =>
        {
            var area = Screen.FromHandle(Handle).WorkingArea;
            if (Height > area.Height) { MinimumSize = new Size(MinimumSize.Width, Math.Min(430, area.Height)); Height = area.Height; }
        };
    }

    private async Task ApplyAsync()
    {
        if (_running) return;
        _running = true; _update.Enabled = _close.Enabled = false;
        try
        {
            foreach (var item in _plan.Updates)
            {
                _detail.Clear();
                _classifier.Reset();
                SetStatus("Updating " + item.Name + "…", HubForm.Good);
                AppendProgress("Starting " + item.Name + "…");
                string? failure = await _apply(item, AppendProgress);
                if (failure is not null)
                {
                    SetStatus(item.Name + " needs attention: " + failure + " Reopen Component updates to retry. Earlier completed updates are kept.", HubForm.Bad);
                    return;
                }
            }
            SetStatus("Component updates completed and checked. Reopen VRCFaceTracking if its module was updated.", HubForm.Good);
        }
        catch (Exception error) { SetStatus("Component updates stopped: " + error.Message + " Check the detailed log before retrying.", HubForm.Bad); }
        finally { _running = false; _close.Enabled = true; }
    }

    private void SetStatus(string text, Color color) { _status.Text = text; _status.ForeColor = color; }

    private void AppendProgress(string line)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => AppendProgress(line))); return; }
        // Keep the modal readable during long pip downloads without retaining
        // an unbounded copy of Activity's full diagnostic history.
        if (_detail.TextLength > 18_000)
        {
            _detail.Text = _detail.Text[^12_000..];
        }
        _detail.SelectionStart = _detail.TextLength; _detail.SelectionLength = 0;
        _detail.SelectionColor = _classifier.Classify(line) switch
        {
            ActivitySeverity.Error => HubForm.Bad,
            ActivitySeverity.Warning => HubForm.Warning,
            _ => HubForm.Good,
        };
        _detail.AppendText((line.Length > 1500 ? line[..1500] + "…" : line) + Environment.NewLine);
        _detail.ScrollToCaret();
    }
}
