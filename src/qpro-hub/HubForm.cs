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
using Qpro.Shared;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm : Form
{
    private readonly string _root;
    private readonly HubEnvironment _environment;
    private readonly HubScriptFactory _scripts;
    private readonly string _stopFile;
    private readonly CheckBox _gaze = FeatureToggle("Independent eye gaze + convergence", false);
    private readonly CheckBox _tongue = FeatureToggle("Experimental tongue tracking", false);
    private readonly CheckBox _pupil = FeatureToggle("Experimental relative pupil dilation (eye cameras)", false);
    private readonly CheckBox _cameraPreview = FeatureToggle("Preview tracking cameras", false);
    private readonly CheckBox _individualCheekPuff = FeatureToggle("Individual cheek puff", true);
    private readonly CheckBox _cameraCheekPuff = FeatureToggle("Camera cheek puff (experimental)", false);
    private readonly Label _cameraCheekSourceNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly ComboBox _cheekCameraDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _cheekCameraBaseModels = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _cheekCameraDatasetNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly DarkButton _recordCameraCheeks = SecondaryButton("1. Record cheek camera poses");
    private readonly DarkButton _trainCameraCheeks = SecondaryButton("2. Train tongue + cheeks copy");
    private readonly CheckBox _individualCheekSuck = FeatureToggle("Individual cheek suck", true);
    private readonly CheckBox _eyebrowBoost = FeatureToggle("Adjust eyebrow movement", false);
    private readonly ComboBox _trackingSourceSetup = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _trackingSourceLive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _trackingSourceSetupNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly Label _trackingSourceLiveNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly ComboBox _eyeProfiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _tongueModels = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _quickDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _focusedDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _fullDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _quickRecordedDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _focusedRecordedDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _fullRecordedDatasets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly Label _quickQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label _focusedQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label _fullQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly DarkProgressBar _trainingProgress = new() { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(4, 5, 4, 2) };
    private readonly Label _trainingProgressStatus = new() { Text = "Training idle — select a recorded dataset when ready.", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 2, 4, 3) };
    private readonly TableLayoutPanel _trainingProgressContainer = new() { Visible = false };
    private readonly ListBox _modelList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly Label _modelEmpty = new() { Text = "No paired tongue models found. Extract the complete release or import a .qptonguemodel package.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted, Padding = new Padding(24) };
    private readonly Label _tongueModelNote = new() { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Muted, Margin = new Padding(24, 2, 0, 4), Tag = "responsive-info" };
    private readonly ComboBox _fps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 76 };
    private readonly ComboBox _pupilSensitivity = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly DarkSlider _smoothing = new() { Minimum = 0, Maximum = 100, Value = 55, Width = 180, Height = 30 };
    private readonly ComboBox _visibilityMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265 };
    private readonly ComboBox _cheekPuffStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245 };
    private readonly DarkButton _calibrateCheekPuff = SecondaryButton("Calibrate cheek puff");
    private readonly ComboBox _cheekSuckStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245 };
    private readonly ComboBox _eyebrowSensitivity = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _usbStatus = StatusLabel();
    private readonly Label _steamStatus = StatusLabel();
    private readonly Label _vrcftStatus = StatusLabel();
    private readonly Label _bridgeStatus = StatusLabel();
    private readonly Label _runtimeStatus = StatusLabel();
    private readonly Label _gazeStatus = StatusLabel();
    private readonly Label _inferenceStatus = StatusLabel();
    private readonly Label _pupilStatus = StatusLabel();
    private string _pupilBackendLabel = "Starting";
    private readonly Label _setupRuntimeStatus = SetupStatusLabel();
    private readonly Label _setupBridgeStatus = SetupStatusLabel();
    private readonly Label _setupGazeStatus = SetupStatusLabel();
    private readonly DarkButton _setupRuntimeButton = SetupButton("Install runtime");
    private readonly DarkButton _setupBridgeButton = SetupButton("Install Virtual Desktop module");
    private readonly DarkButton _setupSteamLinkModuleButton = SetupButton("Install Steam Link module");
    private readonly DarkButton _uninstallBridgeButton = SetupButton("Uninstall Qpro module");
    private readonly DarkButton _setupGazeButton = SetupButton("Prepare gaze");
    private readonly DarkButton _recoverGazeButton = SetupButton("Recover Qpro gaze");
    private readonly DarkButton _inspectGazeButton = SetupButton("Check gaze setup");
    private readonly DarkButton _resetLegacyGazeButton = SetupButton("Reset legacy gaze");
    private readonly DarkProgressBar _setupProgress = new() { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(4, 5, 4, 2) };
    private readonly Label _setupProgressStatus = new() { Text = "Setup idle.", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 2, 4, 3), Tag = "responsive-info" };
    private readonly TableLayoutPanel _setupProgressContainer = new();
    private readonly Label _runStatus = new() { Text = "● Idle — Qpro live overrides off", AutoSize = true, ForeColor = Good };
    private readonly RichTextBox _log = new() { ReadOnly = true, BackColor = Inset, ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical, HideSelection = false };
    private readonly Button _start = PrimaryButton("Start tracking");
    private readonly Button _stop = SecondaryButton("Stop tracking");
    private readonly List<Process> _trackingProcesses = [];
    private SoundPlayer? _soundPlayer;
    private bool _stopping;
    private bool _starting;
    private bool _closingInProgress;
    private bool _gazeStartupInProgress;
    private TaskCompletionSource<bool>? _gazeStartupSignal;
    private bool _gazeFailureHandled;
    private volatile bool _gazeRecoveryConfirmed;
    private volatile bool _gazeInspectionConfirmed;
    private volatile HubGazeInspection? _gazeInspectionResult;
    private volatile bool _legacyGazeResetConfirmed;
    private bool _datasetOperationBusy;
    private CancellationTokenSource? _startCancellation;
    private bool _statusRefreshBusy;
    private bool _setupPulseOn;
    private bool _setupActionRunning;
    private bool _adjacentModelsImported;
    private bool _trackingSourceSelectionUpdating;
    private int _trainingStage;
    private int _trainingStageCount = 2;

    internal static readonly Color Background = Color.FromArgb(33, 57, 66);      // #213942
    internal static readonly Color Panel = Color.FromArgb(25, 47, 55);           // #192F37
    internal static readonly Color Inset = Color.FromArgb(17, 35, 42);
    internal static readonly Color Raised = Color.FromArgb(44, 75, 85);          // #2C4B55
    internal static readonly Color Selected = Color.FromArgb(52, 92, 101);       // #345C65
    internal static readonly Color RaisedHover = Color.FromArgb(59, 98, 108);    // #3B626C
    internal static readonly Color Border = Color.FromArgb(82, 124, 132);        // #527C84
    internal static readonly Color Accent = Color.FromArgb(114, 208, 198);       // #72D0C6
    internal static readonly Color Muted = Color.FromArgb(201, 220, 222);        // #C9DCDE
    internal static readonly Color DisabledText = Color.FromArgb(167, 189, 195);
    internal static readonly Color Good = Color.FromArgb(145, 228, 184);         // #91E4B8
    internal static readonly Color Warning = Color.FromArgb(240, 208, 138);      // #F0D08A
    internal static readonly Color Bad = Color.FromArgb(243, 141, 133);          // #F38D85
    private static readonly string UiFontName = FontFamily.Families.Any(font => font.Name.Equals("Lexend", StringComparison.OrdinalIgnoreCase)) ? "Lexend" : "Segoe UI";

    private readonly bool _previewOnly;
    private readonly System.ComponentModel.Container _uiComponents = new();

    public HubForm(string root, bool rememberLaunch = true, bool previewOnly = false)
    {
        _previewOnly = previewOnly;
        _root = root;
        _environment = new HubEnvironment(root);
        _cameraPreview.Checked = _environment.CameraPreviewEnabled;
        _gaze.Checked = _environment.IndependentGazeEnabled;
        _scripts = new HubScriptFactory(root, _environment);
        _stopFile = Path.Combine(root, ".qpro-hub-stop");
        Text = $"QproFaceTracking V{AppVersion} Hub";
        MinimumSize = new Size(780, 580);
        Size = new Size(1140, 850);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;
        BackColor = Background;
        ForeColor = Color.WhiteSmoke;
        Font = new Font(UiFontName, 10F);
        StartPosition = FormStartPosition.CenterScreen;
        HandleCreated += (_, _) => EnableDarkTitleBar(Handle);
        Shown += (_, _) =>
        {
            var workArea = Screen.FromHandle(Handle).WorkingArea;
            if (Width <= workArea.Width && Height <= workArea.Height) return;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, workArea.Width), Math.Min(MinimumSize.Height, workArea.Height));
            Size = new Size(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
            Location = new Point(workArea.Left + (workArea.Width - Width) / 2, workArea.Top + (workArea.Height - Height) / 2);
        };

        Controls.Add(BuildLayout());
        InitializeTrackingSourceUi();
        InitializeControllerInputUi();
        if (rememberLaunch && !previewOnly)
            Shown += (_, _) =>
            {
                try { _environment.MarkOpened(); }
                catch (Exception error) { AppendLog("Could not save first-launch state: " + error.Message); }
            };
        InitializeIntegratedSetup();
        _start.Click += async (_, _) => await StartTrackingAsync();
        _stop.Click += async (_, _) => await StopTrackingAsync();
        _gaze.CheckedChanged += (_, _) =>
        {
            if (_previewOnly) return;
            try { _environment.SelectIndependentGaze(_gaze.Checked); }
            catch (Exception error) { AppendLog("Could not save independent gaze preference: " + error.Message); }
            UpdateGazeSetupStatus();
            UpdateControlState();
        };
        _tongue.CheckedChanged += (_, _) => UpdateControlState();
        _cameraCheekPuff.CheckedChanged += (_, _) => { UpdateToggleStyle(_cameraCheekPuff); UpdateTongueModelNote(); UpdateControlState(); };
        _pupil.CheckedChanged += (_, _) => UpdateControlState();
        _cameraPreview.CheckedChanged += (_, _) =>
        {
            if (_previewOnly) return;
            try { _environment.SelectCameraPreview(_cameraPreview.Checked); }
            catch (Exception error) { AppendLog("Could not save camera preview preference: " + error.Message); }
            UpdateToggleStyle(_cameraPreview);
        };
        _tongueModels.SelectedIndexChanged += (_, _) => UpdateTongueModelNote();
        _recordCameraCheeks.Click += async (_, _) => await RecordCameraCheeksAsync();
        _trainCameraCheeks.Click += async (_, _) => await TrainCameraCheeksAsync();
        _cheekCameraDatasets.SelectedIndexChanged += (_, _) => UpdateControlState();
        _cheekCameraBaseModels.SelectedIndexChanged += (_, _) => UpdateControlState();
        _fps.Items.AddRange(["12", "15", "18", "20", "24", "30", "36", "48", "60", "72"]);
        _fps.SelectedItem = "24";
        _pupilSensitivity.Items.AddRange(Enumerable.Range(0, 11)
            .Select(index => $"{1.0 + index * 0.2:0.0}×" + (index == 2 ? " (Balanced)" : ""))
            .ToArray());
        _pupilSensitivity.SelectedIndex = 2;
        _visibilityMode.Items.AddRange(["Weighted camera + native", "Camera only", "Native only", "Conservative agreement"]);
        _visibilityMode.SelectedIndex = 0;
        _individualCheekPuff.Checked = HubCheekPuffPreference.LoadMode() != HubCheekPuffMode.Off;
        _cheekPuffStyle.Items.AddRange(["Calibrated", "1/0", "Balanced"]);
        _cheekPuffStyle.SelectedIndex = HubCheekPuffPreference.LoadLastStyle() switch
        {
            HubCheekPuffMode.Calibrated => 0,
            HubCheekPuffMode.Balanced => 2,
            _ => 1,
        };
        _cheekPuffStyle.Enabled = _individualCheekPuff.Checked;
        void SaveCheekPuffChoice()
        {
            if (_previewOnly) return;
            try
            {
                HubCheekPuffMode style = _cheekPuffStyle.SelectedIndex switch
                {
                    0 => HubCheekPuffMode.Calibrated,
                    2 => HubCheekPuffMode.Balanced,
                    _ => HubCheekPuffMode.Strong,
                };
                HubCheekPuffPreference.Save(_individualCheekPuff.Checked, style);
                _cheekPuffStyle.Enabled = _individualCheekPuff.Checked;
                UpdateToggleStyle(_individualCheekPuff);
                AppendLog(_individualCheekPuff.Checked
                    ? $"Individual cheek puff selected: {_cheekPuffStyle.SelectedItem}. Applies while Qpro tracking runs; Stop restores native cheeks."
                    : "Individual cheek puff off: using the streaming app's original cheek values when camera cheek output is off.");
            }
            catch (Exception error) { AppendLog("Could not save cheek puff choice: " + error.Message); }
        }
        _individualCheekPuff.CheckedChanged += (_, _) => SaveCheekPuffChoice();
        _cheekPuffStyle.SelectedIndexChanged += (_, _) => SaveCheekPuffChoice();
        _calibrateCheekPuff.Enabled = true;
        _calibrateCheekPuff.Margin = new Padding(6, 4, 6, 4);
        _calibrateCheekPuff.Click += (_, _) =>
        {
            string source = _environment.SteamLinkSelected
                ? CheekPuffCalibrationProfile.SteamLinkSource
                : CheekPuffCalibrationProfile.VirtualDesktopSource;
            using var dialog = new CheekPuffCalibrationDialog(source, UiFontName);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _cheekPuffStyle.SelectedIndex = 0;
            _individualCheekPuff.Checked = true;
            AppendLog($"Personal cheek puff calibration saved for {(_environment.SteamLinkSelected ? "Steam Link" : "Virtual Desktop")}. Calibrated mode is selected; the Qpro module updates while tracking is running.");
        };
        _individualCheekSuck.Checked = HubCheekSuckPreference.LoadMode() != HubCheekSuckMode.Off;
        _cheekSuckStyle.Items.AddRange(["Balanced", "Strong individual (1/0)"]);
        _cheekSuckStyle.SelectedIndex = HubCheekSuckPreference.LoadLastStrongStyle() ? 1 : 0;
        _cheekSuckStyle.Enabled = _individualCheekSuck.Checked;
        void SaveCheekSuckChoice()
        {
            if (_previewOnly) return;
            try
            {
                HubCheekSuckPreference.Save(_individualCheekSuck.Checked, _cheekSuckStyle.SelectedIndex == 1);
                _cheekSuckStyle.Enabled = _individualCheekSuck.Checked;
                UpdateToggleStyle(_individualCheekSuck);
                AppendLog(_individualCheekSuck.Checked
                    ? $"Individual cheek suck selected: {_cheekSuckStyle.SelectedItem}. Applies while Qpro tracking runs; Stop restores native cheeks."
                    : "Individual cheek suck off: using the streaming app's original cheek values.");
            }
            catch (Exception error) { AppendLog("Could not save cheek suck choice: " + error.Message); }
        }
        _individualCheekSuck.CheckedChanged += (_, _) => SaveCheekSuckChoice();
        _cheekSuckStyle.SelectedIndexChanged += (_, _) => SaveCheekSuckChoice();
        EyebrowSettings eyebrowSettings = EyebrowPreference.Load();
        _eyebrowSensitivity.Items.AddRange(Enumerable.Range(0, 11)
            .Select(index => $"{0.5 + index * 0.25:0.00}×" + (index == 2 ? " (Native)" : ""))
            .ToArray());
        _eyebrowSensitivity.SelectedIndex = Math.Clamp(
            (int)MathF.Round((eyebrowSettings.Sensitivity - 0.5f) / 0.25f), 0, 10);
        _eyebrowBoost.Checked = eyebrowSettings.Enabled;
        _eyebrowSensitivity.Enabled = _eyebrowBoost.Checked;
        void SaveEyebrowChoice()
        {
            if (_previewOnly) return;
            try
            {
                float sensitivity = 0.5f + _eyebrowSensitivity.SelectedIndex * 0.25f;
                EyebrowPreference.Save(new EyebrowSettings(_eyebrowBoost.Checked, sensitivity));
                _eyebrowSensitivity.Enabled = _eyebrowBoost.Checked;
                UpdateToggleStyle(_eyebrowBoost);
                AppendLog(_eyebrowBoost.Checked
                    ? $"Eyebrow response on: {sensitivity:0.00}×. The Qpro module updates while tracking is running."
                    : "Eyebrow adjustment off: using the streaming app's original brow values. The Qpro module updates while tracking is running.");
            }
            catch (Exception error) { AppendLog("Could not save eyebrow choice: " + error.Message); }
        }
        _eyebrowBoost.CheckedChanged += (_, _) => SaveEyebrowChoice();
        _eyebrowSensitivity.SelectedIndexChanged += (_, _) => SaveEyebrowChoice();
        ConfigureDropDown(_eyeProfiles);
        ConfigureDropDown(_tongueModels);
        ConfigureDropDown(_fps);
        ConfigureDropDown(_pupilSensitivity);
        ConfigureDropDown(_visibilityMode);
        ConfigureDropDown(_cheekPuffStyle);
        ConfigureDropDown(_cheekSuckStyle);
        ConfigureDropDown(_eyebrowSensitivity);
        ConfigureDropDown(_quickDatasets);
        ConfigureDropDown(_focusedDatasets);
        ConfigureDropDown(_fullDatasets);
        ConfigureDropDown(_quickRecordedDatasets);
        ConfigureDropDown(_focusedRecordedDatasets);
        ConfigureDropDown(_fullRecordedDatasets);
        ConfigureDropDown(_cheekCameraBaseModels);
        ConfigureDropDown(_cheekCameraDatasets);
        ConfigureModelList(_modelList);
        UpdateToggleStyle(_gaze);
        UpdateToggleStyle(_tongue);
        UpdateToggleStyle(_pupil);
        UpdateToggleStyle(_cameraPreview);
        UpdateToggleStyle(_individualCheekPuff);
        UpdateToggleStyle(_cameraCheekPuff);
        UpdateToggleStyle(_individualCheekSuck);
        UpdateToggleStyle(_eyebrowBoost);
        SetStatus(_inferenceStatus, StatusKind.Warning, "Idle");
        SetStatus(_pupilStatus, StatusKind.Warning, "Idle");
        _gaze.CheckedChanged += (_, _) => UpdateToggleStyle(_gaze);
        _tongue.CheckedChanged += (_, _) => UpdateToggleStyle(_tongue);
        _pupil.CheckedChanged += (_, _) => UpdateToggleStyle(_pupil);
        if (!previewOnly) FormClosing += OnClosing;

        if (!previewOnly) { ReloadProfiles(); _ = RefreshStatusAsync(); }
        var timer = new System.Windows.Forms.Timer(_uiComponents) { Interval = 2500 };
        timer.Tick += async (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) await RefreshStatusAsync();
        };
        if (!previewOnly) timer.Start();
        var pulseTimer = new System.Windows.Forms.Timer(_uiComponents) { Interval = 550 };
        pulseTimer.Tick += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) PulseSetupAttention();
        };
        if (!previewOnly) pulseTimer.Start();
        var setupProgressTimer = new System.Windows.Forms.Timer(_uiComponents) { Interval = 45 };
        setupProgressTimer.Tick += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized && _setupProgress.Visible && _setupProgress.IsIndeterminate)
                _setupProgress.AdvanceAnimation();
        };
        if (!previewOnly) setupProgressTimer.Start();
        FormClosed += (_, _) =>
        {
            EndCheekTrackingSession();
            timer.Stop(); pulseTimer.Stop(); setupProgressTimer.Stop();
        };
        Disposed += (_, _) =>
        {
            // Dispose also covers a form abandoned before it was shown, where
            // FormClosed never fires. Native timers must not retain that form.
            _uiComponents.Dispose();
            EndCheekTrackingSession();
            _soundPlayer?.Dispose();
            foreach (var process in _trackingProcesses) process.Dispose();
        };
        RestoreLiveOptions();
        WireUpdates();
        AppendLog("Hub ready. Cheek adjustments and Qpro live overrides start when you press Start tracking. Eyebrow and smirk adjustments can already apply through the installed module.");
    }

    private void InitializeTrackingSourceUi()
    {
        foreach (var choice in new[] { _trackingSourceSetup, _trackingSourceLive })
        {
            choice.Items.AddRange(["Virtual Desktop", "Steam Link"]);
            choice.SelectedIndex = _environment.SteamLinkSelected ? 1 : 0;
            ConfigureDropDown(choice);
            choice.SelectedIndexChanged += (_, _) => SelectTrackingSource(choice.SelectedIndex == 1);
        }
        UpdateTrackingSourceNotes();
    }

    private void SelectTrackingSource(bool steamLink)
    {
        if (_previewOnly || _trackingSourceSelectionUpdating || steamLink == _environment.SteamLinkSelected) return;
        if (_setupActionRunning || _utilityActionRunning || _datasetOperationBusy ||
            _starting || _stopping || LiveTrackingRunning)
        {
            SyncTrackingSourceControls();
            return;
        }
        try
        {
            _environment.SelectTrackingSource(steamLink);
            SyncTrackingSourceControls();
            UpdateTrackingSourceNotes();
            var selectedName = steamLink ? "Steam Link" : "Virtual Desktop";
            AppendLog($"Face-tracking source selected: {selectedName}.");
            if (!CurrentBridgeInstalled())
            {
                AppendLog("Close VRCFaceTracking, install the selected Qpro module, then restart it to load the face-tracking source.");
                MessageBox.Show(this,
                    $"Close VRCFaceTracking, use First-time setup to install the Qpro {selectedName} module, then reopen VRCFaceTracking before tracking or tongue capture.",
                    "Install selected module", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (_environment.TrackingSourceRequiresVrcftRestart())
            {
                AppendLog("Restart VRCFaceTracking so the Qpro module loads the selected face-tracking source.");
                MessageBox.Show(this, "Close and reopen VRCFaceTracking before tracking or tongue capture.",
                    "Restart VRCFaceTracking", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception error)
        {
            SyncTrackingSourceControls();
            MessageBox.Show(this, error.Message, "Could not save face-tracking source", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SyncTrackingSourceControls()
    {
        _trackingSourceSelectionUpdating = true;
        try
        {
            var selected = _environment.SteamLinkSelected ? 1 : 0;
            _trackingSourceSetup.SelectedIndex = selected;
            _trackingSourceLive.SelectedIndex = selected;
        }
        finally { _trackingSourceSelectionUpdating = false; }
    }

    private void UpdateTrackingSourceNotes()
    {
        var note = _environment.SteamLinkSelected
            ? "In SteamVR > Settings > Steam Link, show Advanced Settings, enable OSC, share eye and face tracking, and set Steam Link OSC output to 9015 (ALT). Keep VRCFaceTracking's VRChat OSC ports at 9000/9001."
            : "Start Virtual Desktop with eye and face tracking forwarded to the PC. Keep VRCFaceTracking's VRChat OSC ports at their defaults.";
        _trackingSourceSetupNote.Text = note;
        _trackingSourceLiveNote.Text = note;
        UpdateModuleInstallButtonState(!_setupActionRunning);
        UpdateControllerInputAvailability();
    }
}
