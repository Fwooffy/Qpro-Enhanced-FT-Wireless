using System.Drawing;
using System.Diagnostics;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    partial void StyleEditionChrome(Label brandTitle, Label brandSubtitle, TableLayoutPanel header, Label pageTitle, Label pageSubtitle);
    private readonly CheckBox _launchWithSteamVr = FeatureToggle("Launch with SteamVR", false);
    private readonly Label _steamVrStartupStatus = Info("Checking SteamVR startup settings…");
    private readonly DarkButton _useBackupOutputButton = SecondaryButton("Use VRCFaceTracking backup");
    private readonly HubOutputRouteSwitch _outputRouteSetup = new();
    private readonly DarkButton _installBackupModuleButton = SetupButton("Install Qpro backup module");
    private Action? _flushResponsiveLayout;
    private int _readinessUpdateDepth;
    private Action<bool>? _readinessBatchState;

    private IDisposable BeginReadinessUpdate()
    {
        if (_readinessUpdateDepth++ == 0) _readinessBatchState?.Invoke(true);
        return new ReadinessUpdateScope(this);
    }

    private sealed class ReadinessUpdateScope(HubForm form) : IDisposable
    {
        private HubForm? _form = form;
        public void Dispose()
        {
            var owner = _form;
            if (owner is null) return;
            _form = null;
            if (--owner._readinessUpdateDepth == 0 && !owner.IsDisposed && !owner.Disposing)
                owner._readinessBatchState?.Invoke(false);
        }
    }

    internal void FlushPreviewLayout()
    {
        if (_previewOnly && !IsDisposed && !Disposing) _flushResponsiveLayout?.Invoke();
    }

    private Control BuildLayout()
    {
        var shell = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));

        var pages = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty };
        var navigation = new List<(DarkButton Tab, Panel Page, string Subtitle)>();
        (DarkButton, Panel) AddPage(string title, HubIcon icon, string subtitle)
        {
            var tab = NavigationButton(title, icon);
            var page = NewPage();
            page.AccessibleName = title;
            navigation.Add((tab, page, subtitle));
            return (tab, page);
        }
        var (setupTab, setupPage) = AddPage("First-time setup", HubIcon.Setup, "Choose how to send face and eye tracking to VRChat.");
        var (liveTab, livePage) = AddPage("Facial tracking", HubIcon.Headset, "Check native output and choose optional tracking features.");
        var (_, controllersPage) = AddPage("Controllers", HubIcon.Controller, "Choose optional hand and controller inputs for Virtual Desktop.");
        var (_, personalizationPage) = AddPage("Personalize", HubIcon.Sliders, "Record tongue poses and train a model for your own face.");
        var (_, modelsPage) = AddPage("Model manager", HubIcon.Models, "Name, import, export, and manage personal models.");
#if QPRO_REMOTE_CLICKER
        var (_, clickerPage) = AddPage("Remote clicker", HubIcon.Speaker, "Send a click to a friend or listen on this PC.");
        clickerPage.VisibleChanged += (_, _) =>
        {
            if (clickerPage.Visible && clickerPage.Controls.Count == 0)
                clickerPage.Controls.Add(new RemoteClickerPanel());
        };
