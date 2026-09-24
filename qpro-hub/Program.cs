using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var executableRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var rootArgument = Array.FindIndex(args, value => value.Equals("--root", StringComparison.OrdinalIgnoreCase));
        var root = rootArgument >= 0 && rootArgument + 1 < args.Length
            ? Path.GetFullPath(args[rootArgument + 1])
            : File.Exists(Path.Combine(executableRoot, "release-manifest.json"))
                ? executableRoot
                : Directory.GetCurrentDirectory();
        try
        {
            var selfTestArgument = Array.FindIndex(args, value => value.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
            if (selfTestArgument >= 0)
            {
                if (selfTestArgument + 1 >= args.Length) throw new ArgumentException("--self-test requires an output JSON path.");
                WriteSelfTest(root, Path.GetFullPath(args[selfTestArgument + 1]));
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.Dark);
            var renderArgument = Array.FindIndex(args, value => value.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
            if (renderArgument >= 0)
            {
                if (renderArgument + 1 >= args.Length) throw new ArgumentException("--render-preview requires an output PNG path.");
                using var form = new HubForm(root);
                if (args.Any(value => value.Equals("--preview-small", StringComparison.OrdinalIgnoreCase)))
                    form.Size = form.MinimumSize;
                form.Show();
                Application.DoEvents();
                var pageArgument = Array.FindIndex(args, value => value.Equals("--preview-page", StringComparison.OrdinalIgnoreCase));
                if (pageArgument >= 0 && pageArgument + 1 < args.Length)
                {
                    var requested = args[pageArgument + 1];
                    var tab = FindControl(form, control => Equals(control.Tag, "workflow-tab") && control.Text.Contains(requested, StringComparison.OrdinalIgnoreCase)) as Button;
                    tab?.PerformClick();
                    Application.DoEvents();
                }
                if (args.Any(value => value.Equals("--preview-scroll-bottom", StringComparison.OrdinalIgnoreCase)))
                {
                    var scroll = FindControl(form, control => control is Panel panel && panel.AutoScroll && panel.Visible) as Panel;
                    if (scroll is not null) scroll.AutoScrollPosition = new Point(0, scroll.VerticalScroll.Maximum);
                    Application.DoEvents();
                }
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                preview.Save(Path.GetFullPath(args[renderArgument + 1]), ImageFormat.Png);
                form.Close();
                return;
            }
            Application.Run(new HubForm(root));
        }
        catch (Exception error)
        {
            var log = Path.Combine(root, "qpro-hub-crash.txt");
            File.WriteAllText(log, error.ToString());
            MessageBox.Show(error.Message + "\n\nDetails: " + log, "QproFaceTracking Hub could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Control? FindControl(Control root, Func<Control, bool> predicate)
    {
        if (predicate(root)) return root;
        foreach (Control child in root.Controls)
        {
            var match = FindControl(child, predicate);
            if (match is not null) return match;
        }
        return null;
    }

    private static void WriteSelfTest(string root, string outputPath)
    {
        var eyeProfiles = Directory.Exists(Path.Combine(root, "calibration"))
            ? Directory.GetFiles(Path.Combine(root, "calibration"), "qpro-independent-visual-axis-v*.json").Select(Path.GetFileName).Order().ToArray()
            : [];
        var tonguePairs = Directory.Exists(Path.Combine(root, "models"))
            ? Directory.GetFiles(Path.Combine(root, "models"), "qpro-stereo-tongue-v*-gate.pt")
                .Select(path => Path.GetFileName(path)!.Replace("-gate.pt", "", StringComparison.OrdinalIgnoreCase))
                .Where(prefix => File.Exists(Path.Combine(root, "models", prefix + "-direction.pt")))
                .Order().ToArray()
            : [];
        var requiredFiles = new[]
        {
            "build-and-run.ps1", "native-eye-local-branch-test.ps1", "install-vrcft-eye-bridge.ps1",
            "platform-tools\\adb.exe", "platform-tools\\AdbWinApi.dll", "platform-tools\\AdbWinUsbApi.dll",
            "python-runtime\\python-3.12.10-amd64.exe", "python-runtime\\LICENSE.txt", "python-runtime\\README.txt",
            "SFX\\succeed.wav", "SFX\\trainingComplete.wav", "SFX\\warning.wav",
            "calibration_inspect.py",
            "libquestpro-camera-streamer-v8.so", "questpro-camera-relay-v8", "questpro-camera-injector",
            "vd-label-bridge\\bin\\Release\\net10.0\\Qpro.VirtualDesktopLabelBridge.exe",
            "vrcft-gaze-bridge\\bin\\Release\\net10.0\\Qpro.GazeBridge.dll"
        };
        var result = new
        {
            ok = requiredFiles.All(path => File.Exists(Path.Combine(root, path))) && eyeProfiles.Length > 0 && tonguePairs.Length > 0,
            root,
            releaseMode = File.Exists(Path.Combine(root, "release-manifest.json")),
            eyeProfiles,
            tonguePairs,
            missingFiles = requiredFiles.Where(path => !File.Exists(Path.Combine(root, path))).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record FileChoice(string Label, string Primary, string? Secondary = null)
{
    public override string ToString() => Label;
}

internal sealed record DatasetInfo(string SessionPath, string CapturePath, string DisplayName, int SampleCount, bool Completed);

internal sealed record DatasetChoice(DatasetInfo Dataset)
{
    public override string ToString() => $"{Dataset.DisplayName} · {Dataset.SampleCount} stills";
}

internal sealed class HubForm : Form
{
    private readonly string _root;
    private readonly string _stopFile;
    private readonly CheckBox _gaze = FeatureToggle("Independent eye gaze + convergence", true);
    private readonly CheckBox _tongue = FeatureToggle("Experimental tongue tracking", false);
    private readonly ComboBox _eyeProfiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 390 };
    private readonly ComboBox _tongueModels = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 390 };
    private readonly ComboBox _quickDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _fullDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly Label _quickQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label _fullQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly DarkProgressBar _trainingProgress = new() { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(4, 5, 4, 2) };
    private readonly Label _trainingProgressStatus = new() { Text = "Training idle — select a recorded dataset when ready.", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 2, 4, 3) };
    private readonly TableLayoutPanel _trainingProgressContainer = new() { Visible = false };
    private readonly ListBox _modelList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly Label _tongueModelNote = new() { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Color.FromArgb(207, 190, 190), Margin = new Padding(24, 2, 0, 4) };
    private readonly ComboBox _fps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 76 };
    private readonly DarkSlider _smoothing = new() { Minimum = 0, Maximum = 100, Value = 55, Width = 180, Height = 30 };
    private readonly ComboBox _visibilityMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265 };
    private readonly Label _usbStatus = StatusLabel();
    private readonly Label _steamStatus = StatusLabel();
    private readonly Label _vrcftStatus = StatusLabel();
    private readonly Label _bridgeStatus = StatusLabel();
    private readonly Label _runtimeStatus = StatusLabel();
    private readonly Label _gazeStatus = StatusLabel();
    private readonly Label _setupRuntimeStatus = SetupStatusLabel();
    private readonly Label _setupBridgeStatus = SetupStatusLabel();
    private readonly Label _setupGazeStatus = SetupStatusLabel();
    private readonly DarkButton _setupRuntimeButton = SetupButton("Install runtime");
    private readonly DarkButton _setupBridgeButton = SetupButton("Install bridge");
    private readonly DarkButton _setupGazeButton = SetupButton("Prepare gaze");
    private readonly DarkProgressBar _setupProgress = new() { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(4, 5, 4, 2) };
    private readonly Label _setupProgressStatus = new() { Text = "Setup idle.", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 2, 4, 3) };
    private readonly TableLayoutPanel _setupProgressContainer = new() { Visible = false };
    private readonly Label _runStatus = new() { Text = "● Idle — stock tracking is untouched", AutoSize = true, ForeColor = Color.FromArgb(103, 218, 132) };
    private readonly RichTextBox _log = new() { ReadOnly = true, BackColor = Color.FromArgb(25, 3, 3), ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical, HideSelection = false };
    private readonly Button _start = PrimaryButton("Apply and start selected");
    private readonly Button _stop = SecondaryButton("Stop and restore stock");
    private readonly List<Process> _trackingProcesses = [];
    private SoundPlayer? _soundPlayer;
    private bool _stopping;
    private bool _setupPulseOn;
    private bool _adjacentModelsImported;
    private int _trainingStage;
    private int _trainingStageCount = 2;

    internal static readonly Color Background = Color.FromArgb(34, 0, 0);       // #220000
    internal static readonly Color Panel = Color.FromArgb(48, 8, 8);
    internal static readonly Color Raised = Color.FromArgb(66, 15, 16);
    internal static readonly Color RaisedHover = Color.FromArgb(83, 20, 21);
    internal static readonly Color Muted = Color.FromArgb(218, 199, 199);
    internal static readonly Color Accent = Color.FromArgb(238, 46, 49);         // #ee2e31
    internal static readonly Color Border = Color.FromArgb(126, 36, 38);
    internal static readonly Color Good = Color.FromArgb(103, 218, 132);
    internal static readonly Color Warning = Color.FromArgb(244, 197, 82);
    internal static readonly Color Bad = Color.FromArgb(244, 101, 96);
    private static readonly string UiFontName = FontFamily.Families.Any(font => font.Name.Equals("Lexend", StringComparison.OrdinalIgnoreCase)) ? "Lexend" : "Segoe UI";

    public HubForm(string root)
    {
        _root = root;
        _stopFile = Path.Combine(root, ".qpro-hub-stop");
        Text = "QproFaceTracking Hub";
        MinimumSize = new Size(1000, 800);
        Size = new Size(1140, 950);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Background;
        ForeColor = Color.WhiteSmoke;
        Font = new Font(UiFontName, 10F);
        StartPosition = FormStartPosition.CenterScreen;
        HandleCreated += (_, _) => EnableDarkTitleBar(Handle);

        Controls.Add(BuildLayout());
        _start.Click += async (_, _) => await StartTrackingAsync();
        _stop.Click += async (_, _) => await StopTrackingAsync();
        _gaze.CheckedChanged += (_, _) => UpdateControlState();
        _tongue.CheckedChanged += (_, _) => UpdateControlState();
        _tongueModels.SelectedIndexChanged += (_, _) => UpdateTongueModelNote();
        _fps.Items.AddRange(["12", "15", "18", "20", "24", "30", "36", "48", "60", "72"]);
        _fps.SelectedItem = "24";
        _visibilityMode.Items.AddRange(["Weighted camera + native", "Camera only", "Native only", "Conservative agreement"]);
        _visibilityMode.SelectedIndex = 0;
        ConfigureDropDown(_eyeProfiles);
        ConfigureDropDown(_tongueModels);
        ConfigureDropDown(_fps);
        ConfigureDropDown(_visibilityMode);
        ConfigureDropDown(_quickDatasets);
        ConfigureDropDown(_fullDatasets);
        ConfigureModelList(_modelList);
        UpdateToggleStyle(_gaze);
        UpdateToggleStyle(_tongue);
        _gaze.CheckedChanged += (_, _) => UpdateToggleStyle(_gaze);
        _tongue.CheckedChanged += (_, _) => UpdateToggleStyle(_tongue);
        FormClosing += OnClosing;

        ReloadProfiles();
        _ = RefreshStatusAsync();
        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += async (_, _) => await RefreshStatusAsync();
        timer.Start();
        var pulseTimer = new System.Windows.Forms.Timer { Interval = 550 };
        pulseTimer.Tick += (_, _) => { _setupPulseOn = !_setupPulseOn; UpdateSetupStepStyles(); };
        pulseTimer.Start();
        var setupProgressTimer = new System.Windows.Forms.Timer { Interval = 45 };
        setupProgressTimer.Tick += (_, _) => { if (_setupProgress.IsIndeterminate) _setupProgress.AdvanceAnimation(); };
        setupProgressTimer.Start();
        AppendLog("Hub ready. Nothing is applied until you press Apply and start selected.");
    }

    private Control BuildLayout()
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background };
        var page = new TableLayoutPanel { Dock = DockStyle.Top, Height = 1210, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 565));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label { Text = "QproFaceTracking · Proof of Concept", AutoSize = true, Font = new Font(UiFontName, 22F, FontStyle.Bold), ForeColor = Color.White };
        var subtitle = new Label { Text = "USB or Wi-Fi control hub · stock Virtual Desktop face, brow, jaw, and blink tracking stays intact", AutoSize = true, ForeColor = Muted, Margin = new Padding(2, 4, 0, 18) };
        var heading = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top };
        heading.Controls.Add(title); heading.Controls.Add(subtitle);
        page.Controls.Add(heading, 0, 0);

        var statuses = Card();
        statuses.ColumnCount = 6;
        statuses.RowCount = 2;
        foreach (var label in new[] { "Quest ADB", "SteamVR", "VRCFaceTracking", "Combined bridge", "PC runtime", "Gaze support" })
            statuses.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Muted, Margin = new Padding(8, 5, 25, 2) });
        foreach (var label in new[] { _usbStatus, _steamStatus, _vrcftStatus, _bridgeStatus, _runtimeStatus, _gazeStatus })
            statuses.Controls.Add(label);
        page.Controls.Add(statuses, 0, 1);

        var tracking = Card();
        tracking.ColumnCount = 3;
        tracking.RowCount = 8;
        tracking.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tracking.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tracking.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tracking.Controls.Add(SectionTitle("Live tracking"), 0, 0);
        tracking.SetColumnSpan(tracking.GetControlFromPosition(0, 0)!, 3);
        tracking.Controls.Add(_gaze, 0, 1); tracking.SetColumnSpan(_gaze, 3);
        tracking.Controls.Add(new Label { Text = "Eye profile", AutoSize = true, ForeColor = Muted, Margin = new Padding(24, 8, 12, 4) }, 0, 2);
        tracking.Controls.Add(_eyeProfiles, 1, 2);
        tracking.Controls.Add(_tongue, 0, 3); tracking.SetColumnSpan(_tongue, 3);
        tracking.Controls.Add(new Label { Text = "Tongue model", AutoSize = true, ForeColor = Muted, Margin = new Padding(24, 8, 12, 4) }, 0, 4);
        tracking.Controls.Add(_tongueModels, 1, 4);
        var fpsPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        fpsPanel.Controls.Add(new Label { Text = "Camera FPS cap", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 8, 8, 0) });
        fpsPanel.Controls.Add(_fps);
        tracking.Controls.Add(fpsPanel, 2, 4);
        tracking.Controls.Add(_tongueModelNote, 0, 5); tracking.SetColumnSpan(_tongueModelNote, 3);
        var tuning = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(24, 5, 0, 0) };
        tuning.Controls.Add(new Label { Text = "Motion smoothing  Responsive", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 7, 5, 0) });
        tuning.Controls.Add(_smoothing);
        tuning.Controls.Add(new Label { Text = "Smooth", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 7, 5, 0) });
        tuning.Controls.Add(new Label { Text = "Visibility", AutoSize = true, ForeColor = Muted, Margin = new Padding(12, 7, 5, 0) });
        tuning.Controls.Add(_visibilityMode);
        tracking.Controls.Add(tuning, 0, 6); tracking.SetColumnSpan(tuning, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
        actions.Controls.Add(_start); actions.Controls.Add(_stop);
        actions.Controls.Add(ActionButton("Refresh", (_, _) => { ReloadProfiles(); _ = RefreshStatusAsync(); }));
        tracking.Controls.Add(actions, 0, 7); tracking.SetColumnSpan(actions, 2);
        tracking.Controls.Add(_runStatus, 2, 7);
        page.Controls.Add(tracking, 0, 2);

        var body = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 420, BackColor = Background, Margin = new Padding(0, 12, 0, 0) };
        body.SizeChanged += (_, _) =>
        {
            if (body.ClientSize.Width > 600)
                body.SplitterDistance = (int)(body.ClientSize.Width * 0.68);
        };
        var workflow = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Background };
        workflow.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        workflow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var workflowNav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Panel, Padding = new Padding(2) };
        workflowNav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        workflowNav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        workflowNav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        var setupTab = NavigationButton("First-time setup");
        var personalizationTab = NavigationButton("Tongue personalization");
        var modelsTab = NavigationButton("Tongue model manager");
        workflowNav.Controls.Add(setupTab, 0, 0);
        workflowNav.Controls.Add(personalizationTab, 1, 0);
        workflowNav.Controls.Add(modelsTab, 2, 0);
        var workflowContent = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(0, 6, 0, 0) };
        var setupPage = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(4), AutoScroll = true };
        var personalizationPage = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(4), Visible = false, AutoScroll = true };
        var modelsPage = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(4), Visible = false };
        void SelectWorkflow(int selected)
        {
            setupPage.Visible = selected == 0;
            personalizationPage.Visible = selected == 1;
            modelsPage.Visible = selected == 2;
            StyleNavigationButton(setupTab, selected == 0);
            StyleNavigationButton(personalizationTab, selected == 1);
            StyleNavigationButton(modelsTab, selected == 2);
            if (selected == 0) setupPage.BringToFront();
            else if (selected == 1) personalizationPage.BringToFront();
            else modelsPage.BringToFront();
        }
        setupTab.Click += (_, _) => SelectWorkflow(0);
        personalizationTab.Click += (_, _) => SelectWorkflow(1);
        modelsTab.Click += (_, _) => SelectWorkflow(2);
        workflowContent.Controls.Add(setupPage);
        workflowContent.Controls.Add(personalizationPage);
        workflowContent.Controls.Add(modelsPage);
        workflow.Controls.Add(workflowNav, 0, 0);
        workflow.Controls.Add(workflowContent, 0, 1);
        SelectWorkflow(0);

        var firstRun = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 4, BackColor = Panel, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 10) };
        firstRun.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        firstRun.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        firstRun.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        firstRun.RowStyles.Add(new RowStyle(SizeType.Absolute, 330));
        firstRun.Controls.Add(SectionTitle("Required setup checklist"), 0, 0);
        firstRun.Controls.Add(Info("Complete these once from left to right. The next required step pulses; completed steps stay green. Close VRCFaceTracking for step 2 and restart it afterward."), 0, 1);
        _setupProgressContainer.Dock = DockStyle.Top;
        _setupProgressContainer.AutoSize = true;
        _setupProgressContainer.ColumnCount = 1;
        _setupProgressContainer.BackColor = Color.FromArgb(39, 5, 5);
        _setupProgressContainer.Padding = new Padding(8);
        _setupProgressContainer.Margin = new Padding(5, 4, 5, 10);
        _setupProgressContainer.Controls.Add(new Label { Text = "First-time setup progress", AutoSize = true, ForeColor = Color.White, Font = new Font(UiFontName, 9.5F, FontStyle.Bold), Margin = new Padding(4, 0, 4, 1) });
        _setupProgressContainer.Controls.Add(_setupProgressStatus);
        _setupProgressContainer.Controls.Add(_setupProgress);
        firstRun.Controls.Add(_setupProgressContainer, 0, 2);
        var setupActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        for (var column = 0; column < 3; column++) setupActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        _setupRuntimeButton.Click += async (_, _) => await RunSetupStepAsync("PC runtime setup", "setup-runtime.ps1", "PC runtime is ready.", "Next: close VRCFaceTracking and install the combined bridge.");
        _setupBridgeButton.Click += async (_, _) => await RunSetupStepAsync("Install bridge", "install-vrcft-eye-bridge.ps1", "The combined VRCFaceTracking bridge is installed.", "Restart VRCFaceTracking, then prepare gaze from the headset.");
        _setupGazeButton.Click += async (_, _) => await PrepareGazeAsync();
        setupActions.Controls.Add(SetupStepCard("1", "PC runtime", "Includes private Python and CPU/GPU libraries. No system Python is needed.", _setupRuntimeStatus, _setupRuntimeButton), 0, 0);
        setupActions.Controls.Add(SetupStepCard("2", "VRCFT bridge", "Adds the combined VRCFT module. Stock face and blink tracking stay intact.", _setupBridgeStatus, _setupBridgeButton), 1, 0);
        setupActions.Controls.Add(SetupStepCard("3", "Independent gaze", "Creates the gaze patch from your rooted Quest Pro. No stock model is distributed.", _setupGazeStatus, _setupGazeButton), 2, 0);
        firstRun.Controls.Add(setupActions, 0, 3);
        setupPage.Controls.Add(firstRun);

        var personalization = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Panel, Padding = new Padding(14) };
        personalization.Controls.Add(SectionTitle("Choose a personalization path"));
        personalization.Controls.Add(Info("The bundled developer model is immediately testable. Personalization improves fit for your mouth, headset position, clothing, and expressions."));
        _trainingProgressContainer.Dock = DockStyle.Top;
        _trainingProgressContainer.AutoSize = true;
        _trainingProgressContainer.ColumnCount = 1;
        _trainingProgressContainer.BackColor = Color.FromArgb(39, 5, 5);
        _trainingProgressContainer.Padding = new Padding(8);
        _trainingProgressContainer.Margin = new Padding(5, 0, 5, 10);
        _trainingProgressContainer.Controls.Add(new Label { Text = "Training progress", AutoSize = true, ForeColor = Color.White, Font = new Font(UiFontName, 9.5F, FontStyle.Bold), Margin = new Padding(4, 0, 4, 1) });
        _trainingProgressContainer.Controls.Add(_trainingProgressStatus);
        _trainingProgressContainer.Controls.Add(_trainingProgress);
        personalization.Controls.Add(_trainingProgressContainer);
        var choices = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 330, ColumnCount = 2, RowCount = 1 };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.Controls.Add(WorkflowCard(
            "Quick refinement · 10–20 min",
            "Fastest. Corrects common false positives and direction gaps, but inherits some developer-model bias.",
            _quickDatasets,
            _quickQueueStatus,
            ActionButton("1. Record refinement", async (_, _) => await ConfirmCaptureAsync(true)),
            ActionButton("2. Train personalized copy", async (_, _) => await TrainTongueAsync(true))
        ), 0, 0);
        choices.Controls.Add(WorkflowCard(
            "Full dataset · 45–90 min",
            "Best individual coverage and independence from v8. Requires much more careful capture time.",
            _fullDatasets,
            _fullQueueStatus,
            ActionButton("1. Record full dataset", async (_, _) => await ConfirmCaptureAsync(false)),
            ActionButton("2. Train new personal model", async (_, _) => await TrainTongueAsync(false))
        ), 1, 0);
        personalization.Controls.Add(choices);
        personalizationPage.Controls.Add(personalization);

        var manager = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Panel, Padding = new Padding(14) };
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        manager.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        manager.Controls.Add(SectionTitle("Tongue model manager"), 0, 0);
        manager.Controls.Add(Info("Friendly names do not alter the paired model files. Export creates one portable .qptonguemodel package; import assigns a safe unused local version."), 0, 1);
        manager.Controls.Add(_modelList, 0, 2);
        var modelActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        modelActions.Controls.Add(ActionButton("Rename", (_, _) => RenameSelectedModel()));
        modelActions.Controls.Add(ActionButton("Export", (_, _) => ExportSelectedModel()));
        modelActions.Controls.Add(ActionButton("Import", (_, _) => ImportModel()));
        modelActions.Controls.Add(ActionButton("Delete", (_, _) => DeleteSelectedModel()));
        modelActions.Controls.Add(ActionButton("Refresh", (_, _) => ReloadProfiles()));
        manager.Controls.Add(modelActions, 0, 3);
        modelsPage.Controls.Add(manager);
        body.Panel1.Controls.Add(workflow);

        var logCard = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(14) };
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logLayout.Controls.Add(SectionTitle("Activity"), 0, 0); logLayout.Controls.Add(_log, 0, 1);
        logCard.Controls.Add(logLayout); body.Panel2.Controls.Add(logCard);
        page.Controls.Add(body, 0, 3);

        page.Controls.Add(new Label { Text = "Experimental research software. Press Stop before disconnecting the headset or closing the hub.", AutoSize = true, ForeColor = Muted, Margin = new Padding(2, 12, 0, 0) }, 0, 4);
        viewport.Controls.Add(page);
        return viewport;
    }

    private void ReloadProfiles()
    {
        ImportAdjacentPersonalModels();
        var selectedEye = (_eyeProfiles.SelectedItem as FileChoice)?.Primary;
        var selectedTongue = (_tongueModels.SelectedItem as FileChoice)?.Primary;
        var selectedManaged = (_modelList.SelectedItem as FileChoice)?.Primary;
        _eyeProfiles.Items.Clear();
        var calibrationDir = Path.Combine(_root, "calibration");
        if (Directory.Exists(calibrationDir))
        {
            foreach (var path in Directory.GetFiles(calibrationDir, "qpro-independent-visual-axis-v*.json")
                         .OrderByDescending(VersionFromPath))
            {
                var version = VersionFromPath(path);
                _eyeProfiles.Items.Add(new FileChoice(version == 2 ? "Developer mapping v2 (current)" : $"Visual-axis mapping v{version}", path));
            }
        }
        SelectOrFirst(_eyeProfiles, selectedEye);

        _tongueModels.Items.Clear();
        _modelList.Items.Clear();
        var modelsDir = Path.Combine(_root, "models");
        if (Directory.Exists(modelsDir))
        {
            foreach (var gate in Directory.GetFiles(modelsDir, "qpro-stereo-tongue-v*-gate.pt").OrderByDescending(VersionFromPath))
            {
                var version = VersionFromPath(gate);
                var direction = Path.Combine(modelsDir, $"qpro-stereo-tongue-v{version}-direction.pt");
                if (File.Exists(direction))
                {
                    var choice = new FileChoice(ModelDisplayName(version), gate, direction);
                    _tongueModels.Items.Add(choice);
                    _modelList.Items.Add(choice);
                }
            }
        }
        SelectOrFirst(_tongueModels, selectedTongue);
        SelectOrFirst(_modelList, selectedManaged);
        UpdateTongueModelNote();
        ReloadDatasetQueues();
        UpdateControlState();
    }

    private void ImportAdjacentPersonalModels()
    {
        if (_adjacentModelsImported) return;
        _adjacentModelsImported = true;

        var releaseParent = Directory.GetParent(_root);
        if (releaseParent is null || !releaseParent.Name.Equals("dist", StringComparison.OrdinalIgnoreCase)) return;

        var destination = Path.Combine(_root, "models");
        Directory.CreateDirectory(destination);
        var imported = new List<int>();
        foreach (var sibling in releaseParent.GetDirectories("QproFaceTracking-*").OrderByDescending(directory => directory.LastWriteTimeUtc))
        {
            if (string.Equals(sibling.FullName.TrimEnd(Path.DirectorySeparatorChar), _root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
            var source = Path.Combine(sibling.FullName, "models");
            if (!Directory.Exists(source)) continue;
            foreach (var gate in Directory.GetFiles(source, "qpro-stereo-tongue-v*-gate.pt").OrderByDescending(VersionFromPath))
            {
                var version = VersionFromPath(gate);
                if (version <= 0 || version == 8) continue;
                var sourceDirection = Path.Combine(source, $"qpro-stereo-tongue-v{version}-direction.pt");
                var destinationGate = Path.Combine(destination, Path.GetFileName(gate));
                var destinationDirection = Path.Combine(destination, Path.GetFileName(sourceDirection));
                if (!File.Exists(sourceDirection) || File.Exists(destinationGate) || File.Exists(destinationDirection)) continue;

                File.Copy(gate, destinationGate, false);
                File.Copy(sourceDirection, destinationDirection, false);
                foreach (var companion in Directory.GetFiles(source, $"qpro-stereo-tongue-v{version}.*")
                             .Concat(Directory.GetFiles(source, $"qpro-stereo-tongue-v{version}-*.torchscript.pt")))
                {
                    var target = Path.Combine(destination, Path.GetFileName(companion));
                    if (!File.Exists(target)) File.Copy(companion, target, false);
                }
                imported.Add(version);
            }
        }
        if (imported.Count > 0)
            AppendLog($"Carried personal tongue model{(imported.Count == 1 ? string.Empty : "s")} v{string.Join(", v", imported.Distinct().Order())} forward from the previous release.");
    }

    private void UpdateTongueModelNote()
    {
        var model = _tongueModels.SelectedItem as FileChoice;
        var version = model is null ? 0 : VersionFromPath(model.Primary);
        _tongueModelNote.Text = version == 8
            ? "Trained only on the developer. It is suitable for a first demo; quick refinement is recommended for another wearer."
            : model is null
                ? "No complete gate/direction model pair was found."
                : "Personal model discovered in this release folder. The bundled developer v8 remains unchanged.";
    }

    private async Task ConfirmCaptureAsync(bool quick)
    {
        var missing = new List<string>();
        if (FindAdb() is null) missing.Add("re-extract the release; bundled platform-tools\\adb.exe is missing");
        else if (!await HasQuestAsync()) missing.Add("connect and authorize the rooted Quest Pro over USB or wireless ADB");
        if (!Process.GetProcessesByName("vrserver").Any()) missing.Add("start SteamVR");
        if (!Process.GetProcessesByName("VRCFaceTracking").Any()) missing.Add("start VRCFaceTracking and confirm Virtual Desktop face tracking is flowing");
        if (!BackendReady()) missing.Add("run First-time setup: Set up PC runtime");
        if (missing.Count > 0)
        {
            MessageBox.Show(
                this,
                "Before recording:\n\n• " + string.Join("\n• ", missing) +
                "\n\nThe current trainer uses Virtual Desktop's native TongueOut confidence as a reference label, so SteamVR and VRCFaceTracking are required during capture.",
                "Capture is not ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        var title = quick ? "Start quick tongue refinement?" : "Start full tongue capture?";
        var estimate = quick ? "about 10–20 minutes" : "about 45–90 minutes";
        var purpose = quick
            ? "This creates a correction dataset that fine-tunes a new copy of the developer model. It is faster, but cannot replace the breadth of a full personal dataset."
            : "This records a much broader personal dataset and is the best-quality option, but it requires many carefully held poses.";
        var choice = MessageBox.Show(
            this,
            $"Estimated capture time: {estimate}.\n\n{purpose}\n\nA guided camera window will open. Press Q at any point to stop safely. Existing captures and models will not be overwritten.\n\nStart now?",
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes) return;
        var started = DateTime.UtcNow;
        var succeeded = await RunUtilityAsync(
            quick ? "Quick refinement capture" : "Full tongue capture",
            "build-and-run.ps1",
            quick ? "-TongueRefinementCalibration" : "-TongueStillCalibration");
        if (!succeeded) return;
        var dataset = FindLatestDataset(quick, requireCompleted: false, newerThan: started.AddSeconds(-3));
        if (dataset is null || dataset.SampleCount == 0) return;
        var proposed = dataset.DisplayName.StartsWith("Dataset ", StringComparison.Ordinal)
            ? (quick ? "My tongue refinement" : "My full tongue dataset")
            : dataset.DisplayName;
        var name = PromptForText(
            "Name this dataset",
            "Give this capture a friendly name so you can identify the model trained from it later.",
            proposed);
        if (name is not null)
        {
            SetDatasetDisplayName(dataset.SessionPath, name);
            AppendLog($"Dataset saved as “{name}”.");
        }
        ReloadDatasetQueues();
    }

    private async Task RunSetupStepAsync(string label, string script, string completed, string next)
    {
        BeginSetupProgress(label);
        var succeeded = await RunUtilityAsync(label, script);
        FinishSetupProgress(succeeded, label);
        if (!succeeded) return;
        UpdateSetupStepStyles();
        PlaySfx("succeed.wav");
        MessageBox.Show(this, completed + "\n\n" + next, "Setup step complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _setupProgressContainer.Visible = false;
    }

    private async Task PrepareGazeAsync()
    {
        var adb = FindAdb();
        if (adb is null)
        {
            PlaySfx("warning.wav");
            MessageBox.Show(
                this,
                "The bundled Android tools could not be found.\n\nRe-extract the complete QproFaceTracking release and confirm that platform-tools\\adb.exe is present, then try Prepare gaze again.",
                "Android tools are missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var connection = await RunAdbProbeAsync(adb, ["devices"]);
        var deviceLines = connection.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var connected = await HasQuestAsync();
        if (!connection.Completed || !connected)
        {
            var stateHint = deviceLines.Any(line => line.Contains("\tunauthorized", StringComparison.OrdinalIgnoreCase))
                ? "The headset is listed as unauthorized. Put it on and accept the debugging authorization prompt."
                : deviceLines.Any(line => line.Contains("\toffline", StringComparison.OrdinalIgnoreCase))
                    ? "The headset is listed as offline. Reconnect wireless ADB or the USB cable and try again."
                    : "No authorized headset was found over ADB.";
            PlaySfx("warning.wav");
            MessageBox.Show(
                this,
                stateHint + "\n\nFor wireless use, run Connect-QproWireless.cmd and then Launch-QproWireless.cmd. Confirm the headset is awake and Magisk Shell access is granted. USB is also supported. Then press Prepare gaze again.",
                "Quest Pro not found over ADB",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var target = GetConfiguredAdbTarget();
        var rootArguments = string.IsNullOrWhiteSpace(target)
            ? new[] { "shell", "su", "-c", "id" }
            : new[] { "-s", target, "shell", "su", "-c", "id" };
        var root = await RunAdbProbeAsync(adb, rootArguments, 8);
        if (!root.Completed || root.ExitCode != 0 || !root.Output.Contains("uid=0", StringComparison.OrdinalIgnoreCase))
        {
            PlaySfx("warning.wav");
            MessageBox.Show(
                this,
                "ADB can see your Quest Pro, but root access was not granted.\n\nIndependent gaze requires a rooted headset. Confirm that the headset is rooted, then open Magisk and grant Superuser access to Shell / ADB Shell (com.android.shell). Keep the headset awake and try Prepare gaze again.",
                "Quest Pro root access is unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        await RunSetupStepAsync(
            "Local gaze preparation",
            "prepare-eye-model.ps1",
            "Independent-gaze support is prepared.",
            "First-time setup is complete. Start Virtual Desktop, SteamVR, and VRCFaceTracking before applying tracking.");
    }

    private void BeginSetupProgress(string label)
    {
        _setupProgressContainer.Visible = true;
        _setupProgress.IsIndeterminate = true;
        _setupProgress.Value = 0;
        _setupProgressStatus.Text = label.Equals("PC runtime setup", StringComparison.OrdinalIgnoreCase)
            ? "Installing and verifying the private PC runtime… This can take several minutes."
            : label + " is running… Please keep this window open.";
        _setupProgressStatus.ForeColor = Warning;
        SetSetupButtonsEnabled(false);
    }

    private void FinishSetupProgress(bool succeeded, string label)
    {
        _setupProgress.IsIndeterminate = false;
        _setupProgress.Value = succeeded ? 100 : 0;
        _setupProgressStatus.Text = succeeded ? label + " completed successfully." : label + " did not complete. See Activity for details.";
        _setupProgressStatus.ForeColor = succeeded ? Good : Bad;
        SetSetupButtonsEnabled(true);
    }

    private void SetSetupButtonsEnabled(bool enabled)
    {
        _setupRuntimeButton.Enabled = enabled;
        _setupBridgeButton.Enabled = enabled;
        _setupGazeButton.Enabled = enabled;
    }

    private async Task TrainTongueAsync(bool quick)
    {
        var queue = quick ? _quickDatasets : _fullDatasets;
        var dataset = (queue.SelectedItem as DatasetChoice)?.Dataset;
        if (dataset is null || dataset.SampleCount == 0 || !dataset.Completed)
        {
            MessageBox.Show(
                this,
                "You did not capture any completed data yet! Capture first to train.",
                "No training data",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        BeginTrainingProgress(quick);
        var versionsBefore = TongueModelVersions().ToHashSet();
        var succeeded = await RunUtilityAsync(
            quick ? "Personal refinement training" : "Full tongue training",
            quick ? "train-latest-tongue-refinement.ps1" : "train-latest-tongue-stills.ps1",
            "-SessionPath", dataset.SessionPath);
        if (!succeeded) { FinishTrainingProgress(false); return; }
        var created = TongueModelVersions().Where(version => !versionsBefore.Contains(version)).OrderDescending().FirstOrDefault();
        if (created > 0)
        {
            WriteModelMetadata(created, dataset.DisplayName, dataset.SessionPath, quick ? "quick refinement" : "full personal dataset");
            AppendLog($"Model v{created} named “{dataset.DisplayName}”.");
        }
        ReloadProfiles();
        FinishTrainingProgress(created > 0);
        if (created > 0)
        {
            PlaySfx("trainingComplete.wav");
            MessageBox.Show(this, $"Training is complete. “{dataset.DisplayName}” is now available as tongue model v{created}.", "Tongue model ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task StartTrackingAsync()
    {
        _trackingProcesses.RemoveAll(p => p.HasExited);
        if (_trackingProcesses.Any(p => !p.HasExited)) { MessageBox.Show(this, "Tracking is already running."); return; }
        if (!_gaze.Checked && !_tongue.Checked) { PlaySfx("warning.wav"); MessageBox.Show(this, "Select at least one tracking feature."); return; }
        var missing = new List<string>();
        if (FindAdb() is null) missing.Add("the bundled Android tools — re-extract the complete release");
        else if (!await HasQuestAsync()) missing.Add("an authorized Quest over USB or wireless ADB (run Connect-QproWireless.cmd, then Launch-QproWireless.cmd)");
        if (!Process.GetProcessesByName("vrserver").Any()) missing.Add("SteamVR");
        if (!Process.GetProcessesByName("VRCFaceTracking").Any()) missing.Add("VRCFaceTracking");
        if (!BridgeInstalled()) missing.Add("the combined Qpro VRCFT bridge — use First-time setup step 2");
        if (!BackendReady()) missing.Add("the PC runtime — use First-time setup step 1");
        if (_gaze.Checked && !EyeModelReady()) missing.Add("the locally prepared gaze patch — use First-time setup step 3: Prepare independent gaze");
        if (_gaze.Checked && _eyeProfiles.SelectedItem is null) missing.Add("an eye profile");
        if (_tongue.Checked && _tongueModels.SelectedItem is null) missing.Add("a paired tongue model");
        if (missing.Count > 0) { PlaySfx("warning.wav"); MessageBox.Show(this, "Before applying tracking, start or provide:\n\n• " + string.Join("\n• ", missing), "Not ready"); return; }

        File.Delete(_stopFile);
        _start.Enabled = false; _stop.Enabled = true;
        _runStatus.Text = "● Starting…"; _runStatus.ForeColor = Warning;
        try
        {
            if (_gaze.Checked)
            {
                var eye = (FileChoice)_eyeProfiles.SelectedItem!;
                StartManaged("Independent gaze", "native-eye-local-branch-test.ps1", "-RuntimePreview", "-VrcftOutput", "-CalibrationOutput", eye.Primary, "-StopFile", _stopFile);
                AppendLog("Waiting for Meta trackingservice to return before starting cameras…");
                await Task.Delay(7000);
                if (_trackingProcesses.Any(p => p.HasExited)) throw new InvalidOperationException("The independent-gaze process exited during startup. See Activity.");
            }
            if (_tongue.Checked)
            {
                var model = (FileChoice)_tongueModels.SelectedItem!;
                StartManaged("Tongue tracking", "build-and-run.ps1", "-TonguePreview", "-EnableTongueOutput", "-MaxFps", (_fps.SelectedItem?.ToString() ?? "24"), "-TongueSmoothing", _smoothing.Value.ToString(), "-TongueVisibilityMode", VisibilityModeValue(), "-TongueModelPath", model.Primary, "-TongueDirectionModelPath", model.Secondary!, "-StopFile", _stopFile);
            }
            _runStatus.Text = "● Selected overrides active"; _runStatus.ForeColor = Good;
            UpdateControlState();
        }
        catch (Exception error)
        {
            AppendLog("START FAILED: " + error.Message);
            await StopTrackingAsync();
            PlaySfx("warning.wav");
            MessageBox.Show(this, error.Message, "Tracking did not start");
        }
    }

    private async Task StopTrackingAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _runStatus.Text = "● Stopping cleanly…"; _runStatus.ForeColor = Warning;
        try
        {
            File.WriteAllText(_stopFile, DateTimeOffset.Now.ToString("O"));
            var deadline = DateTime.UtcNow.AddSeconds(18);
            while (_trackingProcesses.Any(p => !p.HasExited) && DateTime.UtcNow < deadline)
                await Task.Delay(250);
            if (_trackingProcesses.Any(p => !p.HasExited))
                AppendLog("A window is still closing. Press Q in that preview; the hub will not force-kill cleanup.");
            else
            {
                File.Delete(_stopFile);
                AppendLog("All selected overrides stopped; stock tracking restored.");
            }
        }
        finally
        {
            _trackingProcesses.RemoveAll(p => p.HasExited);
            _stopping = false; _start.Enabled = true; _stop.Enabled = false;
            _runStatus.Text = _trackingProcesses.Count == 0 ? "● Idle — stock tracking is untouched" : "● Waiting for preview to close";
            _runStatus.ForeColor = _trackingProcesses.Count == 0 ? Good : Warning;
            UpdateControlState();
        }
    }

    private Process StartManaged(string label, string script, params string[] arguments)
    {
        var start = PowerShellStart(script, arguments, hidden: true);
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog($"[{label}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLog($"[{label}] {e.Data}"); };
        process.Exited += (_, _) => BeginInvoke(() => { AppendLog($"[{label}] exited with code {process.ExitCode}."); UpdateControlState(); });
        if (!process.Start()) throw new InvalidOperationException($"Could not start {label}.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        _trackingProcesses.Add(process);
        AppendLog($"Starting {label}…");
        return process;
    }

    private async Task<bool> RunUtilityAsync(string label, string script, params string[] args)
    {
        if (_trackingProcesses.Any(p => !p.HasExited)) { MessageBox.Show(this, "Stop live tracking before starting this action."); return false; }
        try
        {
            AppendLog($"Starting {label}…");
            var start = PowerShellStart(script, args, hidden: true);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { AppendLog($"[{label}] {e.Data}"); HandleTrainingProgress(label, e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { AppendLog($"[{label}] {e.Data}"); HandleTrainingProgress(label, e.Data); } };
            if (!process.Start()) { AppendLog($"Could not start {label}."); return false; }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            AppendLog($"{label} finished with code {process.ExitCode}.");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{label} failed with code {process.ExitCode}. See Activity for the exact error and suggested fix.");
            ReloadProfiles();
            await RefreshStatusAsync();
            return true;
        }
        catch (Exception error)
        {
            AppendLog($"{label} failed: {error.Message}");
            MessageBox.Show(this, error.Message, label + " failed");
            return false;
        }
    }

    private void BeginTrainingProgress(bool quick)
    {
        _trainingProgressContainer.Visible = true;
        _trainingStage = 0;
        _trainingStageCount = 2;
        _trainingProgress.Value = 1;
        _trainingProgressStatus.Text = $"Preparing the selected {(quick ? "refinement" : "full")} dataset…";
        _trainingProgressStatus.ForeColor = Warning;
    }

    private void HandleTrainingProgress(string label, string line)
    {
        if (!label.Contains("training", StringComparison.OrdinalIgnoreCase)) return;
        if (InvokeRequired) { BeginInvoke(() => HandleTrainingProgress(label, line)); return; }

        if (line.Contains("TRAIN_STATUS phase=preparing", StringComparison.Ordinal))
        {
            _trainingProgress.Value = 2;
            _trainingProgressStatus.Text = "Preparing and validating the selected stereo stills…";
            _trainingProgressStatus.ForeColor = Warning;
            return;
        }
        var deviceMatch = Regex.Match(line, @"TRAIN_DEVICE device=(?<device>\S+) batch=(?<batch>\d+)");
        if (deviceMatch.Success)
        {
            var device = deviceMatch.Groups["device"].Value;
            _trainingProgress.Value = Math.Max(_trainingProgress.Value, 5);
            _trainingProgressStatus.Text = device == "cpu"
                ? "CPU fallback active — training is working but slower. NVIDIA owners can rerun setup step 1 afterward."
                : "NVIDIA CUDA acceleration active — beginning model training…";
            _trainingProgressStatus.ForeColor = device == "cpu" ? Warning : Good;
            return;
        }
        var stageMatch = Regex.Match(line, @"TRAIN_STAGE index=(?<index>\d+) total=(?<total>\d+) name=(?<name>\S+) epochs=(?<epochs>\d+) device=(?<device>\S+)");
        if (stageMatch.Success)
        {
            _trainingStage = int.Parse(stageMatch.Groups["index"].Value);
            _trainingStageCount = Math.Max(1, int.Parse(stageMatch.Groups["total"].Value));
            var name = stageMatch.Groups["name"].Value;
            _trainingProgressStatus.Text = $"Training {name} checkpoint · stage {_trainingStage} of {_trainingStageCount} · 0/{stageMatch.Groups["epochs"].Value} epochs";
            _trainingProgressStatus.ForeColor = stageMatch.Groups["device"].Value == "cpu" ? Warning : Good;
            _trainingProgress.Value = Math.Clamp((int)Math.Round(5 + 94.0 * (_trainingStage - 1) / _trainingStageCount), 1, 99);
            return;
        }
        var epochMatch = Regex.Match(line, @"TRAIN_EPOCH current=(?<current>\d+) total=(?<total>\d+) focus=(?<focus>\S+)");
        if (!epochMatch.Success) return;
        var current = int.Parse(epochMatch.Groups["current"].Value);
        var total = Math.Max(1, int.Parse(epochMatch.Groups["total"].Value));
        var stage = Math.Max(1, _trainingStage);
        var overall = 5 + 94.0 * ((stage - 1) + Math.Clamp((double)current / total, 0, 1)) / Math.Max(1, _trainingStageCount);
        _trainingProgress.Value = Math.Clamp((int)Math.Round(overall), 1, 99);
        _trainingProgressStatus.Text = $"Training {epochMatch.Groups["focus"].Value} checkpoint · stage {stage} of {_trainingStageCount} · {current}/{total} epochs · {_trainingProgress.Value}%";
    }

    private void FinishTrainingProgress(bool succeeded)
    {
        _trainingProgress.Value = succeeded ? 100 : 0;
        _trainingProgressStatus.Text = succeeded
            ? "Training complete — the new model is ready in the model manager."
            : "Training stopped or failed — review Activity for the exact cause.";
        _trainingProgressStatus.ForeColor = succeeded ? Good : Bad;
    }

    private DatasetInfo? FindLatestDataset(bool quick, bool requireCompleted, DateTime? newerThan = null)
    {
        return FindDatasets(quick, requireCompleted, newerThan).FirstOrDefault();
    }

    private IReadOnlyList<DatasetInfo> FindDatasets(bool quick, bool requireCompleted, DateTime? newerThan = null)
    {
        var captureDirectories = new List<string> { Path.Combine(_root, "captures") };
        var rootInfo = new DirectoryInfo(_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (rootInfo.Parent is not null && rootInfo.Parent.Name.Equals("dist", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                captureDirectories.AddRange(
                    Directory.GetDirectories(rootInfo.Parent.FullName, "QproFaceTracking-*")
                        .Select(path => Path.Combine(path, "captures")));
            }
            catch { }
        }
        var sessionPaths = captureDirectories
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => Directory.GetFiles(path, "*.qpsession.json"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (sessionPaths.Count == 0) return [];
        var result = new List<DatasetInfo>();
        var quickTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tongue-stereo-corrections-v1", "tongue-stereo-refinement-v2", "tongue-stereo-arc-v3"
        };
        foreach (var path in sessionPaths)
        {
            if (newerThan is not null && File.GetLastWriteTimeUtc(path) < newerThan.Value) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (node is null) continue;
                var sessionType = node["sessionType"]?.GetValue<string>() ?? string.Empty;
                if (quick != quickTypes.Contains(sessionType)) continue;
                if (!quick && !sessionType.Equals("tongue-stereo-stills-v1", StringComparison.OrdinalIgnoreCase)) continue;
                var completed = node["completed"]?.GetValue<bool>() ?? false;
                if (requireCompleted && !completed) continue;
                var samples = node["samples"] as JsonArray;
                var capture = Regex.Replace(path, "\\.qpsession\\.json$", ".qpcap", RegexOptions.IgnoreCase);
                if (!File.Exists(capture)) continue;
                var fallback = "Dataset " + Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                var displayName = node["displayName"]?.GetValue<string>()?.Trim();
                result.Add(new DatasetInfo(path, capture, string.IsNullOrWhiteSpace(displayName) ? fallback : displayName, samples?.Count ?? 0, completed));
            }
            catch { }
        }
        return result;
    }

    private void ReloadDatasetQueues()
    {
        var quickSelection = (_quickDatasets.SelectedItem as DatasetChoice)?.Dataset.SessionPath;
        var fullSelection = (_fullDatasets.SelectedItem as DatasetChoice)?.Dataset.SessionPath;
        var trainedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = Path.Combine(_root, "models");
        if (Directory.Exists(models))
        {
            foreach (var metadata in Directory.GetFiles(models, "qpro-stereo-tongue-v*.metadata.json"))
            {
                try
                {
                    var session = JsonNode.Parse(File.ReadAllText(metadata))?["datasetSession"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(session)) trainedSessions.Add(session);
                }
                catch { }
            }
        }

        LoadQueue(_quickDatasets, _quickQueueStatus, true, quickSelection, trainedSessions);
        LoadQueue(_fullDatasets, _fullQueueStatus, false, fullSelection, trainedSessions);
    }

    private void LoadQueue(ComboBox box, Label status, bool quick, string? previous, HashSet<string> trainedSessions)
    {
        var all = FindDatasets(quick, requireCompleted: false).ToList();
        var ready = all.Where(dataset => dataset.Completed && dataset.SampleCount > 0 && !trainedSessions.Contains(Path.GetFileName(dataset.SessionPath))).ToList();
        box.Items.Clear();
        foreach (var dataset in ready) box.Items.Add(new DatasetChoice(dataset));
        for (var index = 0; index < box.Items.Count; index++)
        {
            if (string.Equals(((DatasetChoice)box.Items[index]!).Dataset.SessionPath, previous, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                break;
            }
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        var incomplete = all.Count(dataset => !dataset.Completed || dataset.SampleCount == 0);
        status.Text = ready.Count == 0
            ? (incomplete > 0 ? $"Queue empty · {incomplete} incomplete" : "Queue empty — record first")
            : $"{ready.Count} dataset{(ready.Count == 1 ? string.Empty : "s")} ready to train";
        status.ForeColor = ready.Count > 0 ? Good : Muted;
    }

    private static void SetDatasetDisplayName(string sessionPath, string displayName)
    {
        var node = JsonNode.Parse(File.ReadAllText(sessionPath))?.AsObject()
            ?? throw new InvalidDataException("The dataset session metadata is invalid.");
        node["displayName"] = CleanDisplayName(displayName);
        var temporary = sessionPath + ".tmp";
        File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, sessionPath, true);
    }

    private IEnumerable<int> TongueModelVersions()
    {
        var models = Path.Combine(_root, "models");
        if (!Directory.Exists(models)) yield break;
        foreach (var gate in Directory.GetFiles(models, "qpro-stereo-tongue-v*-gate.pt"))
        {
            var version = VersionFromPath(gate);
            if (version > 0 && File.Exists(Path.Combine(models, $"qpro-stereo-tongue-v{version}-direction.pt")))
                yield return version;
        }
    }

    private string ModelDisplayName(int version)
    {
        var metadata = ModelMetadataPath(version);
        if (File.Exists(metadata))
        {
            try
            {
                var name = JsonNode.Parse(File.ReadAllText(metadata))?["displayName"]?.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(name)) return $"{name} · v{version}";
            }
            catch { }
        }
        return version == 8 ? "Developer-trained tongue model · v8 demo" : $"Personal tongue model · v{version}";
    }

    private string ModelMetadataPath(int version) => Path.Combine(_root, "models", $"qpro-stereo-tongue-v{version}.metadata.json");

    private void WriteModelMetadata(int version, string displayName, string? datasetPath, string origin)
    {
        string? existingDataset = null;
        string? existingCreated = null;
        if (File.Exists(ModelMetadataPath(version)))
        {
            try
            {
                var existing = JsonNode.Parse(File.ReadAllText(ModelMetadataPath(version)))?.AsObject();
                existingDataset = existing?["datasetSession"]?.GetValue<string>();
                existingCreated = existing?["createdUtc"]?.GetValue<string>();
            }
            catch { }
        }
        var payload = new JsonObject
        {
            ["format"] = "qpro-tongue-model-metadata-v1",
            ["displayName"] = CleanDisplayName(displayName),
            ["version"] = version,
            ["origin"] = origin,
            ["datasetSession"] = datasetPath is null ? existingDataset : Path.GetFileName(datasetPath),
            ["createdUtc"] = existingCreated ?? DateTimeOffset.UtcNow.ToString("O")
        };
        File.WriteAllText(ModelMetadataPath(version), payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RenameSelectedModel()
    {
        if (_modelList.SelectedItem is not FileChoice model) { MessageBox.Show(this, "Select a tongue model first."); return; }
        var version = VersionFromPath(model.Primary);
        var current = Regex.Replace(model.Label, $@"\s*·\s*v{version}.*$", string.Empty).Trim();
        var name = PromptForText("Rename tongue model", "Choose the friendly name shown in the hub. The model files remain paired and unchanged.", current);
        if (name is null) return;
        WriteModelMetadata(version, name, null, version == 8 ? "bundled developer model" : "renamed local model");
        AppendLog($"Renamed tongue model v{version} to “{name}”.");
        ReloadProfiles();
    }

    private void DeleteSelectedModel()
    {
        if (_modelList.SelectedItem is not FileChoice model) { MessageBox.Show(this, "Select a tongue model first."); return; }
        if (_trackingProcesses.Any(process => !process.HasExited)) { MessageBox.Show(this, "Stop live tracking before deleting a model."); return; }
        var version = VersionFromPath(model.Primary);
        if (version == 8)
        {
            MessageBox.Show(this, "The bundled developer v8 model is protected so the hub always retains a working demo. You can export or rename it, but not delete it.", "Bundled model protected");
            return;
        }
        if (MessageBox.Show(this, $"Delete “{model.Label}” from this installation?\n\nThis removes its paired checkpoints and cannot be undone unless you exported it first.", "Delete tongue model", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var models = Path.Combine(_root, "models");
        foreach (var path in Directory.GetFiles(models, $"qpro-stereo-tongue-v{version}-*")) File.Delete(path);
        if (File.Exists(ModelMetadataPath(version))) File.Delete(ModelMetadataPath(version));
        AppendLog($"Deleted tongue model v{version}.");
        ReloadProfiles();
    }

    private void ExportSelectedModel()
    {
        if (_modelList.SelectedItem is not FileChoice model) { MessageBox.Show(this, "Select a tongue model first."); return; }
        var version = VersionFromPath(model.Primary);
        using var dialog = new SaveFileDialog
        {
            Title = "Export tongue model",
            Filter = "Qpro tongue model (*.qptonguemodel)|*.qptonguemodel",
            FileName = SafeFileName(Regex.Replace(model.Label, @"\s*·\s*v\d+.*$", string.Empty)) + ".qptonguemodel",
            AddExtension = true,
            DefaultExt = "qptonguemodel"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
        using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
        var manifest = new JsonObject
        {
            ["format"] = "qpro-tongue-model-package-v1",
            ["displayName"] = Regex.Replace(model.Label, @"\s*·\s*v\d+.*$", string.Empty).Trim(),
            ["sourceVersion"] = version,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), Encoding.UTF8)) writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        archive.CreateEntryFromFile(model.Primary, "gate.pt", CompressionLevel.Optimal);
        archive.CreateEntryFromFile(model.Secondary!, "direction.pt", CompressionLevel.Optimal);
        AppendLog($"Exported “{model.Label}” to {dialog.FileName}.");
    }

    private void ImportModel()
    {
        if (_trackingProcesses.Any(process => !process.HasExited)) { MessageBox.Show(this, "Stop live tracking before importing a model."); return; }
        using var dialog = new OpenFileDialog { Title = "Import tongue model", Filter = "Qpro tongue model (*.qptonguemodel)|*.qptonguemodel", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, "Only import model files from someone you trust. PyTorch model files are executable data when loaded.\n\nContinue?", "Trust this model?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var version = TongueModelVersions().DefaultIfEmpty(0).Max() + 1;
        var models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        var gatePath = Path.Combine(models, $"qpro-stereo-tongue-v{version}-gate.pt");
        var directionPath = Path.Combine(models, $"qpro-stereo-tongue-v{version}-direction.pt");
        try
        {
            using var archive = ZipFile.OpenRead(dialog.FileName);
            var gate = archive.GetEntry("gate.pt");
            var direction = archive.GetEntry("direction.pt");
            if (gate is null || direction is null || gate.Length <= 0 || direction.Length <= 0 || gate.Length > 268_435_456 || direction.Length > 268_435_456)
                throw new InvalidDataException("This package does not contain a valid, reasonably sized paired model.");
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null) throw new InvalidDataException("This is not a Qpro tongue-model package (manifest.json is missing).");
            using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
            var manifest = JsonNode.Parse(reader.ReadToEnd())?.AsObject()
                ?? throw new InvalidDataException("The model package manifest is invalid.");
            if (!string.Equals(manifest["format"]?.GetValue<string>(), "qpro-tongue-model-package-v1", StringComparison.Ordinal))
                throw new InvalidDataException("This model package format is not supported.");
            var displayName = manifest["displayName"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(dialog.FileName);
            gate.ExtractToFile(gatePath, false);
            direction.ExtractToFile(directionPath, false);
            WriteModelMetadata(version, displayName, null, "imported package");
            AppendLog($"Imported “{CleanDisplayName(displayName)}” as local model v{version}.");
            ReloadProfiles();
        }
        catch (Exception error)
        {
            if (File.Exists(gatePath)) File.Delete(gatePath);
            if (File.Exists(directionPath)) File.Delete(directionPath);
            if (File.Exists(ModelMetadataPath(version))) File.Delete(ModelMetadataPath(version));
            MessageBox.Show(this, error.Message, "Model import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? PromptForText(string title, string prompt, string initial)
    {
        using var dialog = new TextPromptDialog(title, prompt, initial, UiFontName, Background, Panel, Raised, Border, Accent);
        return dialog.ShowDialog(this) == DialogResult.OK ? CleanDisplayName(dialog.Value) : null;
    }

    private static string CleanDisplayName(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean.Length switch { 0 => "Unnamed tongue model", > 80 => clean[..80].Trim(), _ => clean };
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(value.Where(character => !invalid.Contains(character) && !char.IsControl(character)).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(clean) ? "tongue-model" : clean;
    }

    private ProcessStartInfo PowerShellStart(string script, IEnumerable<string> args, bool hidden)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = hidden,
        };
        foreach (var value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(_root, script) }.Concat(args))
            info.ArgumentList.Add(value);
        var python = FindPythonRuntime();
        // Runtime setup must inspect and, when necessary, repair the shared environment itself.
        // Passing its existing interpreter as an explicit override would make setup accept a
        // CPU-only PyTorch build on an NVIDIA machine and skip that repair.
        if (python is not null && !script.Equals("setup-runtime.ps1", StringComparison.OrdinalIgnoreCase))
            info.Environment["QPRO_PYTHON"] = python;
        var adb = FindAdb();
        if (adb is not null) info.Environment["QPRO_ADB"] = adb;
        var target = GetConfiguredAdbTarget();
        if (!string.IsNullOrWhiteSpace(target))
        {
            info.Environment["ANDROID_SERIAL"] = target;
            info.Environment["QPRO_ADB_TARGET"] = target;
        }
        return info;
    }

    private async Task RefreshStatusAsync()
    {
        var usb = await HasQuestAsync();
        var steam = Process.GetProcessesByName("vrserver").Any();
        var vrcft = Process.GetProcessesByName("VRCFaceTracking").Any();
        SetStatus(_usbStatus, usb ? StatusKind.Good : StatusKind.Bad, usb ? "Connected" : "Not connected");
        SetStatus(_steamStatus, steam ? StatusKind.Good : StatusKind.Bad, steam ? "Running" : "Not running");
        SetStatus(_vrcftStatus, vrcft ? StatusKind.Good : StatusKind.Bad, vrcft ? "Running" : "Not running");
        SetStatus(_bridgeStatus, BridgeInstalled() ? StatusKind.Good : StatusKind.Warning, BridgeInstalled() ? "Installed" : "Setup needed");
        SetStatus(_runtimeStatus, BackendReady() ? StatusKind.Good : StatusKind.Warning, BackendReady() ? "Ready" : "Setup needed");
        SetStatus(_gazeStatus, EyeModelReady() ? StatusKind.Good : StatusKind.Warning, EyeModelReady() ? "Prepared" : "Setup needed");
        UpdateSetupStepStyles();
        UpdateControlState();
    }

    private void UpdateSetupStepStyles()
    {
        var ready = new[] { BackendReady(), BridgeInstalled(), EyeModelReady() };
        var next = Array.FindIndex(ready, value => !value);
        StyleSetupStep(_setupRuntimeButton, _setupRuntimeStatus, "Install runtime", ready[0], next == 0);
        StyleSetupStep(_setupBridgeButton, _setupBridgeStatus, "Install bridge", ready[1], next == 1);
        StyleSetupStep(_setupGazeButton, _setupGazeStatus, "Prepare gaze", ready[2], next == 2);
    }

    private void StyleSetupStep(DarkButton button, Label status, string label, bool complete, bool attention)
    {
        button.Text = complete ? "✓  " + label : label;
        button.OutlineColor = complete ? Good : attention && _setupPulseOn ? Accent : Border;
        button.OutlineWidth = complete || attention && _setupPulseOn ? 2 : 1;
        status.Text = complete ? "● Complete" : attention ? "● Next step" : "○ Waiting";
        status.ForeColor = complete ? Good : attention ? Warning : Muted;
        button.Invalidate();
    }

    private void PlaySfx(string fileName)
    {
        var path = Path.Combine(_root, "SFX", fileName);
        if (!File.Exists(path)) return;
        try
        {
            _soundPlayer?.Stop();
            _soundPlayer?.Dispose();
            _soundPlayer = new SoundPlayer(path);
            _soundPlayer.Play();
        }
        catch (Exception error)
        {
            AppendLog($"Sound could not play: {error.Message}");
        }
    }

    private string? GetConfiguredAdbTarget()
    {
        foreach (var value in new[] { Environment.GetEnvironmentVariable("QPRO_ADB_TARGET"), Environment.GetEnvironmentVariable("ANDROID_SERIAL") })
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

        var configPath = Path.Combine(_root, "config", "wireless-headset.json");
        try
        {
            if (File.Exists(configPath))
                return JsonNode.Parse(File.ReadAllText(configPath))?["adbTarget"]?.GetValue<string>()?.Trim();
        }
        catch { /* An invalid local config must not crash the hub. */ }
        return null;
    }

    private async Task<bool> HasQuestAsync()
    {
        var adb = FindAdb();
        if (adb is null) return false;
        var target = GetConfiguredAdbTarget();
        if (!string.IsNullOrWhiteSpace(target))
        {
            var state = await RunAdbProbeAsync(adb, ["-s", target, "get-state"], 3);
            if (state.Completed && state.ExitCode == 0 && state.Output.Trim() == "device") return true;
            if (target.Contains(':'))
            {
                await RunAdbProbeAsync(adb, ["connect", target], 5);
                state = await RunAdbProbeAsync(adb, ["-s", target, "get-state"], 3);
                return state.Completed && state.ExitCode == 0 && state.Output.Trim() == "device";
            }
            return false;
        }
        var devices = await RunAdbProbeAsync(adb, ["devices"], 3);
        return devices.Completed && devices.ExitCode == 0 &&
            devices.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Trim().EndsWith("\tdevice", StringComparison.Ordinal)) == 1;
    }

    private static async Task<(bool Completed, int ExitCode, string Output)> RunAdbProbeAsync(string adb, IEnumerable<string> arguments, int timeoutSeconds = 4)
    {
        try
        {
            var info = new ProcessStartInfo(adb)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return (false, -1, "ADB could not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return (false, -1, "ADB timed out.");
            }
            var output = string.Join("\n", new[] { await standardOutput, await standardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return (true, process.ExitCode, output);
        }
        catch (Exception error)
        {
            return (false, -1, error.Message);
        }
    }

    private string? FindAdb()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("QPRO_ADB"),
            Path.Combine(_root, "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private bool BridgeInstalled() => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "CustomLibs", "000-Qpro.IndependentGaze.dll"));
    private bool BackendReady() => FindPythonRuntime() is not null;
    private bool EyeModelReady() => File.Exists(Path.Combine(_root, "research", "seacliff_eye_model", "bolt-independent-axes.ptl"));
    private string VisibilityModeValue() => _visibilityMode.SelectedIndex switch { 1 => "camera", 2 => "native", 3 => "agreement", _ => "weighted" };

    private string? FindPythonRuntime()
    {
        var environments = new List<string>
        {
            Path.Combine(_root, ".venv"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "runtime", ".venv")
        };
        var sharedEnvironment = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "runtime", ".venv");
        var sharedReadyMarker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "runtime", "runtime-ready.json");
        var parent = new DirectoryInfo(_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent;
        if (parent is not null)
        {
            try
            {
                environments.AddRange(Directory.GetDirectories(parent.FullName, "QproFaceTracking-*")
                    .OrderByDescending(path => path)
                    .Select(path => Path.Combine(path, ".venv")));
            }
            catch { }
            if (parent.Name.Equals("dist", StringComparison.OrdinalIgnoreCase) && parent.Parent is not null)
                environments.Add(Path.Combine(parent.Parent.FullName, ".venv"));
        }
        foreach (var environment in environments.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(environment), Path.GetFullPath(sharedEnvironment), StringComparison.OrdinalIgnoreCase)
                && !File.Exists(sharedReadyMarker)) continue;
            foreach (var name in new[] { "python.exe", "qpro-python-console.exe" })
            {
                var candidate = Path.Combine(environment, "Scripts", name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private void UpdateControlState()
    {
        _eyeProfiles.Enabled = _gaze.Checked;
        _tongueModels.Enabled = _tongue.Checked;
        _fps.Enabled = _tongue.Checked;
        _smoothing.Enabled = _tongue.Checked;
        _visibilityMode.Enabled = _tongue.Checked;
        var running = _trackingProcesses.Any(p => !p.HasExited);
        _start.Enabled = !running && !_stopping;
        _stop.Enabled = running && !_stopping;
        StyleRunButton(_start, !running && !_stopping);
        StyleRunButton(_stop, running && !_stopping);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_trackingProcesses.Any(p => !p.HasExited)) return;
        e.Cancel = true;
        await StopTrackingAsync();
        if (!_trackingProcesses.Any(p => !p.HasExited)) { FormClosing -= OnClosing; Close(); }
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(text)); return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
    }

    private static int VersionFromPath(string path) => Regex.Match(Path.GetFileName(path), @"-v(\d+)").Success && int.TryParse(Regex.Match(Path.GetFileName(path), @"-v(\d+)").Groups[1].Value, out var v) ? v : 0;
    private static void SelectOrFirst(ComboBox box, string? previous)
    {
        for (var i = 0; i < box.Items.Count; i++) if ((box.Items[i] as FileChoice)?.Primary == previous) { box.SelectedIndex = i; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
    private static void SelectOrFirst(ListBox box, string? previous)
    {
        for (var i = 0; i < box.Items.Count; i++) if ((box.Items[i] as FileChoice)?.Primary == previous) { box.SelectedIndex = i; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
    private enum StatusKind { Good, Warning, Bad }
    private static Label StatusLabel() => new() { AutoSize = true, Font = new Font(UiFontName, 10F, FontStyle.Bold), Margin = new Padding(8, 0, 25, 8) };
    private static Label SetupStatusLabel() => new() { Text = "○ Waiting", AutoSize = true, Font = new Font(UiFontName, 9.5F, FontStyle.Bold), ForeColor = Muted, Margin = new Padding(3, 7, 3, 8) };
    private static void SetStatus(Label label, StatusKind status, string text)
    {
        label.Text = "● " + text;
        label.ForeColor = status switch { StatusKind.Good => Good, StatusKind.Warning => Warning, _ => Bad };
    }
    private static Label SectionTitle(string text) => new() { Text = text, AutoSize = true, Font = new Font(UiFontName, 14F, FontStyle.Bold), ForeColor = Color.White, Margin = new Padding(6, 4, 6, 10) };
    private static Label Info(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(395, 0), ForeColor = Muted, Margin = new Padding(6, 0, 6, 14) };
    private static TableLayoutPanel Card() => new() { AutoSize = true, Dock = DockStyle.Top, BackColor = Panel, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 12) };
    private static DarkButton PrimaryButton(string text) => SecondaryButton(text);
    private static DarkButton SecondaryButton(string text)
    {
        var button = new DarkButton { Text = text, AutoSize = true, BackColor = Raised, ForeColor = Color.White, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 0, 8, 0), Enabled = false };
        return button;
    }
    private static DarkButton ActionButton(string text, EventHandler action) { var button = SecondaryButton(text); button.Enabled = true; button.Margin = new Padding(6, 4, 6, 4); button.Click += action; return button; }
    private static DarkButton SetupButton(string text) { var button = SecondaryButton(text); button.Enabled = true; button.AutoSize = false; button.Height = 42; button.Dock = DockStyle.Bottom; button.Margin = new Padding(3, 8, 3, 3); return button; }

    private static Control SetupStepCard(string number, string title, string description, Label status, DarkButton button)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = Raised, Padding = new Padding(13), Margin = new Padding(5) };
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        card.Controls.Add(new Label { Text = $"STEP {number}", AutoSize = true, ForeColor = Warning, Font = new Font(UiFontName, 8.5F, FontStyle.Bold) }, 0, 0);
        card.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.White, Font = new Font(UiFontName, 11F, FontStyle.Bold), Margin = new Padding(3, 3, 3, 4) }, 0, 1);
        card.Controls.Add(status, 0, 2);
        card.Controls.Add(new Label { Text = description, AutoSize = true, MaximumSize = new Size(155, 0), ForeColor = Muted, Margin = new Padding(3, 0, 3, 5) }, 0, 3);
        card.Controls.Add(button, 0, 4);
        return card;
    }

    private static Control WorkflowCard(string title, string description, ComboBox queue, Label queueStatus, Button capture, Button train)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, BackColor = Raised, Padding = new Padding(10), Margin = new Padding(5) };
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(UiFontName, 10.5F, FontStyle.Bold), ForeColor = Warning });
        card.Controls.Add(new Label { Text = description, AutoSize = true, MaximumSize = new Size(220, 0), ForeColor = Muted, Margin = new Padding(3, 4, 3, 7) });
        card.Controls.Add(new Label { Text = "Recorded datasets waiting to train", AutoSize = true, ForeColor = Color.White, Margin = new Padding(3, 5, 3, 3) });
        card.Controls.Add(queue);
        queueStatus.Margin = new Padding(3, 3, 3, 8);
        card.Controls.Add(queueStatus);
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        capture.AutoSize = false; train.AutoSize = false; capture.Height = 38; train.Height = 38; capture.Dock = DockStyle.Fill; train.Dock = DockStyle.Fill;
        actions.Controls.Add(capture, 0, 0); actions.Controls.Add(train, 0, 1);
        card.Controls.Add(actions);
        return card;
    }

    private static CheckBox FeatureToggle(string text, bool initial)
    {
        var toggle = new CheckBox
        {
            Text = "  " + text,
            Checked = initial,
            Appearance = Appearance.Button,
            AutoSize = false,
            Height = 35,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            BackColor = Raised,
            Margin = new Padding(0, 2, 0, 4),
        };
        toggle.FlatAppearance.BorderColor = Border;
        toggle.FlatAppearance.CheckedBackColor = Color.FromArgb(86, 18, 19);
        toggle.FlatAppearance.MouseDownBackColor = Color.FromArgb(100, 21, 22);
        return toggle;
    }

    private static void UpdateToggleStyle(CheckBox toggle)
    {
        toggle.Text = (toggle.Checked ? "  ◆ " : "  ◇ ") + toggle.Text.TrimStart(' ', '◆', '◇');
        toggle.BackColor = toggle.Checked ? Color.FromArgb(86, 18, 19) : Raised;
        toggle.ForeColor = Color.White;
        toggle.FlatAppearance.BorderColor = toggle.Checked ? Accent : Border;
        toggle.FlatAppearance.BorderSize = toggle.Checked ? 2 : 1;
        toggle.FlatAppearance.MouseOverBackColor = RaisedHover;
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
            using var fill = new SolidBrush(selected ? Accent : Raised);
            e.Graphics.FillRectangle(fill, e.Bounds);
            var item = box.Items[e.Index]?.ToString() ?? string.Empty;
            TextRenderer.DrawText(e.Graphics, item, box.Font, new Rectangle(e.Bounds.X + 7, e.Bounds.Y, e.Bounds.Width - 7, e.Bounds.Height), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    private static void ConfigureModelList(ListBox box)
    {
        box.BackColor = Color.FromArgb(25, 3, 3);
        box.ForeColor = Color.White;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 38;
        box.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? Color.FromArgb(86, 18, 19) : Color.FromArgb(25, 3, 3));
            e.Graphics.FillRectangle(fill, e.Bounds);
            using var border = new Pen(selected ? Accent : Border, selected ? 2 : 1);
            e.Graphics.DrawRectangle(border, e.Bounds.X + 1, e.Bounds.Y + 1, e.Bounds.Width - 3, e.Bounds.Height - 3);
            var item = box.Items[e.Index]?.ToString() ?? string.Empty;
            TextRenderer.DrawText(e.Graphics, (selected ? "●  " : "○  ") + item, box.Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    private static DarkButton NavigationButton(string text)
    {
        var button = new DarkButton
        {
            Text = "○  " + text,
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
        button.Text = (selected ? "●  " : "○  ") + button.Text.TrimStart(' ', '●', '○');
        button.BackColor = selected ? Color.FromArgb(86, 18, 19) : Panel;
        button.Emphasized = selected;
        button.Invalidate();
    }

    private static void StyleRunButton(Button button, bool nextAction)
    {
        button.BackColor = Raised;
        button.ForeColor = button.Enabled ? Color.White : Color.FromArgb(143, 119, 119);
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
        e.Graphics.Clear(Color.FromArgb(25, 3, 3));
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
    [DefaultValue(false)]
    public bool Emphasized { get => _emphasized; set { _emphasized = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public Color OutlineColor { get => _outlineColor; set { _outlineColor = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public int OutlineWidth { get => _outlineWidth; set { _outlineWidth = Math.Clamp(value, 1, 4); Invalidate(); } }

    public DarkButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_pressed ? HubForm.Accent : _hovered && Enabled ? HubForm.RaisedHover : BackColor);
        var borderColor = OutlineColor.IsEmpty ? (Emphasized ? HubForm.Accent : HubForm.Border) : OutlineColor;
        var borderWidth = Emphasized ? Math.Max(2, OutlineWidth) : OutlineWidth;
        using var border = new Pen(borderColor, borderWidth);
        var inset = borderWidth > 1 ? 1 : 0;
        e.Graphics.DrawRectangle(border, inset, inset, Width - (inset * 2 + 1), Height - (inset * 2 + 1));
        var textColor = Enabled ? Color.White : Color.FromArgb(142, 112, 112);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
        _input = new TextBox { Text = initial, Dock = DockStyle.Top, BackColor = raised, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, MaxLength = 80 };
        layout.Controls.Add(_input, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 16, 0, 0) };
        var save = new DarkButton { Text = "Save name", DialogResult = DialogResult.OK, AutoSize = true, Enabled = true, Emphasized = true, BackColor = raised, ForeColor = Color.White, Padding = new Padding(14, 7, 14, 7) };
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
        set { _value = Math.Clamp(value, Minimum, Maximum); Invalidate(); }
    }

    public DarkSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var left = 8;
        var right = Math.Max(left + 1, Width - 8);
        var center = Height / 2;
        var range = Math.Max(1, Maximum - Minimum);
        var ratio = (Value - Minimum) / (float)range;
        var thumbX = left + (int)Math.Round((right - left) * ratio);
        using var track = new Pen(HubForm.Border, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(HubForm.Accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(track, left, center, right, center);
        e.Graphics.DrawLine(active, left, center, thumbX, center);
        using var thumb = new SolidBrush(Enabled ? Color.White : Color.FromArgb(125, 105, 105));
        e.Graphics.FillEllipse(thumb, thumbX - 7, center - 7, 14, 14);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (e.Button == MouseButtons.Left) SetFromX(e.X); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Down) Value--;
        if (e.KeyCode is Keys.Right or Keys.Up) Value++;
    }
    private void SetFromX(int x)
    {
        if (!Enabled) return;
        var ratio = Math.Clamp((x - 8f) / Math.Max(1, Width - 16), 0f, 1f);
        Value = Minimum + (int)Math.Round((Maximum - Minimum) * ratio);
    }
}
