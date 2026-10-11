using System.Diagnostics;
using System.Globalization;

namespace QproFaceTracking.Hub;

internal enum TrackingSignalState { Off, Stopped, Waiting, Live, Invalid, Stale, Error }

// Health comes from worker observations, not an ADB poll or a backend-selection
// message. The injected monotonic clock also makes socket stalls testable.
internal sealed class HubTrackingHealth
{
    private readonly Func<double> _now;
    private double? _cameraAt, _healthAt, _pupilAt;
    private double _cameraTimeout = 6.5;
    private string _tongue = "warming", _cheek = "warming", _pupil = "warming";
    private TrackingSignalState? _cameraEnded;
    internal bool Running { get; private set; }
    internal bool TongueSelected { get; private set; }
    internal bool CheekSelected { get; private set; }
    internal bool PupilSelected { get; private set; }
    internal bool CameraSelected => TongueSelected || CheekSelected || PupilSelected;
    internal string LowerFaceBackend { get; private set; } = "Waiting for backend";
    internal string PupilBackend { get; private set; } = "Waiting for backend";
    internal double? CameraFps { get; private set; }
    internal double? InferenceFps { get; private set; }
    internal double? FrameAgeMs { get; private set; }
    internal double? ResultAgeMs { get; private set; }
    internal double? PipelineMs { get; private set; }
    internal double? DecodeMs { get; private set; }
    internal double? PreprocessMs { get; private set; }
    internal double? InferenceMs { get; private set; }
    internal double? TransferMs { get; private set; }
    internal double? PupilMs { get; private set; }
    internal double? PupilAgeMs { get; private set; }
    internal long? DroppedFrames { get; private set; }
    internal long? PupilDroppedFrames { get; private set; }
    internal long? SourceSkips { get; private set; }
    internal double? QueueP95Ms { get; private set; }
    internal bool HasSnapshot => _healthAt.HasValue;
    internal bool SnapshotFresh => _healthAt.HasValue && _now() - _healthAt.Value <= 3.5;
    internal bool CameraFresh => CameraState == TrackingSignalState.Live;
    internal TrackingSignalState CameraState => !CameraSelected ? TrackingSignalState.Off
        : _cameraEnded ?? (!Running ? TrackingSignalState.Stopped
        : !_cameraAt.HasValue ? TrackingSignalState.Waiting
        : _now() - _cameraAt.Value > _cameraTimeout || CameraFps <= 0
            || FrameAgeMs + (_healthAt.HasValue ? (_now() - _healthAt.Value) * 1000 : 0) > 3500
            ? TrackingSignalState.Stale : TrackingSignalState.Live);

    internal HubTrackingHealth(Func<double>? now = null) => _now = now ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    internal void Select(bool tongue, bool cheek, bool pupil)
        => (TongueSelected, CheekSelected, PupilSelected) = (tongue, cheek, pupil);

    internal void Start(bool tongue, bool cheek, bool pupil)
    {
        Clear();
        Select(tongue, cheek, pupil);
        Running = true;
    }

    internal void Stop() { Clear(); Running = false; }

    internal void CameraExited(bool failed)
    {
        _cameraEnded = failed ? TrackingSignalState.Error : TrackingSignalState.Stopped;
        _cameraAt = _healthAt = _pupilAt = null;
    }

    private void Clear()
    {
        _cameraEnded = null;
        _cameraAt = _healthAt = _pupilAt = null;
        _cameraTimeout = 6.5;
        _tongue = _cheek = _pupil = "warming";
        LowerFaceBackend = PupilBackend = "Waiting for backend";
        CameraFps = InferenceFps = FrameAgeMs = ResultAgeMs = PipelineMs = DecodeMs = PreprocessMs = InferenceMs = TransferMs = PupilMs = PupilAgeMs = QueueP95Ms = null;
        DroppedFrames = PupilDroppedFrames = SourceSkips = null;
    }

