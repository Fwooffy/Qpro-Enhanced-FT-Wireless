using System.Diagnostics;
using System.Text.Json.Nodes;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private bool SelectedModelHasCameraCheeks() => _tongueModels.SelectedItem is FileChoice model &&
        HubModelMetadata.HasCameraCheeks(ReadModelMetadata(VersionFromPath(model.Primary)));

    private void UpdateCameraCheekAvailability()
    {
        bool running = LiveTrackingRunning;
        bool editable = !running && !_starting && !_stopping;
        bool available = SelectedModelHasCameraCheeks();
        // A stale checked choice stays editable so it can be turned off after
        // selecting a tongue-only model. Never enable an experimental output.
        _cameraCheekPuff.Enabled = editable && (available || _cameraCheekPuff.Checked);
        _cameraCheekSourceNote.Text = HubCameraTrackingLaunch.DescribeCheekSource(
            available, _cameraCheekPuff.Checked, running);
        _cameraCheekSourceNote.ForeColor = _cameraCheekPuff.Checked && !available ? Warning : Muted;
    }

    private void ReloadCameraCheekWorkflow()
    {
        string? parent = (_cheekCameraBaseModels.SelectedItem as FileChoice)?.Primary ??
            (_tongueModels.SelectedItem as FileChoice)?.Primary;
        string? selected = (_cheekCameraDatasets.SelectedItem as DatasetChoice)?.Dataset.SessionPath;
        _cheekCameraBaseModels.Items.Clear();
        foreach (FileChoice choice in _tongueModels.Items)
            _cheekCameraBaseModels.Items.Add(choice);
        SelectOrFirst(_cheekCameraBaseModels, parent);
        _cheekCameraDatasets.Items.Clear();
        string captures = Path.Combine(_root, "captures");
        if (Directory.Exists(captures))
        {
            foreach (string path in Directory.GetFiles(captures, "*.qpsession.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var session = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                    if (session?["sessionType"]?.GetValue<string>() != "cheek-stereo-stills-v1" ||
                        session["completed"]?.GetValue<bool>() != true) continue;
                    int count = (session["samples"] as JsonArray)?.Count ?? 0;
                    string capture = path[..^".qpsession.json".Length] + ".qpcap";
                    if (count == 0 || !File.Exists(capture)) continue;
                    string name = session["displayName"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path);
                    _cheekCameraDatasets.Items.Add(new DatasetChoice(new(path, capture, name, count, true)));
                }
                catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidOperationException)
                {
                    AppendLog($"Skipped unreadable cheek camera session {Path.GetFileName(path)}: {error.Message}");
                }
            }
        }
        for (int index = 0; index < _cheekCameraDatasets.Items.Count; index++)
            if (((DatasetChoice)_cheekCameraDatasets.Items[index]!).Dataset.SessionPath == selected)
                _cheekCameraDatasets.SelectedIndex = index;
        if (_cheekCameraDatasets.SelectedIndex < 0 && _cheekCameraDatasets.Items.Count > 0)
            _cheekCameraDatasets.SelectedIndex = 0;
        _cheekCameraDatasetNote.Text = _cheekCameraDatasets.Items.Count == 0
            ? "Record all guided poses first. Existing tongue models and native cheek calibration stay available."
            : $"{_cheekCameraDatasets.Items.Count} completed cheek camera dataset(s). Choose one and a parent tongue model to create a separate copy.";
    }

    private bool CameraCheekActionReady()
    {
        if (UtilityActionIsBusy()) return false;
        if (LiveTrackingRunning)
        {
            MessageBox.Show(this, "Stop Qpro tracking before recording or training cheek camera poses.",
                "Stop tracking first", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return true;
    }

    private async Task RecordCameraCheeksAsync()
    {
        if (!CameraCheekActionReady()) return;
        var missing = new List<string>();
        if (FindAdb() is null || !await HasQuestAsync()) missing.Add("connect and authorize the rooted Quest Pro");
        if (!Process.GetProcessesByName("vrserver").Any()) missing.Add("start SteamVR");
        if (!Process.GetProcessesByName("VRCFaceTracking").Any()) missing.Add("start VRCFaceTracking with the selected streaming app's face tracking moving");
        if (_environment.TrackingSourceRequiresVrcftRestart()) missing.Add("restart VRCFaceTracking after changing the streaming app");
        if (!BackendReady()) missing.Add("install the PC runtime");
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "Before recording:\n\n• " + string.Join("\n• ", missing), "Capture is not ready");
            return;
        }
        if (MessageBox.Show(this,
                "Record relaxed, separate left/right, both-cheek and mouth-control poses from the two face cameras. Follow each card and press Space to save stills. Q stops safely. Camera recordings stay in this extracted copy.\n\nThis is an experimental camera model; the native three-pose calibration remains available. Start recording?",
                "Record cheek camera poses", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (!CameraCheekActionReady()) return;
        _datasetOperationBusy = true;
        UpdateControlState();
        try
        {
            await RunUtilityAsync("Cheek camera capture", "build-and-run.ps1",
                "-TrackingSource", _environment.TrackingSourceArgument,
                "-CheekStillCalibration", "-Record", "-CameraMode", "mouth", "-MaxFps", "20");
        }
        finally
        {
            _datasetOperationBusy = false;
            ReloadCameraCheekWorkflow();
            UpdateControlState();
        }
    }

    private async Task TrainCameraCheeksAsync()
    {
        if (!CameraCheekActionReady()) return;
        if (!BackendReady())
        {
            MessageBox.Show(this, "Install the PC runtime in First-time setup before training camera cheeks.");
            return;
        }
        if (_cheekCameraDatasets.SelectedItem is not DatasetChoice choice ||
            _cheekCameraBaseModels.SelectedItem is not FileChoice parent || parent.Secondary is null)
        {
            MessageBox.Show(this, "Select a completed cheek camera dataset and a parent tongue model first.");
            return;
        }
        int version = HubModelPackage.NextVersion(Path.Combine(_root, "models"));
        _datasetOperationBusy = true;
        UpdateControlState();
        try
        {
            AppendLog($"Training experimental cheeks as a new copy of {parent.Label}. Parent tongue weights are preserved.");
            bool success = await RunUtilityAsync("Camera cheek training", "train-latest-cheeks.ps1",
                "-SessionPath", choice.Dataset.SessionPath, "-BaseModelPath", parent.Secondary,
                "-GateModelPath", parent.Primary, "-Version", version.ToString(),
                "-TrackingSource", _environment.TrackingSourceArgument);
            if (!success) return;
            string gate = Path.Combine(_root, "models", $"qpro-stereo-tongue-v{version}-gate.pt");
            string direction = Path.Combine(_root, "models", $"qpro-stereo-tongue-v{version}-direction.pt");
            if (!File.Exists(gate) || !File.Exists(direction))
            {
                AppendLog("Cheek training exited without a complete model pair. Check Activity before using this copy.");
                return;
            }
            ReloadProfiles();
            SelectOrFirst(_tongueModels, gate);
            UpdateTongueModelNote();
            AppendLog($"Experimental tongue + cheeks model v{version} selected. Camera cheek puff stays off until you enable it in Live tracking.");
            MessageBox.Show(this, "The experimental copy is ready. In Live tracking, keep this model selected, enable Camera cheek puff (experimental), then press Start tracking. Check gentle and full puffs on each side before relying on it.",
                "Experimental camera cheeks ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { _datasetOperationBusy = false; UpdateControlState(); }
    }
}
