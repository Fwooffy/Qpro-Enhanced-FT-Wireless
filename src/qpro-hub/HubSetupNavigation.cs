using System.Drawing;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private Action<int>? _selectSetupStep;
    private Action? _fitSetupNavigationDpi;
    private Action? _refreshSetupNavigationEmphasis;
    private int _selectedSetupStep;
    private bool _setupRuntimeObservedReady;
    private bool _setupModuleObservedCurrent;
    private int _setupModuleObservedSource = -1;

    private void UpdateSetupNavigationReadiness(bool runtimeReady, bool moduleFilesCurrent)
    {
        // These values come from the existing status observation. Presentation
        // never adds a probe or treats installed module files as live tracking.
        var observedSource = _previewOnly ? _trackingSourceSetup.SelectedIndex
            : _environment.SteamLinkSelected ? 1 : 0;
        if (_setupRuntimeObservedReady == runtimeReady && _setupModuleObservedCurrent == moduleFilesCurrent
            && _setupModuleObservedSource == observedSource) return;
        _setupRuntimeObservedReady = runtimeReady;
        _setupModuleObservedCurrent = moduleFilesCurrent;
        _setupModuleObservedSource = observedSource;
        _refreshSetupNavigationEmphasis?.Invoke();
    }

    // Static previews select the same view as a user would. Selection never
    // invokes installation, a device helper, or a settings write.
    internal void SelectSetupStepForPreview(int step)
    {
        if (_previewOnly && !IsDisposed && !Disposing)
            _selectSetupStep?.Invoke(Math.Clamp(step, 0, 2));
    }

    private Control BuildGuidedSetupWorkspace(Panel page, Control[] stepCards, Action openFacialTracking)
    {
        foreach (var action in new[] { _reconnectUsbButton, _connectWirelessButton, _setupRuntimeButton, _setupBridgeButton, _setupSteamLinkModuleButton })
            action.VisualStyle = HubButtonStyle.Primary;
        _reconnectUsbButton.Text = "Check USB connection";
        _reconnectUsbButton.AccessibleName = "Check USB connection";
        var workspace = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 0, 14),
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) workspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        workspace.Controls.Add(_setupProgressContainer, 0, 0);

        var overview = Card();
        overview.ColumnCount = 1;
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overview.Padding = new Padding(16, 13, 16, 12);
        overview.Margin = new Padding(0, 0, 0, 12);
        var title = SectionTitle("Three required steps", HubIcon.Setup);
        title.Margin = new Padding(0, 0, 0, 6);
        overview.Controls.Add(title);
        var prerequisites = Info("Start with a rooted Quest Pro and the latest VRCFaceTracking from Steam.");
        prerequisites.Margin = new Padding(0, 0, 0, 10);
        overview.Controls.Add(prerequisites);

        var steps = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 68,
            ColumnCount = 3, RowCount = 1, Margin = Padding.Empty,
        };
        for (var column = 0; column < 3; column++)
            steps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        steps.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        var stepNames = new[] { "Connect", "PC runtime", "VRCFT module" };
        var stepStatuses = new[] { _usbStatus, _runtimeStatus, _bridgeStatus };
        var buttons = new DarkButton[3];
        var stepItems = new TableLayoutPanel[3];
        var statusLabels = new Label[3];
        for (var index = 0; index < 3; index++)
        {
            var selected = index;
            var item = new BufferedTableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = false,
                ColumnCount = 1, RowCount = 2, Margin = new Padding(index == 0 ? 0 : 4, 0, index == 2 ? 0 : 4, 0),
            };
            stepItems[index] = item;
            item.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            item.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            item.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var button = SecondaryButton($"{index + 1}. {stepNames[index]}");
            button.Enabled = true;
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Margin = Padding.Empty;
            button.Padding = new Padding(6, 4, 6, 4);
            button.AccessibleName = $"Setup step {index + 1}: {stepNames[index]}";
            button.AccessibleDescription = "Show this setup step. No installation runs when changing steps.";
            button.Click += (_, _) => _selectSetupStep?.Invoke(selected);
            buttons[index] = button;
            item.Controls.Add(button, 0, 0);
            var status = MirrorSetupStatus(stepStatuses[index]);
            statusLabels[index] = status;
            status.Font = new Font(UiFontName, 8.5F);
            status.Margin = new Padding(2, 6, 2, 0);
            item.Controls.Add(status, 0, 1);
            steps.Controls.Add(item, index, 0);
        }
        overview.Controls.Add(steps);
        workspace.Controls.Add(overview, 0, 1);

        var body = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3, Margin = Padding.Empty,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < stepCards.Length; index++)
        {
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stepCards[index].Margin = Padding.Empty;
            stepCards[index].Visible = index == 0;
            body.Controls.Add(stepCards[index], 0, index);
        }
        workspace.Controls.Add(body, 0, 2);

        var navigation = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 10, 0, 0),
        };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var back = SecondaryButton("Back");
        back.AutoSize = true;
        back.Dock = DockStyle.Fill;
        back.MinimumSize = new Size(84, 0);
        back.Margin = Padding.Empty;
        back.AccessibleName = "Previous setup step";
        var position = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Muted, Margin = new Padding(8, 0, 8, 0), AutoEllipsis = true,
        };
        var next = SecondaryButton("Next step");
        next.Enabled = true;
        next.Dock = DockStyle.Fill;
        next.Margin = Padding.Empty;
        next.MinimumSize = new Size(150, 0);
        back.Click += (_, _) => _selectSetupStep?.Invoke(_selectedSetupStep - 1);
        next.Click += (_, _) =>
        {
            if (_selectedSetupStep < 2) _selectSetupStep?.Invoke(_selectedSetupStep + 1);
            else openFacialTracking();
        };
        navigation.Controls.Add(back, 0, 0);
        navigation.Controls.Add(position, 1, 0);
        navigation.Controls.Add(next, 2, 0);
        workspace.Controls.Add(navigation, 0, 3);

        var fittingOverview = false;
        void FitStepOverview()
        {
            if (fittingOverview || steps.ClientSize.Width <= 0) return;
            fittingOverview = true;
            steps.SuspendLayout();
            try
            {
                var scale = DeviceDpi / 96F;
                int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
                var buttonHeight = Math.Max(Px(40), (int)Math.Ceiling(buttons[0].Font.GetHeight()) + Px(16));
                var rowHeight = buttonHeight;
                // A fixed-width grid must wrap its status text before asking
                // for height. Autosized percent columns retain the old label
                // width after shrinking and can push the last step off-page.
                for (var index = 0; index < 3; index++)
                {
                    var columnWidth = steps.ClientSize.Width / 3
                        + (index < steps.ClientSize.Width % 3 ? 1 : 0);
                    steps.ColumnStyles[index].Width = columnWidth;
                    var item = stepItems[index];
                    var status = statusLabels[index];
                    item.RowStyles[0].Height = buttonHeight;
                    var textWidth = Math.Max(1, columnWidth - item.Margin.Horizontal - status.Margin.Horizontal);
                    var maximum = new Size(textWidth, 0);
                    if (status.MaximumSize != maximum) status.MaximumSize = maximum;
                    rowHeight = Math.Max(rowHeight, buttonHeight + status.Margin.Vertical
                        + status.GetPreferredSize(maximum).Height + item.Margin.Vertical);
                }
                if (steps.RowStyles[0].Height != rowHeight) steps.RowStyles[0].Height = rowHeight;
                if (steps.Height != rowHeight) steps.Height = rowHeight;
            }
            finally { steps.ResumeLayout(true); fittingOverview = false; }
        }
        steps.SizeChanged += (_, _) => FitStepOverview();
        foreach (var status in statusLabels) status.TextChanged += (_, _) => FitStepOverview();
        _fitSetupNavigationDpi = () =>
        {
            var scale = DeviceDpi / 96F;
            int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
            var buttonHeight = Math.Max(Px(40), (int)Math.Ceiling(buttons[0].Font.GetHeight()) + Px(16));
            navigation.RowStyles[0].Height = buttonHeight;
            FitStepOverview();
        };
        _fitSetupNavigationDpi();

        _refreshSetupNavigationEmphasis = () =>
        {
            var currentModuleFiles = _setupModuleObservedCurrent
                && _setupModuleObservedSource == _trackingSourceSetup.SelectedIndex;
            _setupRuntimeButton.VisualStyle = _setupRuntimeObservedReady
                ? HubButtonStyle.Secondary : HubButtonStyle.Primary;
            _setupBridgeButton.VisualStyle = _setupSteamLinkModuleButton.VisualStyle = currentModuleFiles
                ? HubButtonStyle.Secondary : HubButtonStyle.Primary;
            next.VisualStyle = _selectedSetupStep == 2 && _setupRuntimeObservedReady && currentModuleFiles
                ? HubButtonStyle.Primary : HubButtonStyle.Secondary;
        };
        _trackingSourceSetup.SelectedIndexChanged += (_, _) => _refreshSetupNavigationEmphasis();

        _selectSetupStep = requested =>
        {
            _selectedSetupStep = Math.Clamp(requested, 0, 2);
            body.SuspendLayout();
            try
            {
                for (var index = 0; index < stepCards.Length; index++)
                {
                    stepCards[index].Visible = index == _selectedSetupStep;
                    buttons[index].BackColor = index == _selectedSetupStep ? Selected : Raised;
                    buttons[index].Emphasized = index == _selectedSetupStep;
                    buttons[index].AccessibleDescription = index == _selectedSetupStep
                        ? "Current setup step. Changing steps does not run an installation."
                        : "Show this setup step. No installation runs when changing steps.";
                }
                back.Enabled = _selectedSetupStep > 0;
                position.Text = $"Step {_selectedSetupStep + 1} of 3";
                next.Text = _selectedSetupStep == 2 ? "Open Facial tracking" : "Next step";
                next.AccessibleName = _selectedSetupStep == 2 ? "Open Facial tracking" : "Next setup step";
                _refreshSetupNavigationEmphasis();
            }
            finally { body.ResumeLayout(true); }
            page.AutoScrollPosition = Point.Empty;
        };
        _selectSetupStep(0);
        return workspace;
    }

    private static Label MirrorSetupStatus(Label source)
    {
        var mirror = new Label
        {
            AutoSize = true, ForeColor = source.ForeColor, Text = source.Text,
            Tag = "setup-overview-status", Margin = Padding.Empty,
        };
        // Keep the original status labels where the existing refresh pipeline
        // expects them; both views display exactly the same observed state.
        source.TextChanged += (_, _) => mirror.Text = source.Text;
        source.ForeColorChanged += (_, _) => mirror.ForeColor = source.ForeColor;
        return mirror;
    }
}
