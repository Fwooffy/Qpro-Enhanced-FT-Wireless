#if QPRO_REMOTE_CLICKER
global using HubDropDown = QproFaceTracking.Hub.KilobyteComboBox;
#else
global using HubDropDown = System.Windows.Forms.ComboBox;
#endif

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
#if QPRO_REMOTE_CLICKER
    internal const string EditionName = "Kilobyte Edition";
    internal const string ProductDisplayName = "Qpro Companion · " + EditionName;
#else
    internal const string ProductDisplayName = "Qpro Companion";
#endif
    private readonly string _root;
    private readonly HubEnvironment _environment;
    private readonly HubScriptFactory _scripts;
    private readonly string _stopFile;
    private readonly CheckBox _gaze = FeatureToggle("Independent eye gaze + convergence", false);
    private readonly CheckBox _tongue = FeatureToggle("Experimental tongue tracking", false);
    private readonly CheckBox _pupil = FeatureToggle("Experimental relative pupil dilation (eye cameras)", false);
    private readonly CheckBox _cameraPreview = FeatureToggle("Preview tracking cameras", false);
    private readonly CheckBox _trackingStopSound = FeatureToggle("Sound once if a tracking feature stops unexpectedly", false);
    private readonly CheckBox _individualCheekPuff = FeatureToggle("Individual cheek puff (1/0)", true);
    private readonly CheckBox _individualCheekSuck = FeatureToggle("Individual cheek suck (1/0)", true);
    private readonly CheckBox _eyebrowBoost = FeatureToggle("Adjust eyebrow movement", false);
    private readonly ComboBox _trackingSourceSetup = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _trackingSourceLive = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _trackingSourceSetupNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly Label _trackingSourceLiveNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly ComboBox _eyeProfiles = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _tongueModels = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _quickDatasets = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _focusedDatasets = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _fullDatasets = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _quickRecordedDatasets = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _focusedRecordedDatasets = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly ComboBox _fullRecordedDatasets = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly Label _quickQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label _focusedQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label _fullQueueStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly DarkProgressBar _trainingProgress = new() { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(4, 5, 4, 2) };
    private readonly Label _trainingProgressStatus = new() { Text = "Training idle — select a recorded dataset when ready.", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 2, 4, 3) };
    private readonly TableLayoutPanel _trainingProgressContainer = new() { Visible = false };
    private readonly ListBox _modelList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly Label _modelEmpty = new() { Text = "No paired tongue models found. Extract the complete release or import a .qptonguemodel package.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted, Padding = new Padding(24) };
    private readonly Label _tongueModelNote = new() { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Muted, Margin = new Padding(24, 2, 0, 4), Tag = "responsive-info" };
    private readonly ComboBox _fps = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 76 };
    private readonly ComboBox _processingProfile = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Enabled = false };
    private readonly Label _processingProfileNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly ComboBox _pupilSensitivity = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly DarkSlider _smoothing = new() { Minimum = 0, Maximum = 100, Value = 55, Width = 180, Height = 30 };
    private readonly ComboBox _visibilityMode = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 265 };
    private readonly ComboBox _eyebrowSensitivity = new HubDropDown() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _usbStatus = StatusLabel();
    private readonly Label _steamStatus = StatusLabel();
    private readonly Label _directVrchatStatus = StatusLabel();
    private readonly Label _runtimeStatus = StatusLabel();
    private readonly Label _gazeStatus = StatusLabel();
    private readonly Label _inferenceStatus = StatusLabel();
    private readonly Label _pupilStatus = StatusLabel();
    private readonly Label _setupRuntimeStatus = SetupStatusLabel();
    private readonly Label _setupGazeStatus = SetupStatusLabel();
    private readonly DarkButton _setupRuntimeButton = SetupButton("Install runtime");
    private readonly DarkButton _setupGazeButton = SetupButton("Prepare gaze");
    private readonly DarkButton _recoverGazeButton = SetupButton("Recover Qpro gaze");
    private readonly DarkButton _inspectGazeButton = SetupButton("Check gaze setup");
    private readonly DarkButton _resetLegacyGazeButton = SetupButton("Reset legacy gaze");
    private readonly DarkProgressBar _setupProgress = new() { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(4, 5, 4, 2) };
    private readonly Label _setupProgressStatus = new() { Text = "Setup idle.", AutoSize = true, ForeColor = Muted, Margin = new Padding(4, 2, 4, 3), Tag = "responsive-info" };
    private readonly TableLayoutPanel _setupProgressContainer = new();
    private readonly Label _runStatus = new() { Text = "○ Optional tracking stopped", AutoSize = true, ForeColor = Muted };
    private readonly RichTextBox _log = new() { ReadOnly = true, DetectUrls = false, BackColor = Inset, ForeColor = TextBody, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Vertical, HideSelection = false };
    private readonly Button _start = PrimaryButton("Start tracking");
    private readonly Button _stop = SecondaryButton("Stop tracking");
    private readonly List<Process> _trackingProcesses = [];
    private SoundPlayer? _soundPlayer;
    private readonly HubTrackingStopAlert _trackingStopAlert = new();
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