    internal TrackingSignalState FeatureState(string feature)
    {
        var selected = feature switch { "tongue" => TongueSelected, "cheek" => CheekSelected, "pupil" => PupilSelected, _ => false };
        if (!selected) return TrackingSignalState.Off;
        if (_cameraEnded.HasValue) return _cameraEnded.Value;
        if (!Running) return TrackingSignalState.Stopped;
        if (CameraState == TrackingSignalState.Stale) return TrackingSignalState.Stale;
        var value = feature switch { "tongue" => _tongue, "cheek" => _cheek, _ => _pupil };
        var at = feature == "pupil" ? _pupilAt : _healthAt;
        if (!at.HasValue) return TrackingSignalState.Waiting;
        if (_now() - at.Value > 3.5 || (feature == "pupil" ? PupilAgeMs : ResultAgeMs) + (_now() - at.Value) * 1000 > 3500)
            return TrackingSignalState.Stale;
        return value switch
        {
            "visible" or "hidden" or "valid" => TrackingSignalState.Live,
            "stale" => TrackingSignalState.Stale,
            "invalid" => TrackingSignalState.Invalid,
            _ => TrackingSignalState.Waiting,
        };
    }

    internal TrackingSignalState LowerFaceState
    {
        get
        {
            var selected = new List<TrackingSignalState>(2);
            if (TongueSelected) selected.Add(FeatureState("tongue"));
            if (CheekSelected) selected.Add(FeatureState("cheek"));
            return selected.Count == 0 ? TrackingSignalState.Off
                : selected.Contains(TrackingSignalState.Error) ? TrackingSignalState.Error
                : selected.Contains(TrackingSignalState.Stale) ? TrackingSignalState.Stale
                : selected.All(state => state == TrackingSignalState.Live) ? TrackingSignalState.Live
                : selected.All(state => state == TrackingSignalState.Stopped) ? TrackingSignalState.Stopped
                : TrackingSignalState.Waiting;
        }
    }

    internal string LowerFaceStatusText => LowerFaceState switch
    {
        TrackingSignalState.Live => "Live",
        TrackingSignalState.Stale => "Stale output",
        TrackingSignalState.Waiting => "Waiting for output",
        _ => LowerFaceState.ToString(),
    };

    internal double? SnapshotAgeSeconds => _healthAt.HasValue ? Math.Max(0, _now() - _healthAt.Value) : null;