#endif
        var (activityTab, activityPage) = AddPage("Activity", HubIcon.Activity, "Follow setup, training, and tracking output here.");
        var tabs = navigation.Select(item => item.Tab).ToArray();
        var pageList = navigation.Select(item => item.Page).ToArray();

        var sidebarViewport = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ShellSurface, Margin = Padding.Empty };
        var sidebar = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 524,
            ColumnCount = 1,
            RowCount = tabs.Length + 2,
            BackColor = ShellSurface,
            Padding = new Padding(12, 24, 12, 16),
            Margin = Padding.Empty,
        };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        foreach (var _ in tabs) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        var brand = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var brandTitle = new Label { Text = ProductDisplayName, AccessibleName = "Product name", Dock = DockStyle.Fill, Font = new Font(UiFontName, 14F, FontStyle.Bold), ForeColor = TextPrimary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var brandSubtitle = new Label { Text = "Quest Pro tracking", Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
        brand.Controls.Add(brandTitle, 0, 0);
        brand.Controls.Add(brandSubtitle, 0, 1);
        sidebar.Controls.Add(brand, 0, 0);

        for (var index = 0; index < tabs.Length; index++) sidebar.Controls.Add(tabs[index], 0, index + 1);
        var sidebarNotice = new Label
        {
            Text = "Headset tools require root\nGaze and pupil are experimental",
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font(UiFontName, 8.5F),
        };
        sidebar.Controls.Add(sidebarNotice, 0, tabs.Length + 1);
        sidebarViewport.Controls.Add(sidebar);
        shell.Controls.Add(sidebarViewport, 0, 0);
        void FitSidebarWidth()
        {
            var dpiScale = shell.DeviceDpi / 96F;
            var textWidth = tabs.Max(tab => TextRenderer.MeasureText(tab.Text, tab.Font).Width);
            var requiredWidth = Math.Max(textWidth + sidebar.Padding.Horizontal + (int)Math.Ceiling(54 * dpiScale),
                TextRenderer.MeasureText(brandTitle.Text, brandTitle.Font).Width + sidebar.Padding.Horizontal
                    + brand.Margin.Horizontal + brandTitle.Margin.Horizontal);
#if QPRO_REMOTE_CLICKER
            // The seventh tab can introduce a scrollbar at compact sizes.
            // Keep the edition name and the navigation labels readable beside it.
            requiredWidth += SystemInformation.VerticalScrollBarWidth + (int)Math.Ceiling(6 * dpiScale);
#endif
            var sidebarWidth = Math.Max(
                Math.Clamp((int)(shell.ClientSize.Width * 0.2), (int)(180 * dpiScale), (int)(226 * dpiScale)),
                requiredWidth);
            if ((int)shell.ColumnStyles[0].Width != sidebarWidth)
                shell.ColumnStyles[0].Width = sidebarWidth;
        }
        shell.SizeChanged += (_, _) => FitSidebarWidth();

        var main = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Background, Margin = Padding.Empty };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(42, 16, 42, 7), Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        var pageTitle = new Label { Dock = DockStyle.Fill, Font = new Font(UiFontName, 18F, FontStyle.Bold), ForeColor = TextPrimary, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        var pageSubtitle = new Label { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
        header.Controls.Add(pageTitle, 0, 0);
        _updateLink.AutoSize = true;
        _updateLink.Anchor = AnchorStyles.Right;
        _updateLink.TextAlign = ContentAlignment.MiddleRight;
        _updateLink.Margin = new Padding(16, 0, 0, 0);
        header.Controls.Add(_updateLink, 1, 0);
        header.Controls.Add(pageSubtitle, 0, 1);
        header.SetColumnSpan(pageSubtitle, 2);
        StyleEditionChrome(brandTitle, brandSubtitle, header, pageTitle, pageSubtitle);
        main.Controls.Add(header, 0, 0);
        main.Controls.Add(pages, 0, 1);
        shell.Controls.Add(main, 1, 0);

        int PageColumnWidth(Panel page)
        {
            var scale = DeviceDpi / 96F;
            int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
            // Reserve the scrollbar once, whether or not it is currently visible.
            // Wrapping must not change merely because the viewport gains a bar.
            return Math.Min(Px(1160), Math.Max(1,
                page.Width - Px(44) - SystemInformation.VerticalScrollBarWidth));
        }
        void FitPageWidth(Panel page)
        {
            if (WindowState == FormWindowState.Minimized) return;
            // All pages share the same readable column and leave small windows
            // free to shrink without creating a horizontal scrollbar.
            var scale = DeviceDpi / 96F;
            int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
            var columnWidth = PageColumnWidth(page);
            var extra = Math.Max(0, page.ClientSize.Width - Px(44) - columnWidth);
            var padding = new Padding(Px(22), Px(8), Px(22) + extra, Px(20));
            if (page.Padding != padding) page.Padding = padding;
        }
        Panel NewPage()
        {
            // Each page scrolls independently while the tracking controls remain
            // fixed in the footer, including at small window sizes.
            var page = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background, Padding = new Padding(22, 8, 22, 20), Visible = false, Tag = "hub-page" };
            pages.Controls.Add(page);
            page.ClientSizeChanged += (_, _) => FitPageWidth(page);
            FitPageWidth(page);
            return page;
        }

        var liveLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Padding = Padding.Empty, Margin = Padding.Empty };
        liveLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 2; row++) liveLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        livePage.Controls.Add(liveLayout);
        var liveFieldTables = new List<TableLayoutPanel>();
        void LiveRows(TableLayoutPanel card, int row)
        {
            card.RowCount = Math.Max(card.RowCount, row + 1);
            while (card.RowStyles.Count <= row) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        TableLayoutPanel LiveFields()
        {
            var card = Card();
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.ColumnCount = 2;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            liveFieldTables.Add(card);
            return card;
        }
        void LiveField(TableLayoutPanel card, int row, string text, Control control)
        {
            LiveRows(card, row);
            var label = FieldLabel(text);
            label.TabIndex = row * 2;
            card.Controls.Add(label, 0, row);
            control.TabIndex = row * 2 + 1;
            control.AccessibleName = text;
            control.Margin = new Padding(0, 3, 0, 3);
            card.Controls.Add(control, 1, row);
        }
        void Span(TableLayoutPanel card, Control control, int row)
        {
            LiveRows(card, row);
            control.TabIndex = row * 2;
            card.Controls.Add(control, 0, row);
            card.SetColumnSpan(control, card.ColumnCount);
        }
        Button Details(TableLayoutPanel card, Control body, string text, int row)
        {
            body.Visible = false;
            var button = DisclosureButton("Show " + text);
            button.Enabled = true;
            button.Margin = new Padding(0, 4, 8, 4);
            button.AccessibleName = button.Text;
            button.Click += (_, _) =>
            {
                body.Visible = !body.Visible;
                button.Text = (body.Visible ? "Hide " : "Show ") + text;
                button.AccessibleName = button.Text;
            };
            Span(card, button, row);
            Span(card, body, row + 1);
            return button;
        }

        var liveSource = LiveFields();
        liveSource.Padding = new Padding(20, 14, 20, 12);
        Span(liveSource, SectionTitle("Your session", HubIcon.Headset), 0);
        LiveField(liveSource, 1, "Streaming app", _trackingSourceLive);
        var statuses = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = false,
            ColumnCount = 2, RowCount = 4, Margin = new Padding(0, 8, 0, 3) };
        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 4; row++) statuses.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var statusItems = new[]
        {
            ("Face output", _directVrchatStatus), ("SteamVR", _steamStatus),
            ("Quest ADB", _usbStatus), ("PC runtime", _runtimeStatus),
        };
        var readinessItems = new List<TableLayoutPanel>();
        for (var index = 0; index < statusItems.Length; index++)
        {
            var item = new BufferedTableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2,
                Margin = new Padding(0, 0, 18, 4), TabStop = false };
            item.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            item.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            item.Controls.Add(new Label { Text = statusItems[index].Item1 + ":", AutoSize = true,
                ForeColor = Muted, Margin = new Padding(0, 0, 10, 0) }, 0, 0);
            statusItems[index].Item2.Margin = Padding.Empty;
            item.Controls.Add(statusItems[index].Item2, 1, 0);
            statuses.Controls.Add(item, index % 2, index / 2);
            readinessItems.Add(item);
        }
        bool fittingReadiness = false;
        bool readinessDirty = true;
        void FitLiveReadiness()
        {
            // Hidden pages accumulate changes until shown. The visible page
            // still fits synchronously during a completed native size change.
            if (fittingReadiness || _readinessUpdateDepth > 0 || !livePage.Visible ||
                WindowState == FormWindowState.Minimized) return;
            readinessDirty = false;
            fittingReadiness = true;
            statuses.SuspendLayout();
            try
            {
                var scale = DeviceDpi / 96F;
                // Choose columns from the full label/value pair, not a fixed
                // width breakpoint. Each status stays inline when a column can
                // fit it; smaller windows use fewer columns before wrapping.
                const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                var labelWidth = readinessItems.Select(item =>
                    TextRenderer.MeasureText(item.Controls[0].Text, item.Controls[0].Font, Size.Empty, flags).Width
                        + item.Controls[0].Margin.Horizontal).Max();
                var cellWidth = readinessItems.Select((item, index) =>
                {
                    var value = statusItems[index].Item2;
                    return labelWidth + TextRenderer.MeasureText(value.Text, value.Font, Size.Empty, flags).Width
                        + value.Margin.Horizontal + item.Margin.Horizontal + (int)Math.Ceiling(4 * scale);
                }).DefaultIfEmpty((int)Math.Ceiling(240 * scale)).Max();
                var columns = Math.Clamp(statuses.ClientSize.Width / Math.Max(1, cellWidth), 1, 3);
                var rows = (int)Math.Ceiling(statusItems.Length / (double)columns);
                if (statuses.ColumnCount != columns)
                {
                    // Change the shared grid once at each width breakpoint.
                    // Each status then stays aligned with the row beside it.
                    statuses.ColumnCount = columns;
                    statuses.RowCount = rows;
                    statuses.ColumnStyles.Clear();
                    statuses.RowStyles.Clear();
                    for (var column = 0; column < columns; column++)
                        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
                    for (var row = 0; row < rows; row++)
                        statuses.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    for (var index = 0; index < readinessItems.Count; index++)
                        statuses.SetCellPosition(readinessItems[index], new TableLayoutPanelCellPosition(index % columns, index / columns));
                }
                var rowHeights = new int[rows];
                for (var index = 0; index < statusItems.Length; index++)
                {
                    var item = readinessItems[index];
                    var name = (Label)item.Controls[0];
                    var value = statusItems[index].Item2;
                    var width = Math.Max(100, statuses.ClientSize.Width / columns - item.Margin.Horizontal);
                    item.SuspendLayout();
                    try
                    {
                        item.ColumnStyles[0].Width = labelWidth;
                        var maximum = new Size(Math.Max(1, width - labelWidth), 0);
                        if (value.MaximumSize != maximum) value.MaximumSize = maximum;
                        var nameSize = name.PreferredSize;
                        var valueSize = value.PreferredSize;
                        var height = Math.Max(nameSize.Height, valueSize.Height);
                        if (item.Height != height) item.Height = height;
                    }
                    finally { item.ResumeLayout(true); }
                    rowHeights[index / columns] = Math.Max(rowHeights[index / columns], item.Height + item.Margin.Vertical);
                }
                // Fix the total to the measured rows. Otherwise WinForms can
                // stretch the last flow row into blank space while autosizing.
                var totalHeight = rowHeights.Sum();
                if (statuses.Height != totalHeight) statuses.Height = totalHeight;
            }
            finally
            {
                try { statuses.ResumeLayout(true); }
                finally { fittingReadiness = false; }
            }
        }
        void RequestReadinessFit()
        {
            if (fittingReadiness) return;
            readinessDirty = true;
            FitLiveReadiness();
        }
        _readinessBatchState = suspended =>
        {
            if (suspended)
            {
                statuses.SuspendLayout();
                foreach (var item in readinessItems) item.SuspendLayout();
                return;
            }
            foreach (var item in readinessItems) item.ResumeLayout(false);
            statuses.ResumeLayout(false);
            if (readinessDirty) FitLiveReadiness();
        };
        statuses.SizeChanged += (_, _) => RequestReadinessFit();
        foreach (var (_, label) in statusItems) label.TextChanged += (_, _) => RequestReadinessFit();
        livePage.VisibleChanged += (_, _) => { if (livePage.Visible) RequestReadinessFit(); };
        Span(liveSource, statuses, 2);
        var connectionDetails = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Margin = Padding.Empty };
        LiveRows(connectionDetails, 1);
        var processingStatus = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        processingStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        processingStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var processingItems = new[] { ("Gaze support", _gazeStatus), ("Lower-face tracking", _inferenceStatus), ("Pupil tracking", _pupilStatus) };
        for (var index = 0; index < processingItems.Length; index++)
        {
            processingStatus.Controls.Add(FieldLabel(processingItems[index].Item1), 0, index);
            processingItems[index].Item2.Margin = new Padding(0, 4, 0, 4);
            processingStatus.Controls.Add(processingItems[index].Item2, 1, index);
        }
        connectionDetails.Controls.Add(processingStatus);
        var sessionCameraSettings = LiveFields();
        sessionCameraSettings.BackColor = Inset;
        sessionCameraSettings.Padding = new Padding(12);
        sessionCameraSettings.Margin = new Padding(0, 4, 0, 8);
        LiveField(sessionCameraSettings, 0, "Camera FPS cap", _fps);
        LiveField(sessionCameraSettings, 1, "Processing profile", _processingProfile);
        Span(sessionCameraSettings, _processingProfileNote, 2);
        Span(sessionCameraSettings, _cameraPreview, 3);
        Span(sessionCameraSettings, Info("Shared by tongue and pupils. Higher FPS uses more resources. Processing profiles limit pupil work without changing the tongue model. Preview is off by default; hiding it keeps tracking active. Apply changes by restarting tracking."), 4);
        connectionDetails.Controls.Add(sessionCameraSettings);
        connectionDetails.Controls.Add(_trackingStopSound);
        _trackingSourceLiveNote.Margin = new Padding(0, 2, 0, 8);
        connectionDetails.Controls.Add(_trackingSourceLiveNote);
        var connectionActions = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            WrapContents = true, Margin = Padding.Empty };
        connectionActions.Controls.Add(ActionButton("Open setup", (_, _) => setupTab.PerformClick()));
        connectionDetails.Controls.Add(connectionActions);
        connectionDetails.Controls.Add(Info("Enable OSC in VRChat and load a face-tracking avatar for expressions. Close other face tracking senders before using Companion."));
        var sessionDetails = Details(liveSource, connectionDetails, "connection and camera settings", 3);
        sessionDetails.Margin = new Padding(0, 2, 8, 0);
        var sessionActions = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            WrapContents = true, Margin = Padding.Empty };
        var checkVrchatLive = _checkVrchatLiveButton = ActionButton("Check VRChat connection", (_, _) => CheckVrchatConnection());
        checkVrchatLive.AccessibleDescription = "Check the current VRChat OSC connection and avatar input status. Confirm actual movement on your avatar.";
        checkVrchatLive.Margin = new Padding(0, 2, 8, 4);
        sessionActions.Controls.Add(checkVrchatLive);
        sessionActions.Controls.Add(ActionButton("Refresh connection status", (_, _) => { ReloadProfiles(); _ = RefreshStatusAsync(); }));
        _useBackupOutputButton.Margin = checkVrchatLive.Margin;
        _useBackupOutputButton.Visible = false;
        _useBackupOutputButton.Click += (_, _) =>
        {
            setupTab.PerformClick();
            _selectSetupStep?.Invoke(0);
        };
        sessionActions.Controls.Add(_useBackupOutputButton);
        sessionActions.Controls.Add(sessionDetails);
        Span(liveSource, sessionActions, 3);
        var liveRouteNote = Info("");
        Span(liveSource, liveRouteNote, 5);
        liveLayout.Controls.Add(liveSource);

        var eyesCard = LiveFields();
        Span(eyesCard, SectionTitle("Eyes", HubIcon.Eyes), 0);
        var magiskNotice = Info("Using a Magisk gaze module? Leave Hub gaze off. Skip Check gaze setup and Prepare gaze.");
        magiskNotice.Margin = new Padding(0, 0, 0, 10);
        Span(eyesCard, magiskNotice, 1);
        Span(eyesCard, _gaze, 2);
        Span(eyesCard, _pupil, 3);
        Span(eyesCard, _eyebrowBoost, 4);
        var eyeSettings = LiveFields();
        eyeSettings.BackColor = Inset;
        eyeSettings.Padding = new Padding(12);
        eyeSettings.Margin = new Padding(0, 4, 0, 2);
        LiveField(eyeSettings, 0, "Eye profile", _eyeProfiles);
        LiveField(eyeSettings, 1, "Pupil response", _pupilSensitivity);
        LiveField(eyeSettings, 2, "Eyebrow sensitivity", _eyebrowSensitivity);
        Span(eyeSettings, Info("The Hub gaze method briefly freezes the headset while restarting tracking. Wait for Activity to confirm it is ready. Use only one gaze method at a time."), 3);
        Span(eyeSettings, Info("Pupils need a short warmup: look straight and hold steady. Camera FPS and preview are under Your session."), 4);
        Span(eyeSettings, Info("Using an independent-gaze Magisk module? Leave Hub gaze off and skip Check gaze setup and Prepare gaze. Pupil and eyebrow features work separately."), 5);
        Details(eyesCard, eyeSettings, "eye settings and guidance", 5);

        var lowerFace = LiveFields();
        Span(lowerFace, SectionTitle("Lower-face tracking", HubIcon.LowerFace), 0);
        var tongueModelPicker = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0, 0, 0, 8), Padding = Padding.Empty };
        tongueModelPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tongueModelPicker.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tongueModelPicker.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tongueModelLabel = FieldLabel("Tongue model · stop tracking to change");
        tongueModelLabel.Tag = "responsive-info";
        tongueModelLabel.Margin = new Padding(0, 0, 0, 5);
        tongueModelPicker.Controls.Add(tongueModelLabel, 0, 0);
        _tongueModels.AccessibleName = "Tongue model";
        _tongueModels.Margin = Padding.Empty;
        tongueModelPicker.Controls.Add(_tongueModels, 0, 1);
        Span(lowerFace, tongueModelPicker, 1);
        _tongueModelNote.Margin = new Padding(0, 3, 0, 8);
        Span(lowerFace, _tongue, 3);
        var cameraSettings = LiveFields();
        cameraSettings.BackColor = Inset;
        cameraSettings.Padding = new Padding(8);
        cameraSettings.Margin = new Padding(0, 2, 0, 4);
        Span(cameraSettings, _tongueModelNote, 0);
        LiveField(cameraSettings, 1, "Motion smoothing", _smoothing);
        LiveField(cameraSettings, 2, "Tongue visibility", _visibilityMode);
        var smoothingHelp = new ToolTip();
        smoothingHelp.SetToolTip(_smoothing, "Higher values steady small movements. Fast tongue movements still respond quickly.");
        _smoothing.AccessibleDescription = "Higher values steady small movements. Fast tongue movements still respond quickly. Use the arrow keys to adjust.";
        Disposed += (_, _) => smoothingHelp.Dispose();
        Span(cameraSettings, Info("Restart tracking after changing the model or camera options. Shared FPS and preview options are under Your session > Show connection and camera settings."), 3);
        Details(lowerFace, cameraSettings, "tongue settings", 4);
        var nativeCheekHeading = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0, 8, 0, 4) };
        nativeCheekHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nativeCheekHeading.Controls.Add(SectionCaption("STREAMING APP CHEEKS"));
        Span(lowerFace, nativeCheekHeading, 5);
        Span(lowerFace, _individualCheekPuff, 6);
        Span(lowerFace, _individualCheekSuck, 7);
        var cheekHelp = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Margin = Padding.Empty };
        LiveRows(cheekHelp, 1);
        cheekHelp.Controls.Add(Info("1/0 highlights the leading cheek using your streaming app's face data. A two-cheek puff can still move both cheeks."));
        cheekHelp.Controls.Add(Info("Start tracking applies the selected adjustments. Turn an adjustment off or stop optional tracking to use the original face values from your streaming app."));
        Details(lowerFace, cheekHelp, "cheek adjustment details", 8);
        var featureGroups = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty };
        featureGroups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        featureGroups.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        featureGroups.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        featureGroups.Controls.Add(eyesCard, 0, 0);
        featureGroups.Controls.Add(lowerFace, 0, 1);
        liveLayout.Controls.Add(featureGroups);
        bool fittingFeatureGroups = false;
        void FitFeatureGroups()
        {
            if (fittingFeatureGroups) return;
            var scale = DeviceDpi / 96F;
            var columns = PageColumnWidth(livePage) >= (int)Math.Ceiling(900 * scale) ? 2 : 1;
            if (featureGroups.ColumnCount == columns) return;
            fittingFeatureGroups = true;
            featureGroups.SuspendLayout();
            try
            {
                featureGroups.ColumnCount = columns;
                featureGroups.RowCount = columns == 2 ? 1 : 2;
                featureGroups.ColumnStyles.Clear();
                featureGroups.RowStyles.Clear();
                for (var column = 0; column < columns; column++)
                    featureGroups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
                for (var row = 0; row < featureGroups.RowCount; row++)
                    featureGroups.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var gutter = (int)Math.Ceiling(7 * scale);
                eyesCard.Margin = new Padding(0, 0, columns == 2 ? gutter : 0, 14);
                lowerFace.Margin = new Padding(columns == 2 ? gutter : 0, 0, 0, 14);
                featureGroups.SetCellPosition(eyesCard, new TableLayoutPanelCellPosition(0, 0));
                featureGroups.SetCellPosition(lowerFace, new TableLayoutPanelCellPosition(columns == 2 ? 1 : 0, columns == 2 ? 0 : 1));
            }
            finally { featureGroups.ResumeLayout(true); fittingFeatureGroups = false; }
        }
        livePage.ClientSizeChanged += (_, _) => FitFeatureGroups();
        livePage.VisibleChanged += (_, _) => { if (livePage.Visible) FitFeatureGroups(); };
        foreach (var box in new[] { _pupilSensitivity, _eyebrowSensitivity, _visibilityMode }) box.Dock = DockStyle.Fill;
        _smoothing.Dock = DockStyle.Fill;

        var controllersLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        controllersLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        controllersLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        controllersPage.Controls.Add(controllersLayout);
        var handsCard = LiveFields();
        var controllerHeading = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        controllerHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        controllerHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var controllerTitle = SectionTitle("Hands and controllers", HubIcon.Controller);
        controllerTitle.Margin = Padding.Empty;
        controllerTitle.Anchor = AnchorStyles.Left;
        controllerHeading.Controls.Add(controllerTitle, 0, 0);
        var controllerSourceRow = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 1, Margin = new Padding(0, 0, 0, 4) };
        controllerSourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var controllerSource = Info("");
        controllerSource.AccessibleName = "Controller streaming app";
        controllerSource.Margin = Padding.Empty;
        controllerSourceRow.Controls.Add(controllerSource, 0, 0);
        Span(handsCard, controllerHeading, 0);
        void UpdateControllerSource()
        {
            string text = _environment.SteamLinkSelected
                ? "Streaming app: Steam Link · experimental inputs require Virtual Desktop."
                : "Streaming app: Virtual Desktop · experimental inputs";
            if (controllerSource.Text != text) controllerSource.Text = text;
            controllerSource.ForeColor = _environment.SteamLinkSelected ? Warning : Muted;
        }
        _trackingSourceLive.SelectedIndexChanged += (_, _) => UpdateControllerSource();
        _trackingSourceSetup.SelectedIndexChanged += (_, _) => UpdateControllerSource();
        controllersPage.VisibleChanged += (_, _) => { if (controllersPage.Visible) UpdateControllerSource(); };
        UpdateControllerSource();
        Span(handsCard, controllerSourceRow, 1);
        var opticalHeading = SectionCaption("OPTICAL FINGERS");
        opticalHeading.Margin = new Padding(0, 12, 0, 4);
        Span(handsCard, opticalHeading, 2);
        Span(handsCard, _hybridHands, 3);
        var thumbRestHeading = SectionCaption("THUMB-REST INPUT");
        thumbRestHeading.Margin = new Padding(0, 14, 0, 4);
        Span(handsCard, thumbRestHeading, 4);
        Span(handsCard, _controllerTouchpad, 5);
        LiveField(handsCard, 6, "Thumb-rest mode", _touchpadMode);
        LiveField(handsCard, 7, "Apply mode to", _touchpadSide);
        var swipeSettings = LiveFields();
        swipeSettings.Padding = Padding.Empty;
        swipeSettings.Margin = Padding.Empty;
        swipeSettings.AccessibleName = "Swipe sensitivity options";
        var swipeSliderRow = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty,
        };
        swipeSliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        swipeSliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _swipeSensitivity.Margin = new Padding(0, 0, 12, 0);
        _swipeSensitivityValue.Margin = Padding.Empty;
        swipeSliderRow.Controls.Add(_swipeSensitivity, 0, 0);
        swipeSliderRow.Controls.Add(_swipeSensitivityValue, 1, 0);
        LiveField(swipeSettings, 0, "Swipe sensitivity", swipeSliderRow);
        Span(swipeSettings, Info("Lower is steadier; higher scrolls faster. Stop tracking, adjust, then Start tracking."), 1);
        swipeSettings.Visible = _touchpadMode.SelectedIndex == 2;
        _touchpadMode.SelectedIndexChanged += (_, _) => swipeSettings.Visible = _touchpadMode.SelectedIndex == 2;
        Span(handsCard, swipeSettings, 8);
        _controllerModeHint.Margin = new Padding(0, 4, 0, 8);
        Span(handsCard, _controllerModeHint, 9);
        var handsPrerequisiteReminder = Info("Before use: turn on Quest hand tracking and automatic hand/controller switching, plus Singularity's Simultaneous Hands & Controllers mode.");
        handsPrerequisiteReminder.Margin = new Padding(0, 2, 0, 12);
        Span(handsCard, handsPrerequisiteReminder, 10);
        Span(handsCard, ControllerCompatibilitySummary(), 11);
        _checkHandsButton.Margin = new Padding(0, 4, 8, 4);
        var handsDetails = LiveFields();
        handsDetails.BackColor = Inset;
        handsDetails.Padding = new Padding(12);
        Span(handsDetails, Info("Left and right mean your own hands. This selection controls the extra thumb-rest input only; optical fingers and normal controller inputs remain available on both sides. Keep both controllers awake during the initial thumb-rest check, even when applying the mode to one side."), 0);
        Span(handsDetails, _controllerBindingGuide, 1);
        Span(handsDetails, Info("Dashboard and VRChat menu scrolling can be prepared automatically where supported. Choose your own actions for other games in SteamVR's binding editor."), 2);
        Span(handsDetails, Info("Before the first finger test, leave Virtual Desktop's Emulate Index controllers off and Singularity's separate Frida Server off. Check ordinary hand tracking with empty hands first, then test real fingers while holding the controllers."), 3);
        Span(handsDetails, Info("Start tracking applies the selected inputs. Stop tracking stops them. Close SteamVR before installing or removing the optional components."), 4);
        Span(handsDetails, Info("If game FPS drops, press Stop tracking and keep the experimental inputs off. A compatibility check does not confirm performance in your game."), 5);
        Span(handsDetails, HandsPrerequisites(includeAppSettings: true), 6);
        Span(handsDetails, Info("Adds optical finger tracking while you hold the controllers; movement and buttons still come from the controllers. Connection and source settings are on Facial tracking."), 7);
        var handsDisclosure = Details(handsCard, handsDetails, "hand and controller details", 13);
        handsCard.Controls.Remove(handsDisclosure);
        var handsActions = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
        handsActions.Controls.Add(_checkHandsButton);
        handsActions.Controls.Add(handsDisclosure);
        Span(handsCard, handsActions, 12);
        controllersLayout.Controls.Add(handsCard);

        var setupLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        setupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) setupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setupPage.Controls.Add(setupLayout);
        var connectionCard = Card(); connectionCard.Dock = DockStyle.Top;
        connectionCard.ColumnCount = 1;
        connectionCard.BackColor = Raised;
        connectionCard.Padding = new Padding(16);
        connectionCard.Controls.Add(SectionTitle("Connect your Quest Pro", HubIcon.Headset));
        connectionCard.Controls.Add(Info("Connect a rooted Quest Pro for camera, gaze and controller features."));
        var connectionPicker = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 3, 0, 7) };
        connectionPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        connectionPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connectionPicker.Controls.Add(FieldLabel("Connection type"), 0, 0);
        ConfigureDropDown(_connectionMode);
        _connectionMode.AccessibleName = "Connection type";
        _connectionMode.Margin = new Padding(0, 3, 0, 3);
        connectionPicker.Controls.Add(_connectionMode, 1, 0);
        connectionCard.Controls.Add(connectionPicker);
        _connectionModeNote.Margin = new Padding(0, 2, 0, 8);
        var headsetStatus = MirrorStatus(_usbStatus, accessibleName: "Headset setup status");
        headsetStatus.Tag = "responsive-info";
        connectionCard.Controls.Add(headsetStatus);
        connectionCard.Controls.Add(_connectionModeNote);
        _usbActions.Margin = new Padding(0, 3, 0, 8);
        foreach (var button in new[] { _reconnectUsbButton, _forgetUsbButton })
        {
            button.Dock = DockStyle.None;
            button.AutoSize = true;
            button.MinimumSize = new Size(180, 42);
        }
        _usbActions.Controls.Add(_reconnectUsbButton);
        _usbActions.Controls.Add(_forgetUsbButton);
        connectionCard.Controls.Add(_usbActions);
        _wirelessSetup.Margin = new Padding(0, 3, 0, 8);
        _wirelessSetup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        LiveRows(_wirelessSetup, 4);
        _wirelessSetup.Controls.Add(Info("Enter the Quest's Wi-Fi IP and press Connect to Quest. If wireless ADB has not been enabled, open wireless setup options and use Enable from USB first."), 0, 0);
        var wirelessFields = new List<TableLayoutPanel>();
        TableLayoutPanel ConnectionField(string title, TextBox input)
        {
            var row = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 2, 0, 6) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(FieldLabel(title), 0, 0);
            input.AccessibleName = title;
            input.Margin = new Padding(0, 3, 0, 3);
            row.Controls.Add(input, 1, 0);
            wirelessFields.Add(row);
            return row;
        }
        _wirelessSetup.Controls.Add(ConnectionField("Quest IP:port", _wirelessAddress), 0, 1);
        _wirelessSetup.Controls.Add(_connectWirelessButton, 0, 2);
        var wirelessOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        wirelessOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) wirelessOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wirelessOptions.Controls.Add(_enableWirelessButton, 0, 0);
        var pairingOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        pairingOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pairingOptions.Controls.Add(Info("Use Pair and connect only when Android shows a six-digit pairing code. Copy the temporary pairing IP:port and code from that dialog; its port differs from the regular Quest connection port."));
        pairingOptions.Controls.Add(ConnectionField("Pairing IP:port", _pairingEndpoint));
        pairingOptions.Controls.Add(ConnectionField("Six-digit code", _pairingCode));
        pairingOptions.Controls.Add(_pairWirelessButton);
        Details(wirelessOptions, pairingOptions, "pairing options", 1);
        wirelessOptions.Controls.Add(_disableWirelessButton, 0, 3);
        Details(_wirelessSetup, wirelessOptions, "wireless setup options", 3);
        connectionCard.Controls.Add(_wirelessSetup);
        var setupSource = Card(); setupSource.Dock = DockStyle.Top;
        setupSource.ColumnCount = 2;
        setupSource.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        setupSource.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setupSource.BackColor = Raised;
        setupSource.Padding = Padding.Empty;
        setupSource.Margin = Padding.Empty;
        _outputRouteSetup.Margin = new Padding(0, 2, 0, 3);
        setupSource.Controls.Add(_outputRouteSetup, 0, 0);
        setupSource.SetColumnSpan(_outputRouteSetup, 2);
        setupSource.Controls.Add(FieldLabel("Streaming app"), 0, 1);
        _trackingSourceSetup.Margin = new Padding(0, 3, 0, 3);
        _trackingSourceSetup.AccessibleName = "Streaming app";
        setupSource.Controls.Add(_trackingSourceSetup, 1, 1);
        _trackingSourceSetupNote.Margin = new Padding(0, 2, 0, 8);

        var streamerUpdaterControls = BuildStreamerUpdaterControls();
        _setupProgressContainer.Dock = DockStyle.Top;
        _setupProgressContainer.AutoSize = true;
        _setupProgressContainer.ColumnCount = 1;
        _setupProgressContainer.RowCount = 3;
        _setupProgressContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _setupProgressContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _setupProgressContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _setupProgressContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _setupProgressContainer.BackColor = Panel;
        _setupProgressContainer.Padding = new Padding(16, 10, 16, 10);
        _setupProgressContainer.Margin = new Padding(0, 0, 0, 12);
        _setupProgressContainer.Visible = false;
        var setupProgressHeader = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 4) };
        setupProgressHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setupProgressHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        setupProgressHeader.Controls.Add(new Label { Text = "Setup progress", AutoSize = true, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextPrimary,
            Font = new Font(UiFontName, 10F, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        var viewSetupActivity = ActionButton("View setup activity", (_, _) => activityTab.PerformClick());
        viewSetupActivity.Margin = Padding.Empty;
        setupProgressHeader.Controls.Add(viewSetupActivity, 1, 0);
        _setupProgressContainer.Controls.Add(setupProgressHeader, 0, 0);
        var setupProgressMessage = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true,
            TabStop = true, AccessibleName = "Setup progress details", Margin = Padding.Empty,
            Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0) };
        _setupProgressStatus.Dock = DockStyle.Top;
        _setupProgressStatus.Margin = Padding.Empty;
        setupProgressMessage.Controls.Add(_setupProgressStatus);
        _setupProgressContainer.Controls.Add(setupProgressMessage, 0, 1);
        _setupProgressContainer.Controls.Add(_setupProgress, 0, 2);
        void FitSetupProgressText()
        {
            var scale = DeviceDpi / 96F;
            int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
            // Keep the complete message scrollable while a long setup error
            // cannot displace all of the step actions and navigation.
            var messageHeight = _setupProgressStatus.PreferredSize.Height;
            _setupProgressContainer.RowStyles[1].Height = Math.Clamp(messageHeight, Px(22), Px(72));
            _setupProgressContainer.RowStyles[2].Height = Px(24);
        }
        _setupProgressStatus.SizeChanged += (_, _) => FitSetupProgressText();
        _setupProgressStatus.TextChanged += (_, _) => FitSetupProgressText();
        _setupProgressContainer.VisibleChanged += (_, _) =>
        {
            if (!_setupProgressContainer.Visible) return;
            FitSetupProgressText();
            setupPage.AutoScrollPosition = Point.Empty;
        };
        FitSetupProgressText();
        _setupRuntimeButton.Click += async (_, _) => await RunSetupStepAsync("PC runtime setup", "setup-runtime.ps1", "PC runtime is ready.", "Next: choose your streaming app and enable OSC in VRChat.");
        _setupGazeButton.Click += async (_, _) => await PrepareGazeAsync();
        _recoverGazeButton.Click += async (_, _) => await RecoverGazeAsync();
        _inspectGazeButton.Click += async (_, _) => await InspectGazeAsync();
        _resetLegacyGazeButton.Click += async (_, _) => await ResetLegacyGazeAsync();
        var runtimeCard = Card();
        runtimeCard.ColumnCount = 1;
        Span(runtimeCard, SectionTitle("Install the PC runtime", HubIcon.Desktop), 0);
        Span(runtimeCard, _setupRuntimeStatus, 1);
        Span(runtimeCard, Info("Install the private Python runtime for camera, gaze or controller features. Keep Companion open until setup finishes."), 2);
        Span(runtimeCard, _setupRuntimeButton, 3);

        var vrchatCard = Card();
        vrchatCard.BackColor = Raised;
        vrchatCard.ColumnCount = 1;
        vrchatCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Span(vrchatCard, SectionTitle("Face and eye output", HubIcon.Headset), 0);
        Span(vrchatCard, setupSource, 1);
        var routeGuide = Info("");
        Span(vrchatCard, routeGuide, 2);
        var vrchatStatus = MirrorStatus(_directVrchatStatus, accessibleName: "VRChat OSC setup status");
        vrchatStatus.Tag = "responsive-info";
        vrchatStatus.Font = new Font(UiFontName, 9.5F, FontStyle.Bold);
        vrchatStatus.Margin = new Padding(0, 4, 0, 2);
        Span(vrchatCard, vrchatStatus, 3);
        var checkVrchatSetup = _checkVrchatSetupButton = SetupButton("Check VRChat connection");
        checkVrchatSetup.AccessibleName = "Check VRChat connection in setup";
        checkVrchatSetup.Click += (_, _) => CheckVrchatConnection();
        _installBackupModuleButton.VisualStyle = HubButtonStyle.Primary;
        var outputActions = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = Padding.Empty,
        };
        foreach (var action in new[] { _installBackupModuleButton, checkVrchatSetup })
        {
            action.Dock = DockStyle.None;
            action.AutoSize = true;
            action.MinimumSize = new Size(0, 42);
            action.Margin = new Padding(0, 4, 8, 4);
            outputActions.Controls.Add(action);
        }
        Span(vrchatCard, outputActions, 4);
        Details(vrchatCard, _trackingSourceSetupNote, "streaming app instructions", 5);

        void ShowOutputRoute()
        {
            bool backup = _outputRouteSetup.SelectedIndex == 1;
            checkVrchatSetup.Text = checkVrchatLive.Text = backup ? "Check backup connection" : "Check VRChat connection";
            checkVrchatSetup.AccessibleName = checkVrchatSetup.Text + " in setup";
            checkVrchatLive.AccessibleDescription = backup
                ? "Check whether the selected VRCFaceTracking module is active."
                : "Check the current VRChat OSC connection and avatar input status.";
            checkVrchatSetup.VisualStyle = backup ? HubButtonStyle.Secondary : HubButtonStyle.Primary;
            _installBackupModuleButton.Visible = backup;
            routeGuide.Text = backup
                ? "Close VRCFaceTracking before installing the module. Reopen it and enable OSC in VRChat to test your avatar."
                : VrchatOscGuidance;
            liveRouteNote.Text = backup
                ? "VRCFaceTracking sends native expressions to VRChat. Open First-time setup to manage its module or return to direct output."
                : "Native face and eyes connect automatically while Companion is open. Use Start tracking and Stop tracking for optional features.";
        }
        _outputRouteSetup.SelectedIndexChanged += (_, _) => ShowOutputRoute();
        ShowOutputRoute();
        setupLayout.Controls.Add(BuildGuidedSetupWorkspace(setupPage,
            [vrchatCard, connectionCard, runtimeCard], () => liveTab.PerformClick()), 0, 0);

        var headsetCompatibilityBody = BuildHeadsetCompatibilityCard();
        // The compact tool header supplies the title; retain all original
        // diagnostic controls and their handlers inside the expanded body.
        headsetCompatibilityBody.Controls[0].Visible = false;
        var gazeOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        gazeOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Span(gazeOptions, Info("These tools are only for the Hub's temporary gaze method. Check gaze setup is optional diagnostics; Prepare gaze validates the headset before preparing its local patch. Neither is needed for Magisk gaze, tongue, pupils or ordinary face tracking."), 0);
        var gazeActions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        gazeActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        gazeActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // Reserve the action height even while the tool is hidden. Measuring
        // Fill-docked buttons in an auto-sized row can collapse it to one pixel.
        gazeActions.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        _inspectGazeButton.Dock = _setupGazeButton.Dock = DockStyle.Fill;
        _inspectGazeButton.Margin = new Padding(0, 4, 8, 4);
        _setupGazeButton.Margin = new Padding(0, 4, 0, 4);
        gazeActions.Controls.Add(_inspectGazeButton, 0, 0);
        gazeActions.Controls.Add(_setupGazeButton, 1, 0);
        Span(gazeOptions, gazeActions, 1);
        var gazeRecovery = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        gazeRecovery.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gazeRecovery.Controls.Add(Info("Recover Qpro gaze restores the previous eye-model state saved by a recorded Qpro session. Reset legacy gaze selects normal gaze for an older session without a record, after confirmation. Both options leave Magisk settings in place."));
        gazeRecovery.Controls.Add(_recoverGazeButton);
        gazeRecovery.Controls.Add(_resetLegacyGazeButton);
        var handsOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        handsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Span(handsOptions, Info("Installing components does not enable the headset settings."), 0);
        Span(handsOptions, HandsPrerequisites(includeAppSettings: false), 1);
        Span(handsOptions, Info("1. Close SteamVR.\n2. Install the optional components.\n3. Reopen SteamVR through Virtual Desktop, then use Controllers > Check hand/controller compatibility."), 2);
        Span(handsOptions, Info("Uninstall controller add-on removes the thumb-rest add-on and keeps a recovery copy. Quest and Singularity settings stay in place."), 3);
        Span(handsOptions, _installHandsButton, 4);
        Span(handsOptions, _removeHandsButton, 5);
        _setupAmdStatus.Tag = "responsive-info";
        var amdOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        amdOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        amdOptions.Controls.Add(Info($"Install the PC runtime first. ROCm {HubRocmRuntime.InstallVersion} accelerates training and tracking on listed discrete Radeon GPUs. Setup enables it only after GPU tests pass. This remains experimental; earlier verified ROCm runtimes stay available as fallbacks."));
        amdOptions.Controls.Add(_amdGpuStatus);
        var amdSupportLink = new LinkLabel { Text = "ROCm 7.2.1 fallback GPU list", AutoSize = true, LinkColor = LinkText, ActiveLinkColor = LinkText, VisitedLinkColor = LinkText, Margin = new Padding(0, 3, 0, 8) };
        amdSupportLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/compatibility/compatibilityrad/windows/windows_compatibility.html") { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Could not open AMD support list"); }
        };
        var amdExperimentalLink = new LinkLabel { Text = $"AMD TheRock ROCm {HubRocmRuntime.InstallVersion} GPU targets", AutoSize = true, LinkColor = LinkText, ActiveLinkColor = LinkText, VisitedLinkColor = LinkText, Margin = new Padding(0, 3, 0, 8) };
        amdExperimentalLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/ROCm/TheRock/blob/main/RELEASES.md") { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Could not open AMD TheRock details"); }
        };
        amdOptions.Controls.Add(amdExperimentalLink);
        amdOptions.Controls.Add(amdSupportLink);
        amdOptions.Controls.Add(_setupAmdButton);
        var amdDetails = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        amdDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        amdDetails.Controls.Add(_experimentalWindows10Rocm);
        amdDetails.Controls.Add(Info("AMD validates this Windows ROCm path on Windows 11. Windows 10 is experimental and still requires a mapped discrete Radeon plus successful training and inference checks."));
        Details(amdOptions, amdDetails, "AMD compatibility details", amdOptions.Controls.Count);
        var optionalSetupTools = BuildOptionalToolsWorkspace(
            new OptionalSetupGroup("Optional features",
                new OptionalSetupTool("gaze setup", "Temporary independent eye gaze; Magisk users can skip this.", gazeOptions, _setupGazeStatus),
                new OptionalSetupTool("AMD setup", "Experimental Radeon acceleration for tracking and training.", amdOptions, _setupAmdStatus),
                new OptionalSetupTool("hands setup and uninstall", "Experimental optical fingers and thumb-rest input · Virtual Desktop only.", handsOptions)),
            new OptionalSetupGroup("Checks & Updates",
                new OptionalSetupTool("headset compatibility", "Read-only checks for the connected Quest Pro, root access and firmware.", headsetCompatibilityBody),
                new OptionalSetupTool("Windows Streamer updates", "Check, pause or restore the verified Windows updater.", streamerUpdaterControls,
                    Opened: async () => { if (_streamerUpdaterState is null) await RefreshStreamerUpdaterAsync(); })),
            new OptionalSetupGroup("Recovery",
                new OptionalSetupTool("PC runtime repair and recovery", "Repair libraries or restore a verified saved runtime; keep models and recordings.", BuildRuntimeRecoveryControls()),
                new OptionalSetupTool("gaze recovery tools", "Restore a recorded Qpro gaze state or reset an older temporary gaze selection.", gazeRecovery)));
        var startupCard = Card();
        startupCard.ColumnCount = 1;
        startupCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        startupCard.Padding = new Padding(16, 8, 16, 10);
        _launchWithSteamVr.AccessibleName = _launchWithSteamVr.Text;
        _launchWithSteamVr.AccessibleDescription = "Open Companion automatically when SteamVR starts.";
        _steamVrStartupStatus.AccessibleName = "SteamVR startup status";
        _steamVrStartupStatus.Margin = new Padding(4, 2, 4, 0);
        Span(startupCard, _launchWithSteamVr, 0);
        Span(startupCard, _steamVrStartupStatus, 1);
        setupLayout.Controls.Add(startupCard, 0, 1);

        var optionalSetupCard = Card(); optionalSetupCard.ColumnCount = 1;
        Span(optionalSetupCard, SectionTitle("Optional tools and recovery", HubIcon.Sliders), 0);
        var optionalSetupNote = Info("Gaze, hands, Radeon acceleration, compatibility checks and repair.");
        optionalSetupNote.Margin = new Padding(0, 0, 0, 4);
        Span(optionalSetupCard, optionalSetupNote, 1);
        Details(optionalSetupCard, optionalSetupTools, "optional tools and recovery", 2);
        setupLayout.Controls.Add(optionalSetupCard, 0, 2);

        var personalLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        personalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalizationPage.Controls.Add(personalLayout);
        var personalIntro = Card(); personalIntro.Dock = DockStyle.Top;
        personalIntro.Controls.Add(SectionTitle("Capture and train", HubIcon.Sliders));
        personalIntro.Controls.Add(Info("Start with Quick refinement to fit the selected tongue model to your face and headset position. Record the guided poses, then train a separate personal copy."));
        personalLayout.Controls.Add(personalIntro);
        _trainingProgressContainer.Dock = DockStyle.Top;
        _trainingProgressContainer.AutoSize = true;
        _trainingProgressContainer.ColumnCount = 1;
        _trainingProgressContainer.BackColor = Panel;
        _trainingProgressContainer.Padding = new Padding(16);
        _trainingProgressContainer.Margin = new Padding(0, 0, 0, 12);
        _trainingProgressContainer.Controls.Add(new Label { Text = "Training progress", AutoSize = true, ForeColor = TextPrimary, Font = new Font(UiFontName, 10F, FontStyle.Bold) });
        _trainingProgressContainer.Controls.Add(_trainingProgressStatus);
        _trainingProgressContainer.Controls.Add(_trainingProgress);
        personalLayout.Controls.Add(_trainingProgressContainer);
        var choices = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) choices.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        choices.Controls.Add(WorkflowCard("Quick refinement · 15–30 min · Recommended", "Start here to fit the selected tongue model to your face. Follow the guided tongue poses, then train a separate personalized copy.", _quickDatasets, _quickQueueStatus, _quickRecordedDatasets,
            ActionButton("1. Record refinement", async (_, _) => await ConfirmCaptureAsync(TongueDatasetKind.Quick)), ActionButton("2. Train personalized copy", async (_, _) => await TrainTongueAsync(TongueDatasetKind.Quick)),
            ActionButton("Delete selected dataset…", (_, _) => DeleteRecordedDataset(TongueDatasetKind.Quick))), 0, 0);
        personalLayout.Controls.Add(choices);
        var alternativeCaptures = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        alternativeCaptures.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        alternativeCaptures.Controls.Add(WorkflowCard("Focused diagonals + facial hair · 15–30 min", "Extra practice for diagonal tongue movement and facial-hair shadows. Record matching hidden and visible poses, then train a new copy.", _focusedDatasets, _focusedQueueStatus, _focusedRecordedDatasets,
            ActionButton("1. Record focused dataset", async (_, _) => await ConfirmCaptureAsync(TongueDatasetKind.Focused)), ActionButton("2. Train focused copy", async (_, _) => await TrainTongueAsync(TongueDatasetKind.Focused)),
            ActionButton("Delete selected dataset…", (_, _) => DeleteRecordedDataset(TongueDatasetKind.Focused))), 0, 0);
        alternativeCaptures.Controls.Add(WorkflowCard("Full dataset · 60–120 min", "Broader tongue coverage for a new personal model. Choose this when you can take your time and follow each pose carefully.", _fullDatasets, _fullQueueStatus, _fullRecordedDatasets,
            ActionButton("1. Record full dataset", async (_, _) => await ConfirmCaptureAsync(TongueDatasetKind.Full)), ActionButton("2. Train new personal model", async (_, _) => await TrainTongueAsync(TongueDatasetKind.Full)),
            ActionButton("Delete selected dataset…", (_, _) => DeleteRecordedDataset(TongueDatasetKind.Full))), 0, 1);
        var captureOptions = Card(); captureOptions.ColumnCount = 1;
        Span(captureOptions, SectionTitle("More capture options", HubIcon.Sliders), 0);
        Span(captureOptions, Info("Use a focused capture for specific tongue problems, or a full dataset for broader coverage."), 1);
        Details(captureOptions, alternativeCaptures, "focused and full captures", 2);
        personalLayout.Controls.Add(captureOptions);

        var manager = Card(); manager.Dock = DockStyle.Top; manager.AutoSize = false;
        manager.ColumnCount = 1; manager.RowCount = 4;
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var managerTitle = SectionTitle("Models on this PC", HubIcon.Models);
        var managerIntro = Info("Manage bundled models and personal refinements. Import or export .qptonguemodel packages.");
        manager.Controls.Add(managerTitle, 0, 0);
        manager.Controls.Add(managerIntro, 0, 1);
        var modelWorkspace = new BufferedTableLayoutPanel { Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        modelWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        modelWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        modelWorkspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var modelBody = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Inset, Margin = Padding.Empty };
        _modelList.AccessibleName = "Tongue models";
        modelBody.Controls.Add(_modelList);
        modelBody.Controls.Add(_modelEmpty);
        var modelDetails = BuildModelManagerDetails();
        modelWorkspace.Controls.Add(modelBody, 0, 0);
        modelWorkspace.Controls.Add(modelDetails, 1, 0);
        manager.Controls.Add(modelWorkspace, 0, 3);
        var modelActions = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8), AccessibleName = "Model library actions" };
        modelActions.Controls.Add(ActionButton("Import", (_, _) => ImportModel()));
        modelActions.Controls.Add(ActionButton("Refresh", (_, _) => ReloadProfiles()));
        manager.Controls.Add(modelActions, 0, 2);
        modelsPage.Controls.Add(manager);
        bool fittingModelManager = false;
        void FitModelManagerHeight()
        {
            // Native minimize/restore dispatch briefly exposes the old small
            // page bounds. Measure once the completed size has reached the
            // responsive text pass, preserving the page's scroll extent.
            if (fittingModelManager || WindowState == FormWindowState.Minimized
                || _windowPositionDepth > 0 || _sizeMessageDepth > 0) return;
            fittingModelManager = true;
            try
            {
                var scale = DeviceDpi / 96F;
                int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
                var bodyWidth = Math.Max(1, PageColumnWidth(modelsPage) - manager.Padding.Horizontal);
                var wide = bodyWidth >= Px(800);
                var columns = wide ? 2 : 1;
                var gutter = Px(8);
                if (modelWorkspace.ColumnCount != columns)
                {
                    modelWorkspace.SuspendLayout();
                    try
                    {
                        modelWorkspace.ColumnCount = columns;
                        modelWorkspace.RowCount = wide ? 1 : 2;
                        modelWorkspace.ColumnStyles.Clear();
                        modelWorkspace.RowStyles.Clear();
                        modelWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, wide ? 45 : 100));
                        if (wide) modelWorkspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
                        modelWorkspace.RowStyles.Add(new RowStyle(wide ? SizeType.Percent : SizeType.Absolute,
                            wide ? 100 : Px(210)));
                        if (!wide) modelWorkspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                        modelWorkspace.SetCellPosition(modelBody, new TableLayoutPanelCellPosition(0, 0));
                        modelWorkspace.SetCellPosition(modelDetails, new TableLayoutPanelCellPosition(wide ? 1 : 0, wide ? 0 : 1));
                    }
                    finally { modelWorkspace.ResumeLayout(true); }
                }
                modelBody.Margin = new Padding(0, 0, wide ? gutter : 0, wide ? 0 : gutter);
                modelDetails.Margin = new Padding(wide ? gutter : 0, 0, 0, 0);
                var detailWidth = Math.Max(1, (wide ? (int)(bodyWidth * 0.55) : bodyWidth) - modelDetails.Margin.Horizontal);
                var detailHeight = Math.Max(modelDetails.Height,
                    modelDetails.GetPreferredSize(new Size(detailWidth, 0)).Height);
                var bodyHeight = wide ? Math.Max(Px(210), detailHeight) : Px(210) + detailHeight;
                var headerHeight = managerTitle.PreferredSize.Height + managerTitle.Margin.Vertical
                    + managerIntro.PreferredSize.Height + managerIntro.Margin.Vertical;
                var actionHeight = modelActions.GetPreferredSize(new Size(bodyWidth, 0)).Height + modelActions.Margin.Vertical;
                // At narrow widths the list stays usable and the complete
                // read-only detail panel follows it within the page's scroll.
                var minimum = Math.Max(Px(380), manager.Padding.Vertical + headerHeight + bodyHeight + actionHeight);
                var height = Math.Max(minimum, modelsPage.ClientSize.Height - modelsPage.Padding.Vertical);
                if (manager.Height != height) manager.Height = height;
            }
            finally { fittingModelManager = false; }
        }
        modelsPage.SizeChanged += (_, _) => FitModelManagerHeight();
        modelDetails.SizeChanged += (_, _) => FitModelManagerHeight();
        managerIntro.SizeChanged += (_, _) => FitModelManagerHeight();
        modelActions.SizeChanged += (_, _) => FitModelManagerHeight();
        FitModelManagerHeight();

        var activityLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = false,
            ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var feedbackCard = BuildFeedbackCard();
        activityLayout.Controls.Add(feedbackCard, 0, 0);
        var healthCard = BuildTrackingHealthCard();
        healthCard.Padding = new Padding(20, 12, 20, 12);
        healthCard.Margin = new Padding(0, 0, 0, 12);
        healthCard.Controls[0].Margin = new Padding(0, 0, 0, 8);
        foreach (var label in new[] { _healthRates, _healthFeatures, _healthVrchat, _healthNote })
            label.Margin = new Padding(0, 0, 0, 4);
        activityLayout.Controls.Add(healthCard, 0, 1);
        var logCard = Card(); logCard.Dock = DockStyle.Fill; logCard.AutoSize = false;
        logCard.Padding = new Padding(20, 14, 20, 14);
        logCard.ColumnCount = 1; logCard.RowCount = 3;
        logCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logTitle = SectionTitle("Detailed activity", HubIcon.Activity);
        logTitle.Margin = new Padding(0, 0, 0, 6);
        var logHeading = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
        logTitle.Margin = new Padding(0, 10, 18, 6);
        logHeading.Controls.Add(logTitle);
        var activityTools = BuildActivityTools();
        logHeading.Controls.Add(activityTools);
        logCard.Controls.Add(logHeading, 0, 0);
        var activityLegend = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
        foreach (var level in new[] { ActivitySeverity.Normal, ActivitySeverity.Warning, ActivitySeverity.Error })
            activityLegend.Controls.Add(new Label { Text = ActivityName(level), AutoSize = true,
                ForeColor = ActivityColor(level), Margin = new Padding(0, 0, 20, 0) });
        logCard.Controls.Add(activityLegend, 0, 1);
        _log.Margin = Padding.Empty;
        _log.ForeColor = Good;
        _log.AccessibleName = "Detailed activity";
        logCard.Controls.Add(_log, 0, 2);
        activityLayout.Controls.Add(logCard, 0, 2);
        activityPage.Controls.Add(activityLayout);
        bool fittingActivity = false;
        void FitActivityHeight()
        {
            if (fittingActivity) return;
            fittingActivity = true;
            try
            {
                // Reserve a readable log body as well as the wrapped header.
                // Short windows scroll the whole page rather than shrinking
                // the native text viewport beneath the diagnostic cards.
                var minimumLogBody = (int)Math.Ceiling(180 * DeviceDpi / 96F);
                var logHeaderHeight = Math.Max(logHeading.Height, logHeading.PreferredSize.Height) + logHeading.Margin.Vertical
                    + Math.Max(activityLegend.Height, activityLegend.PreferredSize.Height) + activityLegend.Margin.Vertical;
                var minimumLogHeight = Math.Max((int)Math.Ceiling(240 * DeviceDpi / 96F),
                    minimumLogBody + logCard.Padding.Vertical + logHeaderHeight);
                var feedbackHeight = Math.Max(feedbackCard.Height, feedbackCard.PreferredSize.Height);
                var healthHeight = Math.Max(healthCard.Height, healthCard.PreferredSize.Height);
                var available = activityPage.ClientSize.Height - activityPage.Padding.Vertical;
                var height = Math.Max(available, feedbackHeight + feedbackCard.Margin.Vertical
                    + healthHeight + healthCard.Margin.Vertical + minimumLogHeight + logCard.Margin.Vertical);
                if (activityLayout.Height != height) activityLayout.Height = height;
            }
            finally { fittingActivity = false; }
        }
        activityPage.SizeChanged += (_, _) => FitActivityHeight();
        feedbackCard.SizeChanged += (_, _) => FitActivityHeight();
        healthCard.SizeChanged += (_, _) => FitActivityHeight();
        logHeading.SizeChanged += (_, _) => FitActivityHeight();
        activityLegend.SizeChanged += (_, _) => FitActivityHeight();
        activityTools.SizeChanged += (_, _) => FitActivityHeight();
        FitActivityHeight();

        var fittingTextPages = new HashSet<Panel>();
        var pendingTextPages = new HashSet<Panel>();
        var textLayoutTimer = new System.Windows.Forms.Timer(_uiComponents) { Interval = 50 };
        void FitPageText(Panel page)
        {
            if (!page.Visible || WindowState == FormWindowState.Minimized || _interactiveResize) return;
            if (!fittingTextPages.Add(page)) return;
            try
            {
                // A changing child width can make AutoScroll retain a horizontal
                // offset; keep headings anchored to the left while preserving Y.
                if (page.AutoScrollPosition.X != 0)
                    page.AutoScrollPosition = new Point(0, -page.AutoScrollPosition.Y);
                // Use the outer width so the appearance of a vertical scrollbar does not
                // make the wrapped labels and the scrollbar repeatedly resize each other.
                // Cap long setup notes on large displays so they remain easy to read.
                var availableTextWidth = PageColumnWidth(page) - (int)Math.Ceiling(40 * DeviceDpi / 96F);
                var textWidth = Math.Min((int)Math.Ceiling(900 * DeviceDpi / 96F), Math.Max(240, availableTextWidth));
                void Collect(Control parent, List<(Label Label, int Width)> changes)
                {
                    foreach (Control child in parent.Controls)
                    {
                        if (child is Label label && Equals(label.Tag, "responsive-info"))
                        {
                            // Nested disclosures and workflow cards can be narrower
                            // than the page. Wrap to their actual content width.
                            var parentWidth = parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal;
                            if (parent is TableLayoutPanel table)
                            {
                                var position = table.GetPositionFromControl(label);
                                var widths = table.GetColumnWidths();
                                if (position.Column >= 0 && widths.Length > position.Column)
                                {
                                    var span = table.GetColumnSpan(label);
                                    parentWidth = widths.Skip(position.Column).Take(span).Sum() - label.Margin.Horizontal;
                                }
                            }
                            var target = Math.Min(textWidth, parentWidth > 0 ? parentWidth : textWidth);
                            target = Math.Max(120, target);
                            if (label.MaximumSize.Width != target) changes.Add((label, target));
                        }
                        if (child.HasChildren) Collect(child, changes);
                    }
                }
                // Measure before mutating, then release each affected container
                // once. A second bounded pass accommodates nested columns that
                // obtained their final width during the first layout.
                for (var pass = 0; pass < 2; pass++)
                {
                    var changes = new List<(Label Label, int Width)>();
                    Collect(page, changes);
                    if (changes.Count == 0) break;
                    var ancestors = new HashSet<Control>();
                    foreach (var change in changes)
                    {
                        for (var ancestor = change.Label.Parent; ancestor is not null; ancestor = ancestor.Parent)
                        {
                            ancestors.Add(ancestor);
                            if (ancestor == page) break;
                        }
                    }
                    int Depth(Control control)
                    {
                        var depth = 0;
                        for (var parent = control.Parent; parent is not null; parent = parent.Parent) depth++;
                        return depth;
                    }
                    var containers = ancestors.OrderByDescending(Depth).ToArray();
                    foreach (var container in containers.Reverse()) container.SuspendLayout();
                    try
                    {
                        foreach (var change in changes) change.Label.MaximumSize = new Size(change.Width, 0);
                    }
                    finally
                    {
                        foreach (var container in containers) container.ResumeLayout(true);
                    }
                }
            }
            finally
            {
                try
                {
                    if (ReferenceEquals(page, setupPage))
                    {
                        _fitOptionalToolsWidth?.Invoke();
                        setupLayout.PerformLayout();
                    }
                    else if (ReferenceEquals(page, activityPage))
                    {
                        // Finish percent-row allocation after wrapped children
                        // have settled at the page's current height.
                        FitActivityHeight();
                        activityLayout.PerformLayout();
                    }
                    else if (ReferenceEquals(page, modelsPage))
                    {
                        FitModelManagerHeight();
                        manager.PerformLayout();
                        modelWorkspace.PerformLayout();
                    }
                }
                finally { fittingTextPages.Remove(page); }
            }
        }
        void SchedulePageText(Panel page)
        {
            if (!IsHandleCreated || IsDisposed || Disposing || !page.Visible
                || WindowState == FormWindowState.Minimized || fittingTextPages.Contains(page)) return;
            pendingTextPages.Add(page);
            // Layout can trigger another layout as scrollbars and wrapped text
            // settle. A timer yields to painting and input instead of posting
            // an unbroken chain of BeginInvoke callbacks during a corner drag.
            if (!_interactiveResize) textLayoutTimer.Start();
        }
        textLayoutTimer.Tick += (_, _) =>
        {
            textLayoutTimer.Stop();
            if (_interactiveResize) return;
            var pending = pendingTextPages.ToArray();
            pendingTextPages.Clear();
            if (IsDisposed || Disposing) return;
            foreach (var page in pending) FitPageText(page);
        };
        _flushResponsiveLayout = () =>
        {
            textLayoutTimer.Stop();
            var page = pageList.FirstOrDefault(page => page.Visible);
            if (page is null || WindowState == FormWindowState.Minimized) return;
            // Static previews have no running message loop to wait for the
            // resize timer. Drain only their currently visible page's reflow.
            for (var pass = 0; pass < 8; pass++)
            {
                pendingTextPages.Remove(page);
                page.PerformLayout();
                FitPageText(page);
                if (!pendingTextPages.Contains(page)) break;
            }
            textLayoutTimer.Stop();
            pendingTextPages.Clear();
        };
        void WatchResponsiveText(Control container, Panel page)
        {
            if (container.Controls.Cast<Control>().Any(child => child is Label label
                && Equals(label.Tag, "responsive-info")))
            {
                // Hidden disclosures have no final column width yet. Reflow
                // after they become visible and WinForms has laid them out.
                container.Layout += (_, _) => SchedulePageText(page);
                container.SizeChanged += (_, _) => SchedulePageText(page);
                container.VisibleChanged += (_, _) => SchedulePageText(page);
            }
            foreach (Control child in container.Controls)
                if (child.HasChildren) WatchResponsiveText(child, page);
        }
        foreach (var page in pageList)
        {
            WatchResponsiveText(page, page);
            page.ClientSizeChanged += (_, _) => SchedulePageText(page);
            page.VisibleChanged += (_, _) => SchedulePageText(page);
            FitPageText(page);
        }
        ResizeEnd += (_, _) =>
        {
            var page = pageList.FirstOrDefault(page => page.Visible);
            if (page is null) return;
            pendingTextPages.Remove(page);
            page.PerformLayout();
            FitPageText(page);
            if (pendingTextPages.Count == 0) textLayoutTimer.Stop();
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                textLayoutTimer.Stop();
                pendingTextPages.Clear();
                return;
            }
            var page = pageList.FirstOrDefault(page => page.Visible);
            if (page is not null) SchedulePageText(page);
        };
        void ShowPage(int index)
        {
            for (var item = 0; item < pageList.Length; item++)
            {
                pageList[item].Visible = item == index;
                StyleNavigationButton(tabs[item], item == index);
            }
            pageList[index].BringToFront();
            pageTitle.Text = tabs[index].Text;
            pageSubtitle.Text = navigation[index].Subtitle;
        }
        for (var index = 0; index < tabs.Length; index++)
        {
            var selected = index;
            tabs[index].Click += (_, _) => ShowPage(selected);
        }
        ShowPage(_environment.HasOpenedBefore ? 1 : 0);

        var footer = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = ShellSurface, Padding = new Padding(12, 10, 12, 10), Margin = Padding.Empty };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var footerIdentity = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        footerIdentity.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        footerIdentity.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        footerIdentity.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _runStatus.AutoSize = false; _runStatus.Dock = DockStyle.Fill; _runStatus.TextAlign = ContentAlignment.MiddleLeft;
        _runStatus.AutoEllipsis = true;
        footerIdentity.Controls.Add(_runStatus, 0, 0);
        var madeByCredit = new Label
        {
            Text = "Made by Fwooffy",
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font(UiFontName, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        footerIdentity.Controls.Add(madeByCredit, 0, 1);
        const string originalProject = "Qpro-Enhanced-FT";
        const string originalReleasesUrl = "https://github.com/n0tmast3r/Qpro-Enhanced-FT/releases";
        var originalCredit = new LinkLabel
        {
            Text = originalProject + " · n0tmast3r",
            LinkArea = new LinkArea(0, originalProject.Length),
            LinkBehavior = LinkBehavior.AlwaysUnderline,
            LinkColor = LinkText,
            ActiveLinkColor = LinkText,
            VisitedLinkColor = LinkText,
            ForeColor = Muted,
            Dock = DockStyle.Fill,
            Font = new Font(UiFontName, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false,
        };
        originalCredit.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(originalReleasesUrl) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show($"Could not open the original releases page: {error.Message}", "Open link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        footerIdentity.Controls.Add(originalCredit, 0, 2);
        footer.Controls.Add(footerIdentity, 0, 0);
        _start.Dock = DockStyle.None; _stop.Dock = DockStyle.None;
        _start.AccessibleDescription = "Start the selected optional tracking features.";
        _stop.AccessibleDescription = "Stop the optional tracking features and return to automatic native face and eyes.";
        _start.Anchor = _stop.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _start.AutoSize = false; _stop.AutoSize = false;
        _start.Height = _stop.Height = 48;
        _start.Margin = new Padding(0, 0, 8, 0);
        _stop.Margin = Padding.Empty;
        footer.Controls.Add(_start, 1, 0);
        footer.Controls.Add(_stop, 2, 0);
        shell.Controls.Add(footer, 0, 1); shell.SetColumnSpan(footer, 2);
        bool fittingFooter = false;
        void FitFooterColumns()
        {
            if (fittingFooter || shell.ClientSize.Width == 0) return;
            fittingFooter = true;
            try
            {
                var scale = DeviceDpi / 96F;
                int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
                // Use shared geometry instead of a hidden page's last control
                // bounds, so changing pages cannot shift the fixed actions.
                var sidebarWidth = (int)shell.ColumnStyles[0].Width;
                var left = sidebarWidth + Px(22) + lowerFace.Padding.Left
                    + (int)lowerFace.ColumnStyles[0].Width;
                var right = sidebarWidth + Px(22)
                    + Math.Min(Px(1160), Math.Max(1, main.ClientSize.Width - Px(44)
                        - SystemInformation.VerticalScrollBarWidth)) - lowerFace.Padding.Right;
                right = Math.Min(right, footer.ClientSize.Width - footer.Padding.Right);
                left = Math.Min(left, right - Px(240));
                var identityWidth = Math.Max(0, left - footer.Padding.Left);
                var actionWidth = Math.Max(Px(120), (right - left) / 2);
                var stopWidth = right - left - actionWidth;
                if ((int)footer.ColumnStyles[0].Width == identityWidth
                    && (int)footer.ColumnStyles[1].Width == actionWidth
                    && (int)footer.ColumnStyles[2].Width == stopWidth) return;
                footer.SuspendLayout();
                try
                {
                    if ((int)footer.ColumnStyles[0].Width != identityWidth) footer.ColumnStyles[0].Width = identityWidth;
                    if ((int)footer.ColumnStyles[1].Width != actionWidth) footer.ColumnStyles[1].Width = actionWidth;
                    if ((int)footer.ColumnStyles[2].Width != stopWidth) footer.ColumnStyles[2].Width = stopWidth;
                }
                finally { footer.ResumeLayout(true); }
            }
            finally { fittingFooter = false; }
        }
        shell.SizeChanged += (_, _) => FitFooterColumns();
        main.SizeChanged += (_, _) => FitFooterColumns();
        _tongueModels.SizeChanged += (_, _) => FitFooterColumns();
        lowerFace.SizeChanged += (_, _) => FitFooterColumns();

        void FitSidebarHeight()
        {
            var scale = DeviceDpi / 96F;
            int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
            var textWidth = Math.Max(Px(90), sidebar.Width - sidebar.Padding.Horizontal - Px(18));
            var noticeHeight = Math.Max(Px(86), TextRenderer.MeasureText(sidebarNotice.Text,
                sidebarNotice.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + Px(12));
            var baseHeight = sidebar.Padding.Vertical + (int)Math.Ceiling(
                sidebar.RowStyles.Cast<RowStyle>().Take(tabs.Length + 1).Sum(style => style.Height));
            var availableHeight = shell.ClientSize.Height - (int)Math.Ceiling(shell.RowStyles[1].Height);
            // The compact sidebar keeps every navigation button; the
            // decorative notice yields to them when the fixed footer needs room.
            var showNotice = availableHeight >= baseHeight + noticeHeight + Px(4);
            sidebarNotice.Visible = showNotice;
            sidebar.RowStyles[tabs.Length + 1].Height = showNotice ? noticeHeight : 0;
            sidebar.Height = baseHeight + (showNotice ? noticeHeight : 0);
        }
        shell.SizeChanged += (_, _) => FitSidebarHeight();

        // TableLayoutPanel absolute rows and owner-drawn controls do not all grow
        // with WinForms' font scaling. Recalculate them from the monitor DPI so
        // large text cannot be cut off at 150–300% display scaling.
        void ApplyDpiLayout()
        {
            var scale = DeviceDpi / 96F;
            int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
            FitSidebarWidth();
            shell.SuspendLayout();
            main.SuspendLayout();
            lowerFace.SuspendLayout();
            try
            {
                int TextHeight(Control control) => (int)Math.Ceiling(control.Font.GetHeight()) + Px(10);
                header.RowStyles[0].Height = Math.Max(Px(40), TextHeight(pageTitle));
                header.RowStyles[1].Height = Math.Max(Px(26), TextHeight(pageSubtitle));
                main.RowStyles[0].Height = Math.Max(Px(96),
                    header.Padding.Vertical + (int)header.RowStyles[0].Height + (int)header.RowStyles[1].Height + Px(4));
                var creditHeight = Math.Max(TextHeight(madeByCredit), TextHeight(originalCredit));
                footerIdentity.RowStyles[0].Height = Math.Max(Px(30), TextHeight(_runStatus));
                shell.RowStyles[1].Height = Math.Max(Px(110), footer.Padding.Vertical
                    + (int)footerIdentity.RowStyles[0].Height + creditHeight * 2 + Px(8));
                _start.Height = _stop.Height = Px(48);
                var sidebarTextWidth = Math.Max(Px(90), sidebar.Width - sidebar.Padding.Horizontal - Px(18));
                var brandSubtitleHeight = TextRenderer.MeasureText(brandSubtitle.Text, brandSubtitle.Font,
                    new Size(sidebarTextWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
                brand.RowStyles[0].Height = Math.Max(Px(43), TextHeight(brandTitle));
                brand.RowStyles[1].Height = Math.Max(Px(30), brandSubtitleHeight + Px(6));
                sidebar.RowStyles[0].Height = Math.Max(Px(88),
                    (int)brand.RowStyles[0].Height + (int)brand.RowStyles[1].Height + Px(12));
                for (var row = 1; row <= tabs.Length; row++)
                    sidebar.RowStyles[row].Height = Math.Max(Px(48), TextHeight(tabs[row - 1]) + Px(12));
                FitSidebarHeight();
                var fieldWidth = Math.Max(Px(160),
                    TextRenderer.MeasureText("Eyebrow sensitivity", Font).Width + Px(24));
                foreach (var card in liveFieldTables)
                {
                    var inset = card.BackColor == Inset ? card.Padding.Left : 0;
                    card.ColumnStyles[0].Width = fieldWidth - inset;
                }
                foreach (var page in pageList) FitPageWidth(page);
                FitFeatureGroups();
                connectionPicker.ColumnStyles[0].Width = fieldWidth;
                setupSource.ColumnStyles[0].Width = fieldWidth;
                _outputRouteSetup.Height = Math.Max(Px(40), _outputRouteSetup.Font.Height + Px(14));
                foreach (var field in wirelessFields) field.ColumnStyles[0].Width = fieldWidth;
                _fitSetupNavigationDpi?.Invoke();
                FitSetupProgressText();
                FitModelManagerHeight();
                FitActivityHeight();

                foreach (var toggle in new[] { _gaze, _tongue, _pupil, _cameraPreview,
                    _individualCheekPuff, _individualCheekSuck, _eyebrowBoost, _hybridHands, _controllerTouchpad, _launchWithSteamVr })
                    FitFeatureToggle(toggle);
                _fps.Width = Px(84);
                _pupilSensitivity.Width = Px(220);
                _eyebrowSensitivity.Width = Px(220);
                _visibilityMode.Width = Math.Max(Px(265),
                    TextRenderer.MeasureText("Weighted camera + native", _visibilityMode.Font).Width + Px(44));
                _smoothing.Width = Px(180);
                _smoothing.Height = Px(32);
                _swipeSensitivity.Height = Px(32);
                _swipeSensitivity.MinimumSize = new Size(Px(80), Px(32));
                _swipeSensitivityValue.MinimumSize = new Size(Px(56), 0);
                foreach (var box in new[] { _eyeProfiles, _tongueModels, _fps, _pupilSensitivity,
                    _eyebrowSensitivity,
                    _visibilityMode, _connectionMode, _trackingSourceSetup, _trackingSourceLive, _touchpadMode, _touchpadSide,
                    _quickDatasets, _focusedDatasets, _fullDatasets,
                    _quickRecordedDatasets, _focusedRecordedDatasets, _fullRecordedDatasets })
                {
                    box.MinimumSize = Size.Empty;
                    box.ItemHeight = Math.Max(Px(26), (int)Math.Ceiling(box.Font.GetHeight()) + Px(8));
                    // ComboBox.PreferredSize ignores owner-drawn ItemHeight.
                    // Reserve the actual height so following rows cannot overlap.
                    box.MinimumSize = new Size(0, box.Height);
                    if (box.Parent is TableLayoutPanel fields)
                    {
                        var row = fields.GetRow(box);
                        if (row < 0) row = fields.GetPositionFromControl(box).Row;
                        if (row < 0) continue;
                        while (fields.RowStyles.Count <= row) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                        fields.RowStyles[row].SizeType = SizeType.Absolute;
                        fields.RowStyles[row].Height = box.Height + box.Margin.Vertical;
                    }
                }
                _modelList.ItemHeight = Math.Max(Px(40), (int)Math.Ceiling(_modelList.Font.GetHeight()) + Px(12));
                foreach (var button in new[] { _setupRuntimeButton, checkVrchatSetup, _installBackupModuleButton,
                    _setupGazeButton, _recoverGazeButton, _inspectGazeButton, _resetLegacyGazeButton, _setupAmdButton, _enableWirelessButton, _connectWirelessButton, _reconnectUsbButton, _forgetUsbButton,
                    _pairWirelessButton, _disableWirelessButton, _installHandsButton, _removeHandsButton })
                    button.Height = Px(42);
                gazeActions.RowStyles[0].Height = Px(50);
            }
            finally
            {
                lowerFace.ResumeLayout(true);
                main.ResumeLayout(true);
                shell.ResumeLayout(true);
            }
            foreach (var page in pageList) FitPageText(page);
            FitFooterColumns();
        }
        Shown += (_, _) => ApplyDpiLayout();
        DpiChanged += (_, _) =>
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!IsDisposed && !Disposing) ApplyDpiLayout();
                }));
            }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
            {
                // Closing can destroy the handle before the queued resize.
            }
        };
        return shell;
    }
}