#if QPRO_REMOTE_CLICKER
    // Dark evergreen surfaces and controls are paired with sandstone navigation accents.
    internal static readonly Color ShellSurface = Color.FromArgb(0x07, 0x1D, 0x10);
    internal static readonly Color Background = Color.FromArgb(0x0B, 0x26, 0x15);
    internal static readonly Color Panel = Color.FromArgb(0x10, 0x37, 0x1C);
    internal static readonly Color Inset = Color.FromArgb(0x07, 0x1D, 0x0D);
    internal static readonly Color Raised = Color.FromArgb(0x19, 0x42, 0x29);
    internal static readonly Color Selected = Color.FromArgb(0x14, 0x4D, 0x26);
    internal static readonly Color RaisedHover = Color.FromArgb(0x25, 0x53, 0x34);
    internal static readonly Color Border = Color.FromArgb(0x61, 0x89, 0x6A);
    internal static readonly Color ControlOutline = Color.FromArgb(0x95, 0xAD, 0x98);
    internal static readonly Color FocusRing = Color.FromArgb(0xF3, 0xD2, 0x96);
    internal static readonly Color Accent = Color.FromArgb(0x86, 0xBC, 0x8B);
    internal static readonly Color PrimaryFill = Color.FromArgb(0x04, 0x61, 0x28);
    internal static readonly Color PrimaryHover = Color.FromArgb(0x10, 0x6D, 0x32);
    internal static readonly Color PrimaryPressed = Color.FromArgb(0x00, 0x4E, 0x1D);
    internal static readonly Color TextPrimary = Color.FromArgb(0xF0, 0xE4, 0xCC);
    internal static readonly Color PrimaryText = TextPrimary;
    internal static readonly Color TextBody = Color.FromArgb(0xE6, 0xE6, 0xD6);
    internal static readonly Color Muted = Color.FromArgb(0xB6, 0xC6, 0xB7);
    internal static readonly Color DisabledText = Color.FromArgb(0xA0, 0xB0, 0xA0);
    internal static readonly Color Good = Color.FromArgb(0xAC, 0xDC, 0x83);
    internal static readonly Color Warning = Color.FromArgb(0xF1, 0xC1, 0x75);
    internal static readonly Color Bad = Color.FromArgb(0xFF, 0xAD, 0x98);
    internal static readonly Color DestructiveHover = Color.FromArgb(0x50, 0x36, 0x30);
    internal static readonly Color LinkText = Color.FromArgb(0xA2, 0xD2, 0xA4);
    internal static readonly Color NavigationAccent = Color.FromArgb(0xB0, 0x8A, 0x5D);
#else
    internal static readonly Color Background = Color.FromArgb(33, 57, 66);      // #213942
    internal static readonly Color Panel = Color.FromArgb(25, 47, 55);           // #192F37
    internal static readonly Color Inset = Color.FromArgb(17, 35, 42);
    internal static readonly Color Raised = Color.FromArgb(44, 75, 85);          // #2C4B55
    internal static readonly Color Selected = Color.FromArgb(52, 92, 101);       // #345C65
    internal static readonly Color RaisedHover = Color.FromArgb(59, 98, 108);    // #3B626C
    internal static readonly Color Border = Color.FromArgb(82, 124, 132);        // #527C84
    internal static readonly Color ControlOutline = Border;
    internal static readonly Color Accent = Color.FromArgb(114, 208, 198);       // #72D0C6
    internal static readonly Color Muted = Color.FromArgb(201, 220, 222);        // #C9DCDE
    internal static readonly Color DisabledText = Color.FromArgb(167, 189, 195);
    internal static readonly Color Good = Color.FromArgb(145, 228, 184);         // #91E4B8
    internal static readonly Color Warning = Color.FromArgb(240, 208, 138);      // #F0D08A
    internal static readonly Color Bad = Color.FromArgb(243, 141, 133);          // #F38D85
    internal static readonly Color TextPrimary = Color.White;
    internal static readonly Color PrimaryFill = Accent;
    internal static readonly Color PrimaryText = Inset;
    internal static readonly Color TextBody = Color.WhiteSmoke;
    internal static readonly Color PrimaryHover = Color.FromArgb(138, 224, 215);
    internal static readonly Color PrimaryPressed = Color.FromArgb(81, 181, 174);
    internal static readonly Color DestructiveHover = Color.FromArgb(68, 43, 47);
    internal static readonly Color LinkText = Accent;
    internal static readonly Color ShellSurface = Panel;
    internal static readonly Color NavigationAccent = Accent;