    internal string DiagnosticText()
    {
        static string Metric(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unavailable";
        return $"Camera health: {CameraState}; tongue: {FeatureState("tongue")}; camera cheeks: {FeatureState("cheek")}; pupils: {FeatureState("pupil")}\n"
            + $"Lower-face backend: {LowerFaceBackend}; pupil backend: {PupilBackend}\n"
            + $"Last camera FPS: {Metric(CameraFps)}; inference FPS: {Metric(InferenceFps)}; processing delay ms: {Metric(PipelineMs)}; snapshot age seconds: {Metric(SnapshotAgeSeconds)}\n"
            + $"Last PC frame age ms: {Metric(FrameAgeMs)}; result age ms: {Metric(ResultAgeMs)}; pupil age ms: {Metric(PupilAgeMs)}\n"
            + $"Queue drops: {DroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}; pupil drops: {PupilDroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}; source skips: {SourceSkips?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}";
    }

    internal static bool IsTelemetry(string line) => line.StartsWith("TRACKING_HEALTH ", StringComparison.Ordinal)
        || line.StartsWith("TRANSPORT_QUALITY ", StringComparison.Ordinal)
        || line.StartsWith("INFERENCE_BACKEND ", StringComparison.Ordinal)
        || line.StartsWith("PUPIL_STATUS ", StringComparison.Ordinal);

    internal bool Observe(string line, double? receivedAt = null)
    {
        if (!Running || _cameraEnded.HasValue || !IsTelemetry(line) || line.Length > 4096) return false;
        var observedAt = receivedAt ?? _now();
        if (!double.IsFinite(observedAt) || observedAt > _now()) return false;
        var fields = Fields(line);
        if (line.StartsWith("INFERENCE_BACKEND ", StringComparison.Ordinal))
        {
            if (!fields.TryGetValue("feature", out var feature) || !fields.TryGetValue("backend", out var backend)
                || !fields.TryGetValue("device", out var device)) return false;
            var nameAt = line.IndexOf(" name=", StringComparison.Ordinal);
            var name = nameAt < 0 ? device : line[(nameAt + 6)..].Trim();
            var description = backend switch { "amd-rocm" => "AMD ROCm", "nvidia-cuda" => "NVIDIA CUDA", "cpu" => "CPU", _ => backend };
            var value = description + " · " + (string.IsNullOrEmpty(name) ? device : name);
            if (feature == "tongue") LowerFaceBackend = value;
            else if (feature == "pupil") PupilBackend = value;
            else return false;
            return true;
        }
        if (line.StartsWith("TRANSPORT_QUALITY ", StringComparison.Ordinal))
        {
            if (!Number(fields, "fps", out var fps)) return false;
            // The legacy report arrives every five seconds. Once the new
            // one-second health stream is present it owns camera freshness.
            if (!_healthAt.HasValue) { CameraFps = fps; _cameraAt = observedAt; }
            SourceSkips = Count(fields, "source_skips");
            QueueP95Ms = Optional(fields, "extra_queue_p95_ms");
            return true;
        }
        if (line.StartsWith("PUPIL_STATUS ", StringComparison.Ordinal))
        {
            if (!fields.TryGetValue("left", out var left) || !fields.TryGetValue("right", out var right)) return false;
            var leftValid = double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && double.IsFinite(l) && l >= 0;
            var rightValid = double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) && r >= 0;
            _pupil = leftValid && rightValid ? "valid" : "invalid";
            _pupilAt = observedAt;
            PupilMs = Optional(fields, "processing_ms");
            PupilAgeMs = Optional(fields, "age_ms");
            PupilDroppedFrames = Count(fields, "dropped_frames");
            return true;
        }
        if (!Number(fields, "camera_fps", out var cameraFps) || !Number(fields, "inference_fps", out var inferenceFps)
            || !Number(fields, "frame_age_ms", out var frameAge) || !Timing(fields, "pipeline_ms", out var pipeline)
            || Count(fields, "dropped_frames") is not { } dropped
            || !Signal(fields, "tongue", out var tongue) || !Signal(fields, "cheek", out var cheek) || !Signal(fields, "pupil", out var pupil)) return false;
        if (_healthAt.HasValue && observedAt < _healthAt.Value) return false;
        _healthAt = _cameraAt = observedAt;
        _cameraTimeout = 3.5;
        CameraFps = cameraFps; InferenceFps = inferenceFps; FrameAgeMs = frameAge; PipelineMs = pipeline;
        ResultAgeMs = Optional(fields, "result_age_ms");
        DecodeMs = Optional(fields, "decode_ms"); PreprocessMs = Optional(fields, "preprocess_ms");
        InferenceMs = Optional(fields, "inference_ms"); TransferMs = Optional(fields, "transfer_ms");
        DroppedFrames = dropped; _tongue = tongue; _cheek = cheek; _pupil = pupil;
        _pupilAt = observedAt;
        PupilMs = Optional(fields, "pupil_ms"); PupilAgeMs = Optional(fields, "pupil_age_ms");
        PupilDroppedFrames = Count(fields, "pupil_dropped_frames");
        return true;
    }

    private static Dictionary<string, string> Fields(string line)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equal = token.IndexOf('=');
            if (equal > 0) values[token[..equal]] = token[(equal + 1)..];
        }
        return values;
    }
    private static bool Number(Dictionary<string, string> fields, string key, out double value)
    {
        value = 0;
        return fields.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0;
    }
    private static double? Optional(Dictionary<string, string> fields, string key) => Number(fields, key, out var value) ? value : null;
    private static bool Timing(Dictionary<string, string> fields, string key, out double? value)
    {
        value = Optional(fields, key);
        // Workers use -1 when a feature has not produced a timed result yet.
        return value.HasValue || !fields.TryGetValue(key, out var text)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == -1;
    }
    private static long? Count(Dictionary<string, string> fields, string key) => fields.TryGetValue(key, out var text)
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static bool Signal(Dictionary<string, string> fields, string key, out string value)
    {
        value = fields.GetValueOrDefault(key, "");
        return value is "visible" or "hidden" or "valid" or "invalid" or "stale" or "off" or "warming";
    }
}

