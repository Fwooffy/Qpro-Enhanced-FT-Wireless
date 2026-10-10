using System.Drawing;
using System.Diagnostics;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private Action? _flushResponsiveLayout;

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

        var sidebarViewport = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Panel, Margin = Padding.Empty };
        var sidebar = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 472,
            ColumnCount = 1,
            RowCount = 7,
            BackColor = Panel,
            Padding = new Padding(12, 24, 12, 16),
            Margin = Padding.Empty,
        };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        for (var row = 1; row <= 5; row++) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        var brand = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var brandTitle = new Label { Text = "QPRO HUB", Dock = DockStyle.Fill, Font = new Font(UiFontName, 14F, FontStyle.Bold), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var brandSubtitle = new Label { Text = "Face tracking control", Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
        brand.Controls.Add(brandTitle, 0, 0);
        brand.Controls.Add(brandSubtitle, 0, 1);
        sidebar.Controls.Add(brand, 0, 0);

        var liveTab = NavigationButton("Live tracking", HubIcon.Headset);
        var setupTab = NavigationButton("First-time setup", HubIcon.Setup);
        var personalizationTab = NavigationButton("Personalize", HubIcon.Sliders);
        var modelsTab = NavigationButton("Model manager", HubIcon.Models);
        var activityTab = NavigationButton("Activity", HubIcon.Activity);
        var tabs = new[] { setupTab, liveTab, personalizationTab, modelsTab, activityTab };
        for (var index = 0; index < tabs.Length; index++) sidebar.Controls.Add(tabs[index], 0, index + 1);
        var sidebarNotice = new Label
        {
            Text = "ROOTED QUEST PRO REQUIRED\nGaze and pupil are experimental",
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font(UiFontName, 8.5F),
        };
        sidebar.Controls.Add(sidebarNotice, 0, 6);
        sidebarViewport.Controls.Add(sidebar);
        shell.Controls.Add(sidebarViewport, 0, 0);
        void FitSidebarWidth()
        {
            var dpiScale = shell.DeviceDpi / 96F;
            var textWidth = tabs.Max(tab => TextRenderer.MeasureText(tab.Text, tab.Font).Width);
            var requiredWidth = textWidth + sidebar.Padding.Horizontal + (int)Math.Ceiling(54 * dpiScale);
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
        var header = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(38, 16, 38, 7), Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        var pageTitle = new Label { Dock = DockStyle.Fill, Font = new Font(UiFontName, 20F, FontStyle.Bold), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        var pageSubtitle = new Label { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
        header.Controls.Add(pageTitle, 0, 0);
        _updateLink.AutoSize = true;
        _updateLink.Anchor = AnchorStyles.Right;
        _updateLink.TextAlign = ContentAlignment.MiddleRight;
        _updateLink.Margin = new Padding(16, 0, 0, 0);
        header.Controls.Add(_updateLink, 1, 0);
        header.Controls.Add(pageSubtitle, 0, 1);
        header.SetColumnSpan(pageSubtitle, 2);
        main.Controls.Add(header, 0, 0);
        var pages = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty };
        main.Controls.Add(pages, 0, 1);
        shell.Controls.Add(main, 1, 0);

        void FitPageWidth(Panel page)
        {
            if (WindowState == FormWindowState.Minimized) return;
            // All pages share the same readable column and leave small windows
            // free to shrink without creating a horizontal scrollbar.
            var scale = DeviceDpi / 96F;
            int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
            var columnWidth = Math.Min(Px(1160), Math.Max(1,
                page.Width - Px(44) - SystemInformation.VerticalScrollBarWidth));
            var extra = Math.Max(0, page.ClientSize.Width - Px(44) - columnWidth);
            page.Padding = new Padding(Px(22), Px(8), Px(22) + extra, Px(20));
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

        var livePage = NewPage();
        var liveLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 4, Padding = Padding.Empty, Margin = Padding.Empty };
        liveLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) liveLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            var button = SecondaryButton("Show " + text);
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
        Span(liveSource, SectionTitle("Your session", HubIcon.Headset), 0);
        LiveField(liveSource, 1, "Streaming app", _trackingSourceLive);
        var statuses = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = false,
            ColumnCount = 2, RowCount = 4, Margin = new Padding(0, 8, 0, 3) };
        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 4; row++) statuses.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var statusItems = new[]
        {
            ("Quest ADB", _usbStatus), ("SteamVR", _steamStatus), ("VRCFaceTracking", _vrcftStatus),
            ("Qpro module", _bridgeStatus), ("PC runtime", _runtimeStatus),
        };
        var readinessItems = new List<FlowLayoutPanel>();
        for (var index = 0; index < statusItems.Length; index++)
        {
            // A status wraps within its own cell at narrow widths instead of
            // taking two fixed rows on every display.
            var item = new BufferedFlowLayoutPanel { Dock = DockStyle.Top,
                WrapContents = true, Margin = new Padding(0, 0, 12, 6), TabStop = false };
            item.Controls.Add(new Label { Text = statusItems[index].Item1 + ":", AutoSize = true,
                ForeColor = Muted, Margin = new Padding(0, 0, 6, 0) });
            statusItems[index].Item2.Margin = Padding.Empty;
            item.Controls.Add(statusItems[index].Item2);
            statuses.Controls.Add(item, index % 2, index / 2);
            readinessItems.Add(item);
        }
        bool fittingReadiness = false;
        void FitLiveReadiness()
        {
            if (fittingReadiness) return;
            fittingReadiness = true;
            try
            {
                var scale = DeviceDpi / 96F;
                // Choose columns from the full label/value pair, not a fixed
                // width breakpoint. Each status stays inline when a column can
                // fit it; smaller windows use fewer columns before wrapping.
                var cellWidth = readinessItems.Select((item, index) =>
                {
                    var name = (Label)item.Controls[0];
                    var value = statusItems[index].Item2;
                    const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                    return TextRenderer.MeasureText(name.Text, name.Font, Size.Empty, flags).Width
                        + name.Margin.Horizontal
                        + TextRenderer.MeasureText(value.Text, value.Font, Size.Empty, flags).Width
                        + value.Margin.Horizontal + item.Margin.Horizontal + (int)Math.Ceiling(4 * scale);
                }).DefaultIfEmpty((int)Math.Ceiling(240 * scale)).Max();
                var columns = Math.Clamp(statuses.ClientSize.Width / Math.Max(1, cellWidth), 1, 3);
                var rows = (int)Math.Ceiling(statusItems.Length / (double)columns);
                if (statuses.ColumnCount != columns)
                {
                    statuses.SuspendLayout();
                    try
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
                    finally { statuses.ResumeLayout(true); }
                }
                var rowHeights = new int[rows];
                for (var index = 0; index < statusItems.Length; index++)
                {
                    var item = readinessItems[index];
                    var name = (Label)item.Controls[0];
                    var value = statusItems[index].Item2;
                    var width = Math.Max(100, statuses.ClientSize.Width / columns - item.Margin.Horizontal);
                    name.MaximumSize = value.MaximumSize = new Size(width, 0);
                    var nameSize = name.PreferredSize;
                    var valueSize = value.PreferredSize;
                    item.Height = nameSize.Width + name.Margin.Horizontal + valueSize.Width <= width
                        ? Math.Max(nameSize.Height, valueSize.Height) : nameSize.Height + valueSize.Height;
                    rowHeights[index / columns] = Math.Max(rowHeights[index / columns], item.Height + item.Margin.Vertical);
                }
                // Fix the total to the measured rows. Otherwise WinForms can
                // stretch the last flow row into blank space while autosizing.
                statuses.Height = rowHeights.Sum();
            }
            finally { fittingReadiness = false; }
        }
        statuses.SizeChanged += (_, _) => FitLiveReadiness();
        foreach (var (_, label) in statusItems) label.TextChanged += (_, _) => FitLiveReadiness();
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
        Span(sessionCameraSettings, _cameraPreview, 1);
        Span(sessionCameraSettings, Info("Camera preview is off by default. Shared by tongue, camera cheeks and pupils. A higher FPS cap uses more PC resources. Hiding the preview keeps tracking active. These options apply the next time tracking starts."), 2);
        connectionDetails.Controls.Add(sessionCameraSettings);
        _trackingSourceLiveNote.Margin = new Padding(0, 2, 0, 8);
        connectionDetails.Controls.Add(_trackingSourceLiveNote);
        var connectionActions = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            WrapContents = true, Margin = Padding.Empty };
        connectionActions.Controls.Add(ActionButton("Refresh connection status", (_, _) => { ReloadProfiles(); _ = RefreshStatusAsync(); }));
        connectionActions.Controls.Add(ActionButton("Open setup", (_, _) => setupTab.PerformClick()));
        connectionDetails.Controls.Add(connectionActions);
        Span(liveSource, Info("Cheek adjustments apply while Qpro tracking runs. Stop tracking or close the Hub to restore native cheek values. Eyebrow adjustments apply immediately; turn them off for native eyebrows."), 3);
        Details(liveSource, connectionDetails, "connection and camera settings", 4);
        liveLayout.Controls.Add(liveSource);

        var eyesCard = LiveFields();
        Span(eyesCard, SectionTitle("Eyes", HubIcon.Eyes), 0);
        Span(eyesCard, Info("Using an independent-gaze Magisk module? Leave Hub gaze off and skip Check gaze setup and Prepare gaze. Pupil and eyebrow features work separately."), 1);
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
        Span(eyeSettings, Info("Use one independent gaze method at a time. Leave the Hub option off while a Magisk gaze module is active. The Hub method briefly freezes the headset while restarting tracking; wait for Activity to confirm it is ready."), 3);
        Span(eyeSettings, Info("Pupils need a short warmup: look straight and hold steady. Shared FPS and preview options are under Your session > Show connection and camera settings."), 4);
        Details(eyesCard, eyeSettings, "eye settings and guidance", 5);
        liveLayout.Controls.Add(eyesCard);

        var lowerFace = LiveFields();
        Span(lowerFace, SectionTitle("Lower-face tracking", HubIcon.LowerFace), 0);
        LiveField(lowerFace, 1, "Lower-face model", _tongueModels);
        _tongueModelNote.Margin = new Padding(0, 3, 0, 8);
        Span(lowerFace, _tongueModelNote, 2);
        Span(lowerFace, _tongue, 3);
        Span(lowerFace, _cameraCheekPuff, 4);
        _cameraCheekSourceNote.Margin = new Padding(0, 3, 0, 8);
        Span(lowerFace, _cameraCheekSourceNote, 5);
        var cameraSettings = LiveFields();
        cameraSettings.BackColor = Inset;
        cameraSettings.Padding = new Padding(8);
        cameraSettings.Margin = new Padding(0, 2, 0, 4);
        LiveField(cameraSettings, 0, "Motion smoothing", _smoothing);
        LiveField(cameraSettings, 1, "Tongue visibility", _visibilityMode);
        var smoothingHelp = new ToolTip();
        smoothingHelp.SetToolTip(_smoothing, "Higher values steady small movements. Fast tongue movements still respond quickly.");
        _smoothing.AccessibleDescription = "Higher values steady small movements. Fast tongue movements still respond quickly. Use the arrow keys to adjust.";
        Disposed += (_, _) => smoothingHelp.Dispose();
        Span(cameraSettings, Info("Restart tracking after changing the model or camera options. Shared FPS and preview options are under Your session > Show connection and camera settings."), 2);
        Details(lowerFace, cameraSettings, "camera settings", 6);
        Span(lowerFace, SectionCaption("STREAMING APP FACE ADJUSTMENTS"), 8);
        Span(lowerFace, _individualCheekPuff, 9);
        var cheekPuffActions = new BufferedFlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            WrapContents = true, Margin = Padding.Empty };
        var cheekPuffSelector = new BufferedPanel { Margin = Padding.Empty };
        _cheekPuffStyle.Dock = DockStyle.None;
        _cheekPuffStyle.Margin = Padding.Empty;
        cheekPuffSelector.Controls.Add(_cheekPuffStyle);
        cheekPuffActions.Controls.Add(cheekPuffSelector);
        cheekPuffActions.Controls.Add(_calibrateCheekPuff);
        LiveField(lowerFace, 10, "Cheek puff style", cheekPuffActions);
        var cheekPuffLabel = (Label)lowerFace.GetControlFromPosition(0, 10)!;
        // This row can wrap its button below the selector. Keep the caption
        // aligned with the selector, rather than the combined wrapped height.
        cheekPuffLabel.Dock = DockStyle.None;
        cheekPuffLabel.AutoSize = false;
        cheekPuffLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        void FitCheekActions()
        {
            var lineHeight = Math.Max(_cheekPuffStyle.Height, _calibrateCheekPuff.Height);
            cheekPuffSelector.Size = new Size(_cheekPuffStyle.Width + 12, lineHeight + 8);
            _cheekPuffStyle.Location = new Point(0, (cheekPuffSelector.Height - _cheekPuffStyle.Height) / 2);
            _calibrateCheekPuff.Margin = new Padding(0, 4, 0, 4);
            cheekPuffLabel.Height = lineHeight + 8;
        }
        FitCheekActions();
        Span(lowerFace, _individualCheekSuck, 11);
        LiveField(lowerFace, 12, "Cheek suck style", _cheekSuckStyle);
        var cheekHelp = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Margin = Padding.Empty };
        LiveRows(cheekHelp, 1);
        cheekHelp.Controls.Add(Info("Cheek puff: Calibrated gives a smooth relaxed-to-full response, using the developer baseline until you calibrate. Calibrate with the current streaming app's Qpro module running. 1/0 selects a full-strength cheek; Balanced is gentler."));
        cheekHelp.Controls.Add(Info("Cheek suck: Strong selects the leading side; Balanced is gentler. These controls adjust the existing streaming-app values; camera cheek training uses its own recorded poses."));
        cheekHelp.Controls.Add(Info("Camera cheek puff uses the selected camera model. Cheek puff style and Calibrate cheek puff apply only when camera cheek output is off or stops."));
        Span(lowerFace, Info("Start tracking to apply cheek adjustments. Stop tracking or close the Hub to restore native cheeks; your selected style and calibration stay saved."), 13);
        Details(lowerFace, cheekHelp, "cheek adjustment details", 14);
        liveLayout.Controls.Add(lowerFace);

        var handsCard = LiveFields();
        Span(handsCard, SectionTitle("Hands and controllers", HubIcon.Controller), 0);
        Span(handsCard, Info("Experimental · Virtual Desktop only. Check your headset and app versions before use."), 1);
        Span(handsCard, _hybridHands, 2);
        Span(handsCard, _controllerTouchpad, 3);
        _handsStatus.Margin = new Padding(0, 3, 0, 8);
        Span(handsCard, _handsStatus, 4);
        _checkHandsButton.Margin = new Padding(0, 4, 8, 4);
        Span(handsCard, _checkHandsButton, 5);
        var handsDetails = LiveFields();
        handsDetails.BackColor = Inset;
        handsDetails.Padding = new Padding(12);
        LiveField(handsDetails, 0, "Thumb-rest mode", _touchpadMode);
        Span(handsDetails, Info("Hands + controllers combines optical fingers with controller position and buttons when compatibility checks pass. Thumb-rest input offers Trackpad, Relative joystick, Swipe or Desktop mouse."), 1);
        Span(handsDetails, Info("Start tracking applies these options. Desktop mouse can move the Windows pointer. Stop tracking stops these inputs. Close SteamVR before installing or removing their optional components."), 2);
        Span(handsDetails, Info("This experimental option adds processing work. If game FPS drops, press Stop tracking and keep it off."), 3);
        Details(handsCard, handsDetails, "hand and controller details", 6);
        liveLayout.Controls.Add(handsCard);

        var setupPage = NewPage();
        var setupLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty };
        setupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++) setupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setupPage.Controls.Add(setupLayout);
        var setupIntro = Card(); setupIntro.Dock = DockStyle.Top;
        setupIntro.Controls.Add(SectionTitle("Three steps to get ready", HubIcon.Setup));
        setupIntro.Controls.Add(Info("Before you start: a rooted Meta Quest Pro and the latest VRCFaceTracking from Steam."));
        setupIntro.Controls.Add(Info("1. Connect your Quest Pro   →   2. Install the PC runtime   →   3. Install your streaming app's module"));
        var setupNext = Info("After setup, open Live tracking to choose features and start a session. Optional extras are below the three steps.");
        setupNext.ForeColor = Accent;
        setupIntro.Controls.Add(setupNext);
        setupLayout.Controls.Add(setupIntro);
        var connectionCard = Card(); connectionCard.Dock = DockStyle.Top;
        connectionCard.ColumnCount = 1;
        connectionCard.Controls.Add(SectionCaption("STEP 1"));
        connectionCard.Controls.Add(SectionTitle("Connect your Quest Pro", HubIcon.Headset));
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
        LiveRows(_wirelessSetup, 6);
        _wirelessSetup.Controls.Add(Info("Enter the Quest's Wi-Fi IP and press Connect to Quest. If it reports ready, continue with the next setup card; pairing is not needed. Use Enable from USB if wireless ADB has not been enabled yet."), 0, 0);
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
        _wirelessSetup.Controls.Add(_enableWirelessButton, 0, 2);
        _wirelessSetup.Controls.Add(_connectWirelessButton, 0, 3);
        var pairingOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        pairingOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pairingOptions.Controls.Add(Info("Use Pair and connect only when Android shows a six-digit pairing code. Copy the temporary pairing IP:port and code from that dialog; its port differs from the regular Quest connection port."));
        pairingOptions.Controls.Add(ConnectionField("Pairing IP:port", _pairingEndpoint));
        pairingOptions.Controls.Add(ConnectionField("Six-digit code", _pairingCode));
        pairingOptions.Controls.Add(_pairWirelessButton);
        Details(_wirelessSetup, pairingOptions, "pairing options", 4);
        _wirelessSetup.Controls.Add(_disableWirelessButton, 0, 6);
        connectionCard.Controls.Add(_wirelessSetup);
        setupLayout.Controls.Add(connectionCard);
        var setupSource = Card(); setupSource.Dock = DockStyle.Top;
        setupSource.ColumnCount = 2;
        setupSource.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        setupSource.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setupSource.BackColor = Raised;
        setupSource.Padding = Padding.Empty;
        setupSource.Controls.Add(SectionCaption("CHOOSE YOUR STREAMING APP"), 0, 0);
        setupSource.SetColumnSpan(setupSource.GetControlFromPosition(0, 0)!, 2);
        setupSource.Controls.Add(FieldLabel("Streaming app"), 0, 1);
        _trackingSourceSetup.Margin = new Padding(0, 3, 0, 3);
        _trackingSourceSetup.AccessibleName = "Streaming app";
        setupSource.Controls.Add(_trackingSourceSetup, 1, 1);
        _trackingSourceSetupNote.Margin = new Padding(0, 2, 0, 8);
        setupSource.Controls.Add(_trackingSourceSetupNote, 0, 2);
        setupSource.SetColumnSpan(_trackingSourceSetupNote, 2);
        _setupProgressContainer.Dock = DockStyle.Top;
        _setupProgressContainer.AutoSize = true;
        _setupProgressContainer.ColumnCount = 1;
        _setupProgressContainer.BackColor = Panel;
        _setupProgressContainer.Padding = new Padding(16);
        _setupProgressContainer.Margin = new Padding(0, 0, 0, 12);
        _setupProgressContainer.Visible = false;
        _setupProgressContainer.Controls.Add(new Label { Text = "Setup progress", AutoSize = true, ForeColor = Color.White, Font = new Font(UiFontName, 10F, FontStyle.Bold) });
        _setupProgressContainer.Controls.Add(_setupProgressStatus);
        _setupProgressContainer.Controls.Add(_setupProgress);
        setupLayout.Controls.Add(_setupProgressContainer);
        _setupRuntimeButton.Click += async (_, _) => await RunSetupStepAsync("PC runtime setup", "setup-runtime.ps1", "PC runtime is ready.", "Next: close VRCFaceTracking and install the Qpro module for your streaming app.");
        _setupBridgeButton.Click += async (_, _) => await InstallQproModuleAsync(steamLink: false);
        _setupSteamLinkModuleButton.Click += async (_, _) => await InstallQproModuleAsync(steamLink: true);
        _uninstallBridgeButton.Click += async (_, _) =>
        {
            if (VrcftModuleProcessRunning())
            {
                MessageBox.Show(this, "Close VRCFaceTracking and wait for its ModuleProcess helper to exit, then press Uninstall Qpro module again.", "Close VRCFaceTracking", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this,
                    "Remove the Qpro module from VRCFaceTracking and restore any Virtual Desktop modules saved by this Qpro copy?\n\nPersonal tongue models and captures will stay in place.",
                    "Uninstall Qpro module", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            await RunSetupStepAsync("Uninstall Qpro module", "uninstall-vrcft-eye-bridge.ps1",
                "The Qpro VRCFaceTracking module has been removed.",
                "Restart VRCFaceTracking. If normal Virtual Desktop face tracking is missing, install its official module again.");
        };
        _setupGazeButton.Click += async (_, _) => await PrepareGazeAsync();
        _recoverGazeButton.Click += async (_, _) => await RecoverGazeAsync();
        _inspectGazeButton.Click += async (_, _) => await InspectGazeAsync();
        _resetLegacyGazeButton.Click += async (_, _) => await ResetLegacyGazeAsync();
        // Step cards size to their copy and actions, avoiding large empty bands
        // at high resolutions and clipped descriptions in narrow windows.
        var setupCards = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        setupCards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 2; row++) setupCards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        setupCards.Controls.Add(SetupStepCard("2", "Install the PC runtime", "Installs Qpro's private Python and tracking libraries. Keep the Hub open until the downloads finish.", _setupRuntimeStatus, _setupRuntimeButton), 0, 0);
        var moduleCard = (TableLayoutPanel)SetupStepCard("3", "Install your VRCFaceTracking module", "Choose the app you stream with. Close VRCFaceTracking, install its matching module below, then reopen it. This replaces the other Qpro source module.",
            _setupBridgeStatus, _setupBridgeButton, _setupSteamLinkModuleButton);
        var moduleDescription = moduleCard.GetControlFromPosition(0, 3)!;
        moduleCard.Controls.Remove(moduleDescription);
        var moduleChoices = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        moduleChoices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        moduleChoices.Controls.Add(moduleDescription);
        moduleChoices.Controls.Add(setupSource);
        moduleCard.Controls.Add(moduleChoices, 0, 3);
        var moduleActions = (TableLayoutPanel)moduleCard.GetControlFromPosition(0, 4)!;
        void FitSelectedModuleAction()
        {
            // Display the selected source's action in one stable row. Keeping
            // both controls lets their existing install handlers stay intact.
            var steamLink = _trackingSourceSetup.SelectedIndex == 1;
            var actionHeight = (int)Math.Ceiling(54 * DeviceDpi / 96F);
            moduleActions.SuspendLayout();
            try
            {
                _setupBridgeButton.Visible = !steamLink;
                _setupSteamLinkModuleButton.Visible = steamLink;
                moduleActions.RowStyles[0].SizeType = SizeType.Absolute;
                moduleActions.RowStyles[0].Height = steamLink ? 0 : actionHeight;
                moduleActions.RowStyles[1].SizeType = SizeType.Absolute;
                moduleActions.RowStyles[1].Height = steamLink ? actionHeight : 0;
                moduleCard.RowStyles[4].Height = actionHeight;
            }
            finally { moduleActions.ResumeLayout(true); }
        }
        _trackingSourceSetup.SelectedIndexChanged += (_, _) => FitSelectedModuleAction();
        FitSelectedModuleAction();
        var moduleMaintenance = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        moduleMaintenance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        moduleMaintenance.Controls.Add(Info("Close VRCFaceTracking before removing its Qpro module. Your personal models and recordings stay in place."));
        moduleMaintenance.Controls.Add(_uninstallBridgeButton);
        Details(moduleCard, moduleMaintenance, "module uninstall", 5);
        setupCards.Controls.Add(moduleCard, 0, 1);
        setupLayout.Controls.Add(setupCards);
        setupLayout.Controls.Add(BuildHeadsetCompatibilityCard());
        var gazeSetup = Card(); gazeSetup.ColumnCount = 1;
        Span(gazeSetup, SectionTitle("Optional independent gaze", HubIcon.Eyes), 0);
        Span(gazeSetup, _setupGazeStatus, 1);
        Span(gazeSetup, Info("Using a Magisk gaze module? Skip Check gaze setup and Prepare gaze. Leave Independent Eye Gaze off in Live tracking."), 2);
        var gazeOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        gazeOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Span(gazeOptions, Info("These tools are only for the Hub's temporary gaze method. Check gaze setup is optional diagnostics; Prepare gaze validates the headset before preparing its local patch. Neither is needed for Magisk gaze, tongue, camera cheeks, pupils or ordinary face tracking."), 0);
        var gazeActions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        gazeActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        gazeActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _inspectGazeButton.Dock = _setupGazeButton.Dock = DockStyle.Fill;
        _inspectGazeButton.Margin = new Padding(0, 4, 8, 4);
        _setupGazeButton.Margin = new Padding(0, 4, 0, 4);
        gazeActions.Controls.Add(_inspectGazeButton, 0, 0);
        gazeActions.Controls.Add(_setupGazeButton, 1, 0);
        Span(gazeOptions, gazeActions, 1);
        var gazeRecovery = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        gazeRecovery.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gazeRecovery.Controls.Add(Info("Recover Qpro gaze restores the previous eye-model state saved by a recorded Qpro session. Reset legacy gaze selects normal gaze for an older session without a record, after confirmation. Neither option disables Magisk or removes the PC module."));
        gazeRecovery.Controls.Add(_recoverGazeButton);
        gazeRecovery.Controls.Add(_resetLegacyGazeButton);
        Details(gazeOptions, gazeRecovery, "gaze recovery tools", 2);
        Details(gazeSetup, gazeOptions, "gaze setup", 3);
        setupLayout.Controls.Add(gazeSetup);
        var handsSetup = Card(); handsSetup.ColumnCount = 1;
        handsSetup.Controls.Add(SectionTitle("Optional hands and controllers", HubIcon.Controller));
        handsSetup.Controls.Add(Info("Experimental · Virtual Desktop only. Check compatibility on Live tracking before enabling."));
        var handsOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        handsOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        handsOptions.Controls.Add(Info("Close SteamVR before installing or uninstalling, then reopen it through Virtual Desktop. These components are separate from the VRCFaceTracking face module."));
        handsOptions.Controls.Add(_installHandsButton);
        handsOptions.Controls.Add(_removeHandsButton);
        Details(handsSetup, handsOptions, "hands setup and uninstall", 2);
        setupLayout.Controls.Add(handsSetup);
        var amdCard = Card(); amdCard.Dock = DockStyle.Top; amdCard.ColumnCount = 1;
        amdCard.Controls.Add(SectionTitle("Optional AMD ROCm acceleration", HubIcon.Module));
        _setupAmdStatus.Tag = "responsive-info";
        amdCard.Controls.Add(_setupAmdStatus);
        amdCard.Controls.Add(Info("Optional acceleration for compatible Radeon graphics cards. Install the PC runtime first."));
        var amdOptions = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        amdOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        amdOptions.Controls.Add(Info($"ROCm {HubRocmRuntime.InstallVersion} accelerates training and tracking on listed discrete Radeon GPUs. Setup enables it only after GPU tests pass. This remains experimental; earlier verified ROCm runtimes stay available as fallbacks."));
        amdOptions.Controls.Add(_amdGpuStatus);
        var amdSupportLink = new LinkLabel { Text = "ROCm 7.2.1 fallback GPU list", AutoSize = true, LinkColor = Accent, ActiveLinkColor = Accent, VisitedLinkColor = Accent, Margin = new Padding(0, 3, 0, 8) };
        amdSupportLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/compatibility/compatibilityrad/windows/windows_compatibility.html") { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Could not open AMD support list"); }
        };
        var amdExperimentalLink = new LinkLabel { Text = $"AMD TheRock ROCm {HubRocmRuntime.InstallVersion} GPU targets", AutoSize = true, LinkColor = Accent, ActiveLinkColor = Accent, VisitedLinkColor = Accent, Margin = new Padding(0, 3, 0, 8) };
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
        Details(amdCard, amdOptions, "AMD setup", 3);
        setupLayout.Controls.Add(amdCard);

        var personalizationPage = NewPage();
        var personalLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        personalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        personalizationPage.Controls.Add(personalLayout);
        var personalIntro = Card(); personalIntro.Dock = DockStyle.Top;
        personalIntro.Controls.Add(SectionTitle("Capture and train", HubIcon.Sliders));
        personalIntro.Controls.Add(Info("Record tongue and cheek poses to fit tracking to your face and headset position. Quick refinement and Full dataset include 21 cheek camera cards. The camera cheek option is experimental and remains off until you enable it."));
        personalLayout.Controls.Add(personalIntro);
        _trainingProgressContainer.Dock = DockStyle.Top;
        _trainingProgressContainer.AutoSize = true;
        _trainingProgressContainer.ColumnCount = 1;
        _trainingProgressContainer.BackColor = Panel;
        _trainingProgressContainer.Padding = new Padding(16);
        _trainingProgressContainer.Margin = new Padding(0, 0, 0, 12);
        _trainingProgressContainer.Controls.Add(new Label { Text = "Training progress", AutoSize = true, ForeColor = Color.White, Font = new Font(UiFontName, 10F, FontStyle.Bold) });
        _trainingProgressContainer.Controls.Add(_trainingProgressStatus);
        _trainingProgressContainer.Controls.Add(_trainingProgress);
        personalLayout.Controls.Add(_trainingProgressContainer);
        var choices = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) choices.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        choices.Controls.Add(WorkflowCard("Quick refinement · 15–30 min · Recommended", "Start here to fit the selected model to your face. The guided capture includes tongue poses and 21 experimental cheek camera cards. Training saves a separate personalized copy.", _quickDatasets, _quickQueueStatus, _quickRecordedDatasets,
            ActionButton("1. Record refinement", async (_, _) => await ConfirmCaptureAsync(TongueDatasetKind.Quick)), ActionButton("2. Train personalized copy", async (_, _) => await TrainTongueAsync(TongueDatasetKind.Quick)),
            ActionButton("Delete selected dataset…", (_, _) => DeleteRecordedDataset(TongueDatasetKind.Quick))), 0, 0);
        personalLayout.Controls.Add(choices);
        var alternativeCaptures = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        alternativeCaptures.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        alternativeCaptures.Controls.Add(WorkflowCard("Focused diagonals + facial hair · 15–30 min", "Extra practice for diagonal tongue movement and facial-hair shadows. Record matching hidden and visible poses, then train a new copy.", _focusedDatasets, _focusedQueueStatus, _focusedRecordedDatasets,
            ActionButton("1. Record focused dataset", async (_, _) => await ConfirmCaptureAsync(TongueDatasetKind.Focused)), ActionButton("2. Train focused copy", async (_, _) => await TrainTongueAsync(TongueDatasetKind.Focused)),
            ActionButton("Delete selected dataset…", (_, _) => DeleteRecordedDataset(TongueDatasetKind.Focused))), 0, 0);
        alternativeCaptures.Controls.Add(WorkflowCard("Full dataset · 60–120 min", "Broader tongue coverage and 21 experimental cheek camera cards. Choose this when you can take your time and follow each pose carefully.", _fullDatasets, _fullQueueStatus, _fullRecordedDatasets,
            ActionButton("1. Record full dataset", async (_, _) => await ConfirmCaptureAsync(TongueDatasetKind.Full)), ActionButton("2. Train new personal model", async (_, _) => await TrainTongueAsync(TongueDatasetKind.Full)),
            ActionButton("Delete selected dataset…", (_, _) => DeleteRecordedDataset(TongueDatasetKind.Full))), 0, 1);
        var captureOptions = Card(); captureOptions.ColumnCount = 1;
        Span(captureOptions, SectionTitle("More capture options", HubIcon.Sliders), 0);
        Span(captureOptions, Info("Use a focused capture for specific tongue problems, or a full dataset for broader coverage."), 1);
        Details(captureOptions, alternativeCaptures, "focused and full captures", 2);
        personalLayout.Controls.Add(captureOptions);
        var cameraCheekTraining = Card(); cameraCheekTraining.Dock = DockStyle.Top;
        cameraCheekTraining.ColumnCount = 1;
        Span(cameraCheekTraining, SectionTitle("Experimental tongue + cheeks model", HubIcon.LowerFace), 0);
        Span(cameraCheekTraining, Info("Quick refinement and Full dataset already include cheek camera poses. Use this separate capture to add cheeks to a new copy of a selected tongue model."), 1);
        var cameraCheekTrainingBody = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        cameraCheekTrainingBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        LiveRows(cameraCheekTrainingBody, 6);
        cameraCheekTrainingBody.Controls.Add(Info("Native cheek calibration remains available. Record all guided poses and test the resulting copy before using it for a session."), 0, 0);
        cameraCheekTrainingBody.Controls.Add(new Label { Text = "Parent tongue model", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 0, 4) }, 0, 1);
        _cheekCameraBaseModels.Margin = new Padding(0, 3, 0, 8);
        _cheekCameraBaseModels.AccessibleName = "Parent tongue model";
        cameraCheekTrainingBody.Controls.Add(_cheekCameraBaseModels, 0, 2);
        cameraCheekTrainingBody.Controls.Add(new Label { Text = "Completed cheek camera dataset", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 0, 4) }, 0, 3);
        _cheekCameraDatasets.Margin = new Padding(0, 3, 0, 8);
        _cheekCameraDatasets.AccessibleName = "Completed cheek camera dataset";
        _cheekCameraDatasetNote.Margin = new Padding(0, 0, 0, 8);
        cameraCheekTrainingBody.Controls.Add(_cheekCameraDatasets, 0, 4);
        cameraCheekTrainingBody.Controls.Add(_cheekCameraDatasetNote, 0, 5);
        var cameraCheekActions = new BufferedFlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        cameraCheekActions.Controls.Add(_recordCameraCheeks);
        cameraCheekActions.Controls.Add(_trainCameraCheeks);
        cameraCheekTrainingBody.Controls.Add(cameraCheekActions, 0, 6);
        Details(cameraCheekTraining, cameraCheekTrainingBody, "advanced cheek training", 2);
        personalLayout.Controls.Add(cameraCheekTraining);

        var modelsPage = NewPage();
        var manager = Card(); manager.Dock = DockStyle.Top; manager.AutoSize = false;
        void FitModelManagerHeight()
        {
            var minimum = (int)Math.Ceiling(380 * DeviceDpi / 96F);
            // Dock.Top lets AutoScroll expose the actions when the available
            // viewport is shorter than the list's useful minimum height.
            manager.Height = Math.Max(minimum, modelsPage.ClientSize.Height - modelsPage.Padding.Vertical);
        }
        modelsPage.SizeChanged += (_, _) => FitModelManagerHeight();
        FitModelManagerHeight();
        manager.ColumnCount = 1; manager.RowCount = 4;
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.Controls.Add(SectionTitle("Lower-face model manager", HubIcon.Models), 0, 0);
        manager.Controls.Add(Info("Manage tongue models and experimental tongue + camera cheeks copies. Export creates a portable .qptonguemodel package containing the paired files; import assigns a safe new version."), 0, 1);
        var modelBody = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Inset, Margin = Padding.Empty };
        _modelList.AccessibleName = "Lower-face models";
        modelBody.Controls.Add(_modelList);
        modelBody.Controls.Add(_modelEmpty);
        manager.Controls.Add(modelBody, 0, 2);
        var modelActions = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        modelActions.Controls.Add(ActionButton("Rename", (_, _) => RenameSelectedModel()));
        modelActions.Controls.Add(ActionButton("Export", (_, _) => ExportSelectedModel()));
        modelActions.Controls.Add(ActionButton("Import", (_, _) => ImportModel()));
        modelActions.Controls.Add(ActionButton("Delete", (_, _) => DeleteSelectedModel()));
        modelActions.Controls.Add(ActionButton("Refresh", (_, _) => ReloadProfiles()));
        manager.Controls.Add(modelActions, 0, 3);
        modelsPage.Controls.Add(manager);

        var activityPage = NewPage();
        var activityLayout = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = false,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var feedbackCard = BuildFeedbackCard();
        activityLayout.Controls.Add(feedbackCard, 0, 0);
        var logCard = Card(); logCard.Dock = DockStyle.Fill; logCard.AutoSize = false;
        logCard.ColumnCount = 1; logCard.RowCount = 3;
        logCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logCard.Controls.Add(SectionTitle("Detailed activity", HubIcon.Activity), 0, 0);
        var activityLegend = new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            WrapContents = true, Margin = new Padding(0, 0, 0, 10) };
        foreach (var level in new[] { ActivitySeverity.Normal, ActivitySeverity.Warning, ActivitySeverity.Error })
            activityLegend.Controls.Add(new Label { Text = ActivityName(level), AutoSize = true,
                ForeColor = ActivityColor(level), Margin = new Padding(0, 0, 20, 0) });
        logCard.Controls.Add(activityLegend, 0, 1);
        _log.Margin = Padding.Empty;
        _log.ForeColor = Good;
        _log.AccessibleName = "Detailed activity";
        logCard.Controls.Add(_log, 0, 2);
        activityLayout.Controls.Add(logCard, 0, 1);
        activityPage.Controls.Add(activityLayout);
        bool fittingActivity = false;
        void FitActivityHeight()
        {
            if (fittingActivity) return;
            fittingActivity = true;
            try
            {
                // Short windows scroll the complete feedback and log instead
                // of squeezing the log's heading into an unusable last row.
                var minimumLogHeight = (int)Math.Ceiling(240 * DeviceDpi / 96F);
                var feedbackHeight = Math.Max(feedbackCard.Height, feedbackCard.PreferredSize.Height);
                var available = activityPage.ClientSize.Height - activityPage.Padding.Vertical;
                var height = Math.Max(available, feedbackHeight + feedbackCard.Margin.Vertical
                    + minimumLogHeight + logCard.Margin.Vertical);
                if (activityLayout.Height != height) activityLayout.Height = height;
            }
            finally { fittingActivity = false; }
        }
        activityPage.SizeChanged += (_, _) => FitActivityHeight();
        feedbackCard.SizeChanged += (_, _) => FitActivityHeight();
        FitActivityHeight();

        var pageList = new[] { setupPage, livePage, personalizationPage, modelsPage, activityPage };
        var fittingTextPages = new HashSet<Panel>();
        var pendingTextPages = new HashSet<Panel>();
        var textLayoutTimer = new System.Windows.Forms.Timer(_uiComponents) { Interval = 50 };
        void FitPageText(Panel page)
        {
            if (!page.Visible || WindowState == FormWindowState.Minimized) return;
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
                var availableTextWidth = page.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 40;
                var textWidth = Math.Min((int)Math.Ceiling(900 * DeviceDpi / 96F), Math.Max(240, availableTextWidth));
                void Fit(Control parent)
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
                            if (label.MaximumSize.Width != target)
                                label.MaximumSize = new Size(target, 0);
                        }
                        if (child.HasChildren) Fit(child);
                    }
                }
                Fit(page);
            }
            finally { fittingTextPages.Remove(page); }
        }
        void SchedulePageText(Panel page)
        {
            if (!IsHandleCreated || IsDisposed || Disposing || !page.Visible
                || WindowState == FormWindowState.Minimized) return;
            pendingTextPages.Add(page);
            // Layout can trigger another layout as scrollbars and wrapped text
            // settle. A timer yields to painting and input instead of posting
            // an unbroken chain of BeginInvoke callbacks during a corner drag.
            textLayoutTimer.Start();
        }
        textLayoutTimer.Tick += (_, _) =>
        {
            textLayoutTimer.Stop();
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
            page.PerformLayout();
            FitPageText(page);
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
        var titles = new[] { "First-time setup", "Live tracking", "Lower-face calibration", "Model manager", "Activity" };
        var subtitles = new[]
        {
            "Connect your Quest and set up tracking.",
            "Check connections, choose features, and start a session.",
            "Record and train a model for your own face.",
            "Name, import, export, and manage personal models.",
            "Follow setup, training, and tracking output here.",
        };
        void ShowPage(int index)
        {
            for (var item = 0; item < pageList.Length; item++)
            {
                pageList[item].Visible = item == index;
                StyleNavigationButton(tabs[item], item == index);
            }
            pageList[index].BringToFront();
            pageTitle.Text = titles[index];
            pageSubtitle.Text = subtitles[index];
        }
        for (var index = 0; index < tabs.Length; index++)
        {
            var selected = index;
            tabs[index].Click += (_, _) => ShowPage(selected);
        }
        ShowPage(_environment.HasOpenedBefore ? 1 : 0);

        var footer = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Panel, Padding = new Padding(12, 10, 12, 10), Margin = Padding.Empty };
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
            Text = "made with love and dedication by Fwooffy",
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font(UiFontName, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        footerIdentity.Controls.Add(madeByCredit, 0, 1);
        const string creditPrefix = "Thanks to n0tmast3r · based on ";
        const string originalProject = "Qpro-Enhanced-FT";
        const string originalReleasesUrl = "https://github.com/n0tmast3r/Qpro-Enhanced-FT/releases";
        var originalCredit = new LinkLabel
        {
            Text = creditPrefix + originalProject,
            LinkArea = new LinkArea(creditPrefix.Length, originalProject.Length),
            LinkBehavior = LinkBehavior.AlwaysUnderline,
            LinkColor = Accent,
            ActiveLinkColor = Accent,
            VisitedLinkColor = Accent,
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
                if ((int)footer.ColumnStyles[0].Width != identityWidth) footer.ColumnStyles[0].Width = identityWidth;
                if ((int)footer.ColumnStyles[1].Width != actionWidth) footer.ColumnStyles[1].Width = actionWidth;
                if ((int)footer.ColumnStyles[2].Width != right - left - actionWidth)
                    footer.ColumnStyles[2].Width = right - left - actionWidth;
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
                sidebar.RowStyles.Cast<RowStyle>().Take(6).Sum(style => style.Height));
            var availableHeight = shell.ClientSize.Height - (int)Math.Ceiling(shell.RowStyles[1].Height);
            // The compact sidebar still has all five navigation buttons; the
            // decorative notice yields to them when the fixed footer needs room.
            var showNotice = availableHeight >= baseHeight + noticeHeight + Px(4);
            sidebarNotice.Visible = showNotice;
            sidebar.RowStyles[6].Height = showNotice ? noticeHeight : 0;
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
                header.RowStyles[0].Height = Math.Max(Px(48), TextHeight(pageTitle));
                header.RowStyles[1].Height = Math.Max(Px(28), TextHeight(pageSubtitle));
                main.RowStyles[0].Height = Math.Max(Px(100),
                    header.Padding.Vertical + (int)header.RowStyles[0].Height + (int)header.RowStyles[1].Height + Px(8));
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
                for (var row = 1; row <= 5; row++)
                    sidebar.RowStyles[row].Height = Math.Max(Px(52), TextHeight(tabs[row - 1]) + Px(12));
                FitSidebarHeight();
                var fieldWidth = Math.Max(Px(160),
                    TextRenderer.MeasureText("Eyebrow sensitivity", Font).Width + Px(24));
                foreach (var card in liveFieldTables)
                {
                    var inset = card.BackColor == Inset ? card.Padding.Left : 0;
                    card.ColumnStyles[0].Width = fieldWidth - inset;
                }
                foreach (var page in pageList) FitPageWidth(page);
                connectionPicker.ColumnStyles[0].Width = fieldWidth;
                setupSource.ColumnStyles[0].Width = fieldWidth;
                foreach (var field in wirelessFields) field.ColumnStyles[0].Width = fieldWidth;
                foreach (TableLayoutPanel card in setupCards.Controls)
                {
                    var actionCount = card.GetControlFromPosition(0, 4) is TableLayoutPanel actions ? actions.RowCount : 1;
                    card.RowStyles[4].Height = Px(54 * (ReferenceEquals(card, moduleCard) ? 1 : actionCount));
                }
                FitSelectedModuleAction();
                FitModelManagerHeight();
                FitActivityHeight();

                foreach (var toggle in new[] { _gaze, _tongue, _pupil, _cameraPreview, _cameraCheekPuff,
                    _individualCheekPuff, _individualCheekSuck, _eyebrowBoost, _hybridHands, _controllerTouchpad })
                    FitFeatureToggle(toggle);
                _fps.Width = Px(84);
                _pupilSensitivity.Width = Px(220);
                _eyebrowSensitivity.Width = Px(220);
                _cheekPuffStyle.Width = Px(245);
                _cheekSuckStyle.Width = Px(245);
                _visibilityMode.Width = Math.Max(Px(265),
                    TextRenderer.MeasureText("Weighted camera + native", _visibilityMode.Font).Width + Px(44));
                _smoothing.Width = Px(180);
                _smoothing.Height = Px(32);
                foreach (var box in new[] { _eyeProfiles, _tongueModels, _fps, _pupilSensitivity,
                    _eyebrowSensitivity, _cheekPuffStyle, _cheekSuckStyle,
                    _visibilityMode, _connectionMode, _trackingSourceSetup, _trackingSourceLive, _touchpadMode,
                    _quickDatasets, _focusedDatasets, _fullDatasets,
                    _quickRecordedDatasets, _focusedRecordedDatasets, _fullRecordedDatasets,
                    _cheekCameraBaseModels, _cheekCameraDatasets })
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
                foreach (var button in new[] { _setupRuntimeButton, _setupBridgeButton, _setupSteamLinkModuleButton, _uninstallBridgeButton,
                    _setupGazeButton, _recoverGazeButton, _inspectGazeButton, _resetLegacyGazeButton, _setupAmdButton, _enableWirelessButton, _connectWirelessButton, _reconnectUsbButton, _forgetUsbButton,
                    _pairWirelessButton, _disableWirelessButton, _installHandsButton, _removeHandsButton })
                    button.Height = Px(42);
                FitCheekActions();
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