#endif
    private static readonly string UiFontName = ResolveUiFontName();

    private static string ResolveUiFontName()
    {
        using var probe = new Font("Lexend", 9f);
        return probe.Name.Equals("Lexend", StringComparison.OrdinalIgnoreCase) ? "Lexend" : "Segoe UI";
    }

    private readonly bool _previewOnly;
    private readonly System.ComponentModel.Container _uiComponents = new();
    private readonly System.Windows.Forms.Timer _setupProgressTimer;

    private bool _interactiveResize;
    private bool _paintingSizeTransition;
    private int _sizeMessageDepth;
    private int _windowPositionDepth;
    private bool _pendingSizePaint;
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(nint window, nint updateRectangle, nint updateRegion, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        internal nint Window, InsertAfter;
        internal int X, Y, Width, Height;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowRectangle { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeWindowRectangle rectangle);

    private void PaintCompletedSize()
    {
        if (_paintingSizeTransition || !IsHandleCreated || !Visible || IsDisposed || Disposing
            || WindowState == FormWindowState.Minimized) return;
        _paintingSizeTransition = true;
        try
        {
            // Finish managed backgrounds, native children and their non-client
            // scrollbars at the completed size/layout boundary.
            RedrawWindow(Handle, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400);
            // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME
        }
        finally { _paintingSizeTransition = false; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Present completed descendant painting together, including native
            // ComboBox and RichEdit windows outside the local managed buffers.
            parameters.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return parameters;
        }
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        _interactiveResize = true;
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        _interactiveResize = false;
        base.OnResizeEnd(e);
        PaintCompletedSize();
    }

    protected override void WndProc(ref Message message)
    {
        var positionChange = message.Msg == 0x0047; // WM_WINDOWPOSCHANGED
        var sizeChange = message.Msg == 0x0005; // WM_SIZE
        if (positionChange) _windowPositionDepth++;
        if (sizeChange) _sizeMessageDepth++;
        try { base.WndProc(ref message); }
        finally
        {
            if (sizeChange) _sizeMessageDepth--;
            if (positionChange) _windowPositionDepth--;
        }
        if (message.Msg == 0x0046 && message.LParam != 0 && IsHandleCreated) // WM_WINDOWPOSCHANGING
        {
            var position = Marshal.PtrToStructure<NativeWindowPosition>(message.LParam);
            if (position.Window == Handle && (position.Flags & 0x0001) == 0 // SWP_NOSIZE
                && GetWindowRect(Handle, out var rectangle)
                && (position.Width != rectangle.Right - rectangle.Left || position.Height != rectangle.Bottom - rectangle.Top))
            {
                // A resized client needs newly painted child geometry rather
                // than pixels preserved from its previous width and height.
                position.Flags |= 0x0100; // SWP_NOCOPYBITS
                Marshal.StructureToPtr(position, message.LParam, false);
            }
        }
        // DefWindowProc sends WM_SIZE inside WM_WINDOWPOSCHANGED before WinForms
        // updates managed bounds and docks children. Paint at the completed
        // outer position dispatch, after that layout, rather than its nested size.
        if (sizeChange && message.WParam != 1)
        {
            if (_windowPositionDepth > 0) _pendingSizePaint = true;
            else if (_sizeMessageDepth == 0)
            {
                PaintCompletedSize();
            }
        }
        if (positionChange && _windowPositionDepth == 0 && WindowState != FormWindowState.Minimized
            && _pendingSizePaint)
        {
            _pendingSizePaint = false;
            PaintCompletedSize();
        }
    }

    public HubForm(string root, bool rememberLaunch = true, bool previewOnly = false)
        : this(root, rememberLaunch, previewOnly, null) { }

    internal HubForm(string root, bool rememberLaunch, bool previewOnly,
        Func<bool, Action<VrchatFaceStatus>, NativeIdleFeed>? nativeIdleFactory,
        Func<bool, VrchatFaceSelection, Action<VrchatFaceStatus>, VrchatFaceSession>? directSessionFactory = null,
        Action<string, string>? presentBindingGuide = null, string? faceOutputConfigDirectory = null,
        Func<bool>? competingFaceHost = null, Func<bool>? packageProvidesBackup = null)
    {
        _nativeIdleFactory = nativeIdleFactory ?? NativeIdleFeed.Open;
        _openDirectSession = directSessionFactory ?? OpenDirectSession;
        _presentBindingGuide = presentBindingGuide ?? ((title, instructions) =>
            MessageBox.Show(this, instructions, title, MessageBoxButtons.OK, MessageBoxIcon.Information));
        _previewOnly = previewOnly;
        _root = root;
        _faceOutputConfigDirectory = faceOutputConfigDirectory;
        _competingFaceHost = competingFaceHost ?? BackupModuleHostRunning;
        _packageProvidesBackup = packageProvidesBackup ?? (() => MemoryOnlyRoute || HubModuleInstallation.PackageIncludesBackup(_root));
        _environment = new HubEnvironment(root, loadUserSource: !previewOnly);
        _cameraPreview.Checked = _environment.CameraPreviewEnabled;
        _gaze.Checked = _environment.IndependentGazeEnabled;
        _scripts = new HubScriptFactory(root, _environment);
        _stopFile = Path.Combine(root, ".qpro-hub-stop");
        Text = $"{ProductDisplayName} V{AppVersion}";
        MinimumSize = new Size(780, 580);
        Size = new Size(1140, 850);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;
        BackColor = Background;
        ForeColor = TextBody;
        Font = new Font(UiFontName, 10F);
        StartPosition = FormStartPosition.CenterScreen;
        HandleCreated += (_, _) => EnableDarkTitleBar(Handle);
        Shown += (_, _) =>
        {
            var workArea = Screen.FromHandle(Handle).WorkingArea;
            if (Width <= workArea.Width && Height <= workArea.Height) return;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, workArea.Width), Math.Min(MinimumSize.Height, workArea.Height));
            Size = new Size(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
            if (!_previewOnly || StartPosition != FormStartPosition.Manual)
                Location = new Point(workArea.Left + (workArea.Width - Width) / 2, workArea.Top + (workArea.Height - Height) / 2);
        };

        SetStatus(_directVrchatStatus, StatusKind.Neutral, "VRChat connection not checked");
        using (new HubLayoutConstructionScope()) Controls.Add(BuildLayout());
        InitializeTrackingHealthUi();
        InitializeTrackingSourceUi();
        ConfigureOutputRouteControls();
        ConfigureSteamVrStartup();
        InitializeControllerInputUi();
        InitializeStreamerUpdaterUi();
        if (rememberLaunch && !previewOnly)
            Shown += (_, _) =>
            {
                try { _environment.MarkOpened(); }
                catch (Exception error) { AppendLog("Could not save first-launch state: " + error.Message); }
            };
        if (!previewOnly || nativeIdleFactory is not null) Shown += (_, _) => BeginNativeIdle();
        InitializeIntegratedSetup();
        _start.Click += async (_, _) => await StartTrackingAsync();
        _stop.Click += async (_, _) => await StopTrackingAsync();
        _hybridHands.CheckedChanged += (_, _) => UpdateControlState();
        _controllerTouchpad.CheckedChanged += (_, _) => UpdateControlState();
        _gaze.CheckedChanged += (_, _) =>
        {
            if (_previewOnly || _updatingMagiskGazeSelection) return;
            if (_gaze.Checked && HasActiveMagiskGaze)
            {
                ApplyMagiskGazeControlGate(!_trackingCleanupPending && !LiveTrackingRunning && !_starting && !_stopping);
                return;
            }
            try { _environment.SelectIndependentGaze(_gaze.Checked); }
            catch (Exception error) { AppendLog("Could not save independent gaze preference: " + error.Message); }
            UpdateGazeSetupStatus();
            UpdateControlState();
        };
        _tongue.CheckedChanged += (_, _) => UpdateControlState();
        _pupil.CheckedChanged += (_, _) => UpdateControlState();
        _cameraPreview.CheckedChanged += (_, _) =>
        {
            if (_previewOnly) return;
            try { _environment.SelectCameraPreview(_cameraPreview.Checked); }
            catch (Exception error) { AppendLog("Could not save camera preview preference: " + error.Message); }
            UpdateToggleStyle(_cameraPreview);
        };
        _tongueModels.SelectedIndexChanged += (_, _) => UpdateTongueModelNote();
        _fps.Items.AddRange(["12", "15", "18", "20", "24", "30", "36", "48", "60", "72"]);
        _fps.SelectedItem = "24";
        _processingProfile.Items.AddRange(HubProcessingProfile.Choices);
        _processingProfile.SelectedIndex = 0;
        _processingProfile.SelectedIndexChanged += (_, _) => { UpdateProcessingProfileNote(); UpdateControlState(); };
        UpdateProcessingProfileNote();
        _pupilSensitivity.Items.AddRange(Enumerable.Range(0, 11)
            .Select(index => $"{1.0 + index * 0.2:0.0}×" + (index == 2 ? " (Balanced)" : ""))
            .ToArray());
        _pupilSensitivity.SelectedIndex = 2;
        _visibilityMode.Items.AddRange(["Weighted camera + native", "Camera only", "Native only", "Conservative agreement"]);
        _visibilityMode.SelectedIndex = 0;
        _trackingStopSound.Checked = !_previewOnly && HubTrackingSoundPreference.Load();
        _trackingStopSound.CheckedChanged += (_, _) =>
        {
            if (_previewOnly) return;
            try { HubTrackingSoundPreference.Save(_trackingStopSound.Checked); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { AppendLog("Tracking sound preference could not be saved: " + error.Message); }
        };
        _individualCheekPuff.Checked = previewOnly || HubCheekPuffPreference.LoadMode() != HubCheekPuffMode.Off;
        void SaveCheekPuffChoice()
        {
            if (_previewOnly) { UpdateDirectFaceAdjustments(); return; }
            try
            {
                HubCheekPuffPreference.Save(_individualCheekPuff.Checked, HubCheekPuffMode.Strong);
                UpdateDirectFaceAdjustments();
                UpdateToggleStyle(_individualCheekPuff);
                AppendLog(_individualCheekPuff.Checked
                    ? "Individual cheek puff (1/0) selected for face tracking."
                    : "Individual cheek puff adjustment off.");
            }
            catch (Exception error) { AppendLog("Could not save cheek puff choice: " + error.Message); }
        }
        _individualCheekPuff.CheckedChanged += (_, _) => { SaveCheekPuffChoice(); UpdateControlState(); };
        _individualCheekSuck.Checked = previewOnly || HubCheekSuckPreference.LoadMode() != HubCheekSuckMode.Off;
        void SaveCheekSuckChoice()
        {
            if (_previewOnly) { UpdateDirectFaceAdjustments(); return; }
            try
            {
                HubCheekSuckPreference.Save(_individualCheekSuck.Checked, true);
                UpdateDirectFaceAdjustments();
                UpdateToggleStyle(_individualCheekSuck);
                AppendLog(_individualCheekSuck.Checked
                    ? "Individual cheek suck (1/0) selected for face tracking."
                    : "Individual cheek suck adjustment off.");
            }
            catch (Exception error) { AppendLog("Could not save cheek suck choice: " + error.Message); }
        }
        _individualCheekSuck.CheckedChanged += (_, _) => { SaveCheekSuckChoice(); UpdateControlState(); };
        EyebrowSettings eyebrowSettings = previewOnly ? EyebrowPreference.Default : EyebrowPreference.Load();
        _eyebrowSensitivity.Items.AddRange(Enumerable.Range(0, 11)
            .Select(index => $"{0.5 + index * 0.25:0.00}×" + (index == 2 ? " (Native)" : ""))
            .ToArray());
        _eyebrowSensitivity.SelectedIndex = Math.Clamp(
            (int)MathF.Round((eyebrowSettings.Sensitivity - 0.5f) / 0.25f), 0, 10);
        _eyebrowBoost.Checked = eyebrowSettings.Enabled;
        _eyebrowSensitivity.Enabled = _eyebrowBoost.Checked;
        void SaveEyebrowChoice()
        {
            if (_previewOnly) { UpdateDirectFaceAdjustments(); return; }
            try
            {
                float sensitivity = 0.5f + _eyebrowSensitivity.SelectedIndex * 0.25f;
                EyebrowPreference.Save(new EyebrowSettings(_eyebrowBoost.Checked, sensitivity));
                UpdateDirectFaceAdjustments();
                _eyebrowSensitivity.Enabled = _eyebrowBoost.Checked;
                UpdateToggleStyle(_eyebrowBoost);
                AppendLog(_eyebrowBoost.Checked
                    ? $"Eyebrow response on: {sensitivity:0.00}× during face tracking."
                    : "Eyebrow adjustment off: using the streaming app's original brow values.");
            }
            catch (Exception error) { AppendLog("Could not save eyebrow choice: " + error.Message); }
        }
        _eyebrowBoost.CheckedChanged += (_, _) => { SaveEyebrowChoice(); UpdateControlState(); };
        _eyebrowSensitivity.SelectedIndexChanged += (_, _) => SaveEyebrowChoice();
        ConfigureDropDown(_eyeProfiles);
        ConfigureDropDown(_tongueModels);
        ConfigureDropDown(_fps);
        ConfigureDropDown(_processingProfile);
        ConfigureDropDown(_pupilSensitivity);
        ConfigureDropDown(_visibilityMode);
        ConfigureDropDown(_eyebrowSensitivity);
        ConfigureDropDown(_quickDatasets);
        ConfigureDropDown(_focusedDatasets);
        ConfigureDropDown(_fullDatasets);
        ConfigureDropDown(_quickRecordedDatasets);
        ConfigureDropDown(_focusedRecordedDatasets);
        ConfigureDropDown(_fullRecordedDatasets);
        ConfigureModelList(_modelList);
        UpdateToggleStyle(_gaze);
        UpdateToggleStyle(_tongue);
        UpdateToggleStyle(_pupil);
        UpdateToggleStyle(_cameraPreview);
        UpdateToggleStyle(_individualCheekPuff);
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
            if (WindowState != FormWindowState.Minimized)
            {
                PulseSetupAttention();
                RefreshTrackingHealth();
            }
        };
        if (!previewOnly) pulseTimer.Start();
        _setupProgressTimer = new System.Windows.Forms.Timer(_uiComponents) { Interval = 45 };
        _setupProgressTimer.Tick += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized && _setupProgress.Visible && _setupProgress.IsIndeterminate)
                _setupProgress.AdvanceAnimation();
        };
        FormClosed += (_, _) =>
        {
            EndFaceOutput();
            timer.Stop(); pulseTimer.Stop(); _setupProgressTimer.Stop();
        };
        Disposed += (_, _) =>
        {
            // Dispose also covers a form abandoned before it was shown, where
            // FormClosed never fires. Native timers must not retain that form.
            _uiComponents.Dispose();
            EndFaceOutput();
            _soundPlayer?.Dispose();
            foreach (var process in _trackingProcesses) process.Dispose();
        };
        RestoreLiveOptions();
        WireUpdates();
        AppendLog(BackupSelected ? "Companion ready. VRCFaceTracking is the selected face output. Use First-time setup to check the backup module."
            : "Companion ready. Native face and eyes connect automatically when the streaming app shares tracking data and VRChat OSC is enabled. Use Start tracking for optional features.");
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
        if ((_previewOnly && !_nativeIdleShown) || _trackingSourceSelectionUpdating || steamLink == _environment.SteamLinkSelected) return;
        if (_faceRouteChanging || _setupActionRunning || _utilityActionRunning || _datasetOperationBusy ||
            _starting || _stopping || LiveTrackingRunning)
        {
            SyncTrackingSourceControls();
            return;
        }
        try
        {
            if (_previewOnly) _environment.SetPreviewTrackingSource(steamLink);
            else _environment.SelectTrackingSource(steamLink);
            SyncTrackingSourceControls();
            UpdateTrackingSourceNotes();
            var selectedName = steamLink ? "Steam Link" : "Virtual Desktop";
            AppendLog($"Face-tracking source selected: {selectedName}.");
            if (BackupSelected) { CancelVrchatCheck(); ShowOutputRouteState(); }
            RequestNativeIdleRefresh();
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
            ? "In SteamVR > Settings > Steam Link, show Advanced Settings, enable OSC, share eye and face tracking, and set Steam Link OSC output to 9015 (ALT). " +
              (BackupSelected ? "The Qpro module in VRCFaceTracking listens on this port." : "Companion listens on this port for Steam Link face data.")
            : "Enable eye and face tracking on the Quest and grant Virtual Desktop permission. In Virtual Desktop > Streaming, turn on Forward tracking data to PC.";
        _trackingSourceSetupNote.Text = note;
        _trackingSourceLiveNote.Text = note;
        UpdateControllerInputAvailability();
    }
}