internal sealed partial class HubForm
{
    private readonly HubTrackingHealth _trackingHealth = new();
    private readonly Label _healthBackend = Info("GPU/backend: tracking stopped");
    private readonly Label _healthRates = Info("Camera — FPS · inference — FPS · processing delay — ms");
    private readonly Label _healthTiming = Info("PC frame age — ms · result age — ms");
    private readonly Label _healthDrops = Info("Queue drops — · source skips —");
    private readonly Label _healthFeatures = Info("Tongue: off · camera cheeks: off · pupils: off");
    private readonly Label _healthNote = Info("Live health appears when camera tracking starts.");
    private readonly Label _healthStages = Info("Decode — ms · prepare — ms · prediction total — ms · GPU wait/readback — ms");
    private readonly Label _healthPupil = Info("Pupil processing — ms · pupil result age — ms · queue delay p95 — ms");
    private TableLayoutPanel? _trackingHealthCard;

    private Control BuildTrackingHealthCard()
    {
        var card = Card();
        _trackingHealthCard = card;
        card.AccessibleName = "Live tracking health";
        card.ColumnCount = 1;
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(SectionTitle("Live tracking health", HubIcon.Activity));
        foreach (var label in new[] { _healthRates, _healthFeatures, _healthModule, _healthNote })
        {
            label.Margin = new Padding(0, 0, 0, 6);
            label.AccessibleName = label.Text;
            card.Controls.Add(label);
        }
        _healthFeatures.Font = new System.Drawing.Font(UiFontName, 10F, System.Drawing.FontStyle.Bold);
        _healthNote.Font = new System.Drawing.Font(UiFontName, 9F);
        _healthNote.Visible = false;
        var details = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Visible = false, Margin = Padding.Empty };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.Controls.Add(_healthBackend);
        details.Controls.Add(_healthTiming);
        details.Controls.Add(_healthDrops);
        details.Controls.Add(_healthStages);
        details.Controls.Add(_healthPupil);
        details.Controls.Add(_healthModuleDetail);
        details.Controls.Add(Info("Prediction includes preparation and GPU wait/readback. These timings overlap and cannot be added to estimate total latency."));
        var disclosure = ActionButton("Show tracking details", (_, _) => { });
        disclosure.Click += (_, _) =>
        {
            details.Visible = !details.Visible;
            disclosure.Text = details.Visible ? "Hide tracking details" : "Show tracking details";
            disclosure.AccessibleName = disclosure.Text;
            RefreshTrackingHealth();
        };
        card.Controls.Add(disclosure);
        card.Controls.Add(details);
        card.VisibleChanged += (_, _) => RefreshTrackingHealth();
        return card;
    }

    private void InitializeTrackingHealthUi()
    {
        foreach (var toggle in new[] { _tongue, _cameraCheekPuff, _pupil })
            toggle.CheckedChanged += (_, _) =>
            {
                if (!_trackingHealth.Running) _trackingHealth.Select(_tongue.Checked, _cameraCheekPuff.Checked, _pupil.Checked);
                RefreshTrackingHealth();
            };
        Resize += (_, _) => { if (WindowState != FormWindowState.Minimized) RefreshTrackingHealth(); };
        ResizeEnd += (_, _) => RefreshTrackingHealth();
        _trackingHealth.Select(_tongue.Checked, _cameraCheekPuff.Checked, _pupil.Checked);
        RefreshTrackingHealth();
    }

    private void BeginTrackingHealth()
    {
        _trackingHealth.Start(_tongue.Checked, _cameraCheekPuff.Checked, _pupil.Checked);
        _cameraInputReady = false;
        RefreshTrackingHealth();
    }

    private void StopTrackingHealth()
    {
        _trackingHealth.Stop();
        _cameraInputReady = false;
        RefreshTrackingHealth();
    }

    private void RefreshTrackingHealth()
    {
        if (IsDisposed || Disposing || WindowState == FormWindowState.Minimized) return;
        RefreshModuleHeartbeatDecoration();
        var cameraFresh = _trackingHealth.CameraFresh;
        if (_cameraInputReady != cameraFresh)
        {
            _cameraInputReady = cameraFresh;
            if (!_starting && _trackingHealth.Running && !(_trackingCleanupPending || _stopping || _closingInProgress)
                && _trackingHealth.CameraState is not (TrackingSignalState.Error or TrackingSignalState.Stopped))
                UpdateSessionReadiness();
        }
        // Keep observations and the footer's freshness authoritative during a
        // corner drag. Optional metric text can settle once the drag finishes.
        if (_interactiveResize || _trackingHealthCard?.Visible != true) return;
        static string Number(double? value, string format = "0.0") => value?.ToString(format, CultureInfo.InvariantCulture) ?? "—";
        static string State(TrackingSignalState state) => state switch
        {
            TrackingSignalState.Live => "live", TrackingSignalState.Invalid => "invalid / warming",
            TrackingSignalState.Waiting => "waiting", _ => state.ToString().ToLowerInvariant(),
        };
        void Label(Label label, string text, System.Drawing.Color color)
        {
            if (label.Text != text) { label.Text = text; label.AccessibleDescription = text; }
            if (label.ForeColor != color) label.ForeColor = color;
        }
        var camera = _trackingHealth.CameraState;
        var fresh = camera == TrackingSignalState.Live;
        var metricsCurrent = fresh && _trackingHealth.SnapshotFresh;
        var color = camera == TrackingSignalState.Error ? Bad : fresh ? Good
            : camera is TrackingSignalState.Off or TrackingSignalState.Stopped ? Muted : Warning;
        var backend = !_trackingHealth.Running || !_trackingHealth.CameraSelected ? "tracking stopped"
            : (_trackingHealth.TongueSelected || _trackingHealth.CheekSelected ? _trackingHealth.LowerFaceBackend : "")
                + (_trackingHealth.PupilSelected && (!(_trackingHealth.TongueSelected || _trackingHealth.CheekSelected)
                    || _trackingHealth.PupilBackend != _trackingHealth.LowerFaceBackend)
                    ? (_trackingHealth.TongueSelected || _trackingHealth.CheekSelected ? " · pupils: " : "pupils: ") + _trackingHealth.PupilBackend : "");
        Label(_healthBackend, "GPU/backend: " + backend, Muted);
        Label(_healthRates, $"Camera {(fresh ? Number(_trackingHealth.CameraFps) : "—")} FPS · inference {(metricsCurrent ? Number(_trackingHealth.InferenceFps) : "—")} FPS · processing delay {(metricsCurrent ? Number(_trackingHealth.PipelineMs) : "—")} ms", color);
        Label(_healthTiming, $"PC frame age {(metricsCurrent ? Number(_trackingHealth.FrameAgeMs) : "—")} ms · result age {(metricsCurrent ? Number(_trackingHealth.ResultAgeMs) : "—")} ms", Muted);
        if (_healthStages.Visible)
        {
            Label(_healthStages, $"Decode {(metricsCurrent ? Number(_trackingHealth.DecodeMs) : "—")} ms · prepare {(metricsCurrent ? Number(_trackingHealth.PreprocessMs) : "—")} ms · prediction total {(metricsCurrent ? Number(_trackingHealth.InferenceMs) : "—")} ms · GPU wait/readback {(metricsCurrent ? Number(_trackingHealth.TransferMs) : "—")} ms", Muted);
            Label(_healthPupil, $"Pupil processing {(metricsCurrent ? Number(_trackingHealth.PupilMs) : "—")} ms · pupil result age {(metricsCurrent ? Number(_trackingHealth.PupilAgeMs) : "—")} ms · queue delay p95 {Number(_trackingHealth.QueueP95Ms)} ms", Muted);
        }
        Label(_healthDrops, $"Queue drops {_trackingHealth.DroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? "—"} · pupil drops {_trackingHealth.PupilDroppedFrames?.ToString(CultureInfo.InvariantCulture) ?? "—"} · source skips {_trackingHealth.SourceSkips?.ToString(CultureInfo.InvariantCulture) ?? "—"}", Muted);
        var states = new[] { _trackingHealth.FeatureState("tongue"), _trackingHealth.FeatureState("cheek"), _trackingHealth.FeatureState("pupil") };
        var featureColor = states.Contains(TrackingSignalState.Error) ? Bad
            : states.Any(state => state is TrackingSignalState.Stale or TrackingSignalState.Invalid or TrackingSignalState.Waiting) ? Warning
            : states.Contains(TrackingSignalState.Live) ? Good : Muted;
        Label(_healthFeatures, $"Tongue: {State(states[0])} · camera cheeks: {State(states[1])} · pupils: {State(states[2])}", featureColor);
        var note = camera switch
        {
            TrackingSignalState.Stale => "Camera input is stale. Keep the headset awake; check the connection and Activity.",
            TrackingSignalState.Error => "Camera worker failed. Check Activity for the error before restarting.",
            TrackingSignalState.Waiting => "Waiting for camera frames. Selecting a GPU does not confirm live input.",
            TrackingSignalState.Stopped => "Camera tracking stopped. Start tracking to see live health.",
            TrackingSignalState.Off => "Camera features are off. Enable tongue, camera cheeks or pupils to see live health.",
            _ => states.Contains(TrackingSignalState.Stale) ? "A camera feature has stale output. Check Activity before relying on it."
                : states.Contains(TrackingSignalState.Invalid) ? "Pupils are invalid or warming. Hold steady and check Activity if this continues."
                : states.Contains(TrackingSignalState.Waiting) ? "Camera frames are live. Waiting for selected model outputs to warm up."
                : "Processing delay is measured on this PC. Check tracking output in VRCFaceTracking.",
        };
        var showWarning = camera is TrackingSignalState.Stale or TrackingSignalState.Error or TrackingSignalState.Waiting
            || states.Any(state => state is TrackingSignalState.Stale or TrackingSignalState.Error or TrackingSignalState.Invalid or TrackingSignalState.Waiting);
        if (_healthNote.Visible != showWarning) _healthNote.Visible = showWarning;
        Label(_healthNote, note, fresh ? featureColor : color);
        var accessible = _healthBackend.Text + ". " + _healthTiming.Text + ". " + _healthDrops.Text;
        if (_trackingHealthCard.AccessibleDescription != accessible) _trackingHealthCard.AccessibleDescription = accessible;
        if (_trackingHealth.HasSnapshot)
            _healthTiming.AccessibleDescription = $"Decode {Number(_trackingHealth.DecodeMs)} ms; preprocess {Number(_trackingHealth.PreprocessMs)} ms; prediction total {Number(_trackingHealth.InferenceMs)} ms; GPU wait/readback {Number(_trackingHealth.TransferMs)} ms. Prediction includes preparation and GPU wait/readback, so timings cannot be added. These are PC observations, not headset-to-avatar latency.";
        if (!_stopping && !_starting && _trackingHealth.Running)
        {
            if (_trackingHealth.TongueSelected || _trackingHealth.CheekSelected)
                SetStatus(_inferenceStatus, _trackingHealth.LowerFaceState == TrackingSignalState.Live ? StatusKind.Good
                    : _trackingHealth.LowerFaceState == TrackingSignalState.Error ? StatusKind.Bad : StatusKind.Warning,
                    _trackingHealth.LowerFaceStatusText);
            if (_trackingHealth.PupilSelected)
                SetStatus(_pupilStatus, states[2] == TrackingSignalState.Live ? StatusKind.Good : StatusKind.Warning, State(states[2]));
        }
    }

    internal void ApplyTrackingHealthPreview()
    {
        if (!_previewOnly) throw new InvalidOperationException("Health sample data requires preview mode.");
        _tongue.Checked = _cameraCheekPuff.Checked = _pupil.Checked = true;
        BeginTrackingHealth();
        _trackingHealth.Observe("INFERENCE_BACKEND feature=tongue backend=nvidia-cuda device=cuda:0 name=NVIDIA RTX 4070 · sample");
        _trackingHealth.Observe("INFERENCE_BACKEND feature=pupil backend=nvidia-cuda device=cuda:0 name=NVIDIA RTX 4070 · sample");
        _trackingHealth.Observe("TRANSPORT_QUALITY fps=24 source_skips=3 extra_queue_p95_ms=2.4");
        _trackingHealth.Observe("TRACKING_HEALTH camera_fps=23.8 inference_fps=23.6 frame_age_ms=4.1 result_age_ms=1.2 pipeline_ms=8.6 decode_ms=0.7 preprocess_ms=1.0 inference_ms=5.4 transfer_ms=0.5 dropped_frames=2 pupil_dropped_frames=1 pupil_age_ms=40 pupil_ms=7.2 tongue=hidden cheek=valid pupil=valid");
        RefreshTrackingHealth();
        _runStatus.Text = "● Preview — sample health data, no live tracking";
        _runStatus.ForeColor = Muted;
    }
}
