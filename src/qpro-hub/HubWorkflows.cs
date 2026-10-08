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

internal sealed partial class HubForm
{
    // Setup, capture, and training all use the same private Python runtime.
    // Keep their child processes mutually exclusive so setup cannot replace
    // a virtual environment while a trainer is using it.
    private bool _utilityActionRunning;
    private bool _gazeRecoveryRunning;
    private bool _trackingCleanupPending;
    private CheekTrackingSession? _cheekTrackingSession;
    private bool _nativeCheekOnlySession;
    private bool LiveTrackingRunning => _cheekTrackingSession is not null || _trackingProcesses.Any(p => !p.HasExited);
    private bool NativeCheekAdjustmentsSelected => _individualCheekPuff.Checked || _individualCheekSuck.Checked;
    private bool WorkerTrackingSelected => _gaze.Checked || _tongue.Checked || _cameraCheekPuff.Checked || _pupil.Checked || _hybridHands.Checked || _controllerTouchpad.Checked;

    private void EndCheekTrackingSession()
    {
        _cheekTrackingSession?.Dispose();
        _cheekTrackingSession = null;
        _nativeCheekOnlySession = false;
    }

    private bool UtilityActionIsBusy(bool allowTrackingTransition = false)
    {
        if (_closingInProgress) return true;
        if (!allowTrackingTransition && (_starting || _stopping))
        {
            SetActionFeedback("Tracking is changing state", "Wait for tracking startup or cleanup to finish before starting another action.", "Press Stop tracking to cancel startup, then wait for cleanup.");
            return true;
        }
        if (_trackingCleanupPending)
        {
            MessageBox.Show(this, "Tracking cleanup has not finished. Press Stop tracking again and check Activity before starting another action or closing the Hub.",
                "Tracking cleanup is pending", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }
        if (_gazeRecoveryRunning)
        {
            MessageBox.Show(this, "Wait for the stock eye-model recovery to finish.",
                "Qpro is restoring tracking", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        if (!allowTrackingTransition && LiveTrackingRunning)
        {
            MessageBox.Show(this, "Stop Qpro tracking before starting setup, capture, or training.",
                "Stop tracking first", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        if (!_utilityActionRunning && !_setupActionRunning && !_datasetOperationBusy)
            return false;
        MessageBox.Show(this, "Wait for the current setup, capture, or training action to finish.",
            "Qpro is busy", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
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
            foreach (var gate in Directory.GetFiles(modelsDir, "qpro-stereo-tongue-v*-gate.pt")
                         .OrderBy(path => IsExperimentalTongueModel(VersionFromPath(path)))
                         .ThenByDescending(VersionFromPath))
            {
                var version = VersionFromPath(gate);
                var direction = Path.Combine(modelsDir, $"qpro-stereo-tongue-v{version}-direction.pt");
                if (File.Exists(direction))
                {
                    var metadata = ReadModelMetadata(version);
                    var choice = new FileChoice(ModelDisplayName(version), gate, direction,
                        IsExperimentalModelMetadata(metadata), HasMoustacheModelIcon(metadata));
                    _tongueModels.Items.Add(choice);
                    _modelList.Items.Add(choice);
                }
            }
        }
        SelectOrFirst(_tongueModels, selectedTongue);
        SelectOrFirst(_modelList, selectedManaged);
        _modelEmpty.Visible = _modelList.Items.Count == 0;
        if (_modelEmpty.Visible) _modelEmpty.BringToFront();
        UpdateTongueModelNote();
        ReloadDatasetQueues();
        ReloadCameraCheekWorkflow();
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
        _tongueModelNote.Text = model?.HasMoustacheIcon == true
            ? "Highly experimental: includes the Mustachio beard/moustache training branch. Independent clean-shaven validation is pending. The developer v8 demo remains available."
            : model?.IsExperimental == true
                ? "Highly experimental tongue model. Independent wearer validation is pending. The developer v8 demo remains available."
                : version == 8
            ? "Trained only on the developer. It is suitable for a first demo; quick refinement is recommended for another wearer."
            : model is null
                ? "No complete gate/direction model pair was found."
                : "Personal model discovered in this release folder. The bundled developer v8 remains unchanged.";
        _tongueModelNote.Text += " Stop tracking before changing models, then press Start tracking to load the selection.";
        if (SelectedModelHasCameraCheeks())
            _tongueModelNote.Text += " This experimental copy also has camera cheek puff outputs; enable Camera cheek puff separately to use them.";
        UpdateCameraCheekAvailability();
    }

    private async Task ConfirmCaptureAsync(TongueDatasetKind kind)
    {
        if (UtilityActionIsBusy()) return;
        var missing = new List<string>();
        if (FindAdb() is null) missing.Add("re-extract the release; bundled platform-tools\\adb.exe is missing");
        else if (!await HasQuestAsync()) missing.Add("connect and authorize the rooted Quest Pro over USB or wireless ADB");
        if (!Process.GetProcessesByName("vrserver").Any()) missing.Add("start SteamVR");
        if (!Process.GetProcessesByName("VRCFaceTracking").Any()) missing.Add($"start VRCFaceTracking and confirm {(_environment.SteamLinkSelected ? "Steam Link" : "Virtual Desktop")} face tracking is flowing");
        else if (_environment.TrackingSourceRequiresVrcftRestart()) missing.Add("close and reopen VRCFaceTracking so its module loads the selected face-tracking source");
        if (!BackendReady()) missing.Add("run First-time setup: Set up PC runtime");
        if (_closingInProgress) return;
        if (missing.Count > 0)
        {
            MessageBox.Show(
                this,
                "Before recording:\n\n• " + string.Join("\n• ", missing) +
                "\n\nThe trainer uses the selected streaming app's native TongueOut confidence as a reference label, so SteamVR and VRCFaceTracking are required during capture.",
                "Capture is not ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        var title = kind switch
        {
            TongueDatasetKind.Quick => "Start quick lower-face refinement?",
            TongueDatasetKind.Focused => "Start focused tongue capture?",
            _ => "Start full lower-face capture?",
        };
        var estimate = kind switch
        {
            TongueDatasetKind.Quick => "about 15–30 minutes",
            TongueDatasetKind.Focused => "about 15–30 minutes",
            _ => "about 60–120 minutes",
        };
        var purpose = kind switch
        {
            TongueDatasetKind.Quick => "This refines the selected tongue model and records 21 cheek camera cards. Training creates a separate tongue + cheeks copy. Camera cheeks remain experimental and are enabled separately in Live tracking.",
            TongueDatasetKind.Focused => "This records fixed diagonal tongue poses plus matched tongue-hidden and tongue-visible poses with facial hair. Keep the tongue visible in both camera views for each visible card. It fine-tunes a new model without replacing your other captures or models.",
            _ => "This records broad tongue coverage and 21 cheek camera cards. Training creates a new personal lower-face copy. It requires many carefully held poses; camera cheeks remain experimental and are enabled separately in Live tracking.",
        };
        var choice = MessageBox.Show(
            this,
            $"Estimated capture time: {estimate}.\n\n{purpose}\n\nA guided camera window will open. Press Q at any point to stop safely. Existing captures and models will not be overwritten.\n\nStart now?",
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes) return;
        if (UtilityActionIsBusy()) return;
        _datasetOperationBusy = true;
        UpdateControlState();
        try
        {
            var started = DateTime.UtcNow;
            var succeeded = await RunUtilityAsync(
                kind switch
                {
                    TongueDatasetKind.Quick => "Quick refinement capture",
                    TongueDatasetKind.Focused => "Focused tongue capture",
                    _ => "Full lower-face capture",
                },
                "build-and-run.ps1",
                "-TrackingSource", _environment.TrackingSourceArgument,
                kind switch
                {
                    TongueDatasetKind.Quick => "-TongueRefinementCalibration",
                    TongueDatasetKind.Focused => "-TongueArcCalibration",
                    _ => "-TongueStillCalibration",
                });
            if (!succeeded) return;
            var dataset = FindLatestDataset(kind, requireCompleted: false, newerThan: started.AddSeconds(-3));
            if (dataset is null || dataset.SampleCount == 0) return;
            var proposed = dataset.DisplayName.StartsWith("Dataset ", StringComparison.Ordinal)
                ? kind switch
                {
                    TongueDatasetKind.Quick => "My lower-face refinement",
                    TongueDatasetKind.Focused => "My diagonal and facial hair refinement",
                    _ => "My full lower-face dataset",
                }
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
        catch (Exception error) { ShowWorkflowFailure("Capture result", error); }
        finally { _datasetOperationBusy = false; UpdateControlState(); }
    }

    private async Task RunSetupStepAsync(string label, string script, string completed, string next, params string[] args)
    {
        if (!TryBeginSetupProgress(label)) return;
        var succeeded = false;
        try { succeeded = await RunUtilityAsync(label, script, args); }
        catch (Exception error) { ShowWorkflowFailure(label, error); }
        finally
        {
            if (script.Equals("Install-QproRocm.ps1", StringComparison.OrdinalIgnoreCase)) _rocmInstallRunning = false;
            FinishSetupProgress(succeeded, label);
        }
        if (!succeeded) return;
        UpdateSetupStepStyles();
        PlaySfx("succeed.wav");
        MessageBox.Show(this, completed + "\n\n" + next, "Setup step complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task InstallQproModuleAsync(bool steamLink)
    {
        var sourceName = steamLink ? "Steam Link" : "Virtual Desktop";
        if (VrcftModuleProcessRunning())
        {
            MessageBox.Show(this,
                $"Close VRCFaceTracking and wait for its ModuleProcess helper to exit, then press Install {sourceName} module again. Qpro will not close them for you.",
                "Close VRCFaceTracking", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var label = $"Install {sourceName} module";
        if (!TryBeginSetupProgress(label)) return;
        var succeeded = false;
        try
        {
            succeeded = await RunUtilityAsync(label, "install-vrcft-eye-bridge.ps1",
                "-TrackingSource", steamLink ? "SteamLink" : "VirtualDesktop");
            if (succeeded)
            {
                _environment.ReloadTrackingSource();
                succeeded = _environment.SteamLinkSelected == steamLink && CurrentBridgeInstalled();
                if (!succeeded)
                    throw new InvalidOperationException("The module helper completed, but the selected source and installed module could not be verified. Retry with VRCFaceTracking closed.");
                SyncTrackingSourceControls();
                UpdateTrackingSourceNotes();
            }
        }
        catch (Exception error) { succeeded = false; ShowWorkflowFailure(label, error); }
        finally { FinishSetupProgress(succeeded, label); }
        if (!succeeded) return;
        UpdateSetupStepStyles();
        await RefreshStatusAsync();
        AppendLog($"The Qpro {sourceName} module is active on disk; the other Qpro source module was removed. Restart VRCFaceTracking to load it.");
        PlaySfx("succeed.wav");
        MessageBox.Show(this,
            $"The Qpro {sourceName} module is installed. The other Qpro source module was removed.\n\nStart VRCFaceTracking, then press Check gaze setup and follow its next step if you want independent gaze.",
            "Setup step complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        if (_closingInProgress) return;
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
        if (_closingInProgress) return;
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
            $"Start {(_environment.SteamLinkSelected ? "Steam Link" : "Virtual Desktop")}, SteamVR, and VRCFaceTracking. Enable Independent Eye Gaze only if you want the Hub method; Start tracking checks the prepared patch again.");
    }

    private bool TryBeginSetupProgress(string label)
    {
        if (UtilityActionIsBusy()) return false;
        // The clicked setup button is about to be disabled. Leave keyboard focus
        // on the form so WinForms does not focus a later control and scroll there.
        ActiveControl = null;
        _setupActionRunning = true;
        _setupProgressContainer.Visible = true;
        _setupProgress.IsIndeterminate = true;
        _setupProgress.Value = 0;
        _setupProgressStatus.Text = label.Equals("PC runtime setup", StringComparison.OrdinalIgnoreCase)
            ? "Installing and verifying the private PC runtime… This can take several minutes."
            : label + " is running… Please keep this window open.";
        _setupProgressStatus.ForeColor = Warning;
        SetSetupButtonsEnabled(false);
        return true;
    }

    private void FinishSetupProgress(bool succeeded, string label)
    {
        _setupActionRunning = false;
        _setupProgress.IsIndeterminate = false;
        _setupProgress.Value = succeeded ? 100 : 0;
        _setupProgressStatus.Text = succeeded ? label + " completed successfully." : _actionTitle.Text + ". " + _actionNext.Text;
        _setupProgressStatus.ForeColor = succeeded ? Good : Bad;
        SetSetupButtonsEnabled(true);
    }

    private void SetSetupButtonsEnabled(bool enabled)
    {
        enabled &= !_closingInProgress && !_starting && !_stopping && !_trackingCleanupPending && !_gazeRecoveryRunning &&
            !_setupActionRunning && !_utilityActionRunning && !_datasetOperationBusy && !LiveTrackingRunning;
        _setupRuntimeButton.Enabled = enabled;
        UpdateModuleInstallButtonState(enabled);
        _uninstallBridgeButton.Enabled = enabled && BridgeUninstallAvailable();
        _setupGazeButton.Enabled = enabled;
        _recoverGazeButton.Enabled = enabled && !_gazeRecoveryRunning;
        _inspectGazeButton.Enabled = _recoverGazeButton.Enabled;
        _resetLegacyGazeButton.Enabled = _recoverGazeButton.Enabled;
        _enableWirelessButton.Enabled = enabled;
        _connectWirelessButton.Enabled = enabled;
        _pairWirelessButton.Enabled = enabled;
        _disableWirelessButton.Enabled = enabled;
        _reconnectUsbButton.Enabled = enabled && !_compatibilityChecking;
        _forgetUsbButton.Enabled = _reconnectUsbButton.Enabled && _environment.UsbHeadsetRemembered;
        _setupAmdButton.Enabled = enabled && AmdInstallEligible && BackendReady();
    }

    private async Task TrainTongueAsync(TongueDatasetKind kind)
    {
        if (UtilityActionIsBusy()) return;
        var queue = kind switch
        {
            TongueDatasetKind.Quick => _quickDatasets,
            TongueDatasetKind.Focused => _focusedDatasets,
            _ => _fullDatasets,
        };
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
        if (UtilityActionIsBusy()) return;
        var refinementParent = kind == TongueDatasetKind.Full ? null : _tongueModels.SelectedItem as FileChoice;
        var parentVersion = refinementParent is null ? 0 : VersionFromPath(refinementParent.Primary);
        var trainingArguments = new List<string> { "-SessionPath", dataset.SessionPath };
        if (parentVersion > 0)
        {
            trainingArguments.AddRange(["-BaseVersion", parentVersion.ToString()]);
            AppendLog($"Refinement will extend the selected tongue model: {refinementParent!.Label}.");
        }
        _datasetOperationBusy = true;
        UpdateControlState();
        try
        {
            BeginTrainingProgress(kind);
            var versionsBefore = TongueModelVersions().ToHashSet();
            var succeeded = await RunUtilityAsync(
                kind switch
                {
                    TongueDatasetKind.Quick => "Personal refinement training",
                    TongueDatasetKind.Focused => "Focused refinement training",
                    _ => "Full tongue training",
                },
                kind == TongueDatasetKind.Full ? "train-latest-tongue-stills.ps1" : "train-latest-tongue-refinement.ps1",
                trainingArguments.ToArray());
            if (!succeeded) { FinishTrainingProgress(false); return; }
            var created = TongueModelVersions().Where(version => !versionsBefore.Contains(version)).OrderDescending().FirstOrDefault();
            if (created <= 0)
                throw new InvalidOperationException("The training helper exited successfully, but no complete new model pair was found. Check Activity and the selected dataset before retrying.");
            if (created > 0)
            {
                // The trainer's result determines whether cheeks were trained.
                // An older tongue-only capture must not inherit a parent's
                // camera-cheek flag after its new tongue checkpoint is written.
                JsonObject? resultMetadata = ReadModelMetadata(created);
                JsonObject? classification = (resultMetadata ??
                    (parentVersion > 0 ? ReadModelMetadata(parentVersion) : null))?.DeepClone()?.AsObject();
                if (resultMetadata?["hasCameraCheeks"] is not JsonValue cheekFlag ||
                    !cheekFlag.TryGetValue<bool>(out var hasCheeks) || !hasCheeks)
                    classification?.Remove("hasCameraCheeks");
                if (parentVersion > 0)
                {
                    classification ??= new JsonObject();
                    JsonObject? parentMetadata = ReadModelMetadata(parentVersion);
                    classification["parentVersion"] = parentVersion;
                    classification["parentDisplayName"] = ModelFriendlyName(parentVersion, parentMetadata);
                    classification["parentModelKind"] = HasMoustacheModelIcon(parentMetadata)
                        ? "mustachio-experimental" : parentMetadata?["modelKind"]?.DeepClone();
                    if (IsExperimentalModelMetadata(parentMetadata)) classification["isExperimental"] = true;
                }
                WriteModelMetadata(created, dataset.DisplayName, dataset.SessionPath, kind switch
                {
                    TongueDatasetKind.Quick => "quick refinement",
                    TongueDatasetKind.Focused when dataset.LegacyDiagonalOnly => "legacy diagonal-only refinement",
                    TongueDatasetKind.Focused => "focused diagonal and facial hair refinement",
                    _ => "full personal dataset",
                }, classification);
                AppendLog($"Model v{created} named “{dataset.DisplayName}”.");
            }
            ReloadProfiles();
            if (created > 0)
            {
                var trainedGate = Path.Combine(_root, "models", $"qpro-stereo-tongue-v{created}-gate.pt");
                for (var index = 0; index < _tongueModels.Items.Count; index++)
                {
                    if (_tongueModels.Items[index] is FileChoice choice &&
                        string.Equals(choice.Primary, trainedGate, StringComparison.OrdinalIgnoreCase))
                    {
                        _tongueModels.SelectedIndex = index;
                        AppendLog($"Selected new tongue model v{created} for the next tracking session.");
                        break;
                    }
                }
                if (_cameraCheekPuff.Checked && !SelectedModelHasCameraCheeks())
                {
                    _cameraCheekPuff.Checked = false;
                    AppendLog("This tongue-only capture has no trained cheek camera outputs. Camera cheek puff is off; record the new lower-face cheek cards to train a combined copy.");
                }
            }
            FinishTrainingProgress(created > 0);
            if (created > 0)
            {
                PlaySfx("trainingComplete.wav");
                MessageBox.Show(this, $"Training is complete. “{dataset.DisplayName}” is now available as lower-face model v{created}." +
                    (SelectedModelHasCameraCheeks() ? " Enable Camera cheek puff (experimental) in Live tracking to test its cheek outputs." : " This copy contains tongue outputs."),
                    "Lower-face model ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception error)
        {
            FinishTrainingProgress(false);
            ShowWorkflowFailure("Training result", error);
        }
        finally { _datasetOperationBusy = false; UpdateControlState(); }
    }

    private void ShowWorkflowFailure(string label, Exception error)
    {
        AppendLog($"{label} failed: {error}");
        var failure = HubActionFailure.Explain(label, error.Message, string.Empty);
        SetActionFeedback(failure.Title, failure.Detail, failure.NextStep, true);
        MessageBox.Show(this, failure.Detail + "\n\nNext: " + failure.NextStep,
            failure.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task<List<string>> TrackingPrerequisitesAsync()
    {
        var missing = new List<string>();
        bool needsHeadsetRuntime = WorkerTrackingSelected;
        if (needsHeadsetRuntime && FindAdb() is null) missing.Add("the bundled Android tools — re-extract the complete release");
        else if (needsHeadsetRuntime && !await HasQuestAsync()) missing.Add(_environment.WirelessSelected
            ? "an authorized wireless Quest — use First-time setup to connect or pair it"
            : "an authorized Quest over USB — connect the cable and approve debugging");
        else if (needsHeadsetRuntime && await QuestIdentityProblemAsync() is { } identityProblem) missing.Add(identityProblem);
        if (!Process.GetProcessesByName("vrserver").Any()) missing.Add("SteamVR");
        bool faceFeatures = _gaze.Checked || _tongue.Checked || _cameraCheekPuff.Checked || _pupil.Checked || NativeCheekAdjustmentsSelected;
        if (faceFeatures && !Process.GetProcessesByName("VRCFaceTracking").Any()) missing.Add("VRCFaceTracking");
        else if (faceFeatures && _environment.TrackingSourceRequiresVrcftRestart()) missing.Add("restart VRCFaceTracking after changing the face-tracking source");
        if (faceFeatures && !BridgeInstalled()) missing.Add("the Qpro VRCFT module — use First-time setup: Install module");
        else if (faceFeatures && !CurrentBridgeInstalled()) missing.Add("the module for this face-tracking source and Qpro build — close VRCFaceTracking, use First-time setup: Install module, then restart it");
        if (needsHeadsetRuntime && !BackendReady()) missing.Add("the PC runtime — use First-time setup: Install runtime");
        if (_gaze.Checked && !EyeModelReady()) missing.Add("the locally prepared gaze patch — use First-time setup: Optional independent gaze");
        if (_gaze.Checked && _eyeProfiles.SelectedItem is null) missing.Add("an eye profile");
        if ((_tongue.Checked || _cameraCheekPuff.Checked) && _tongueModels.SelectedItem is null) missing.Add("a paired tongue model");
        if (_cameraCheekPuff.Checked && !SelectedModelHasCameraCheeks()) missing.Add("a trained tongue + cheeks model — use Personalize to record and train cheek camera poses, then select that copy under Lower-face model");
        if (_pupil.Checked && !File.Exists(Path.Combine(_root, "pupil_dilation.py"))) missing.Add("the pupil estimation script — re-extract the complete release");
        AddControllerPrerequisites(missing);
        return missing;
    }

    private async Task StartTrackingAsync()
    {
        if (_starting || _stopping) return;
        if (UtilityActionIsBusy()) return;
        _trackingProcesses.RemoveAll(p => p.HasExited);
        if (LiveTrackingRunning) { MessageBox.Show(this, "Tracking is already running."); return; }
        if (!_gaze.Checked && !_tongue.Checked && !_cameraCheekPuff.Checked && !_pupil.Checked && !_hybridHands.Checked && !_controllerTouchpad.Checked && !NativeCheekAdjustmentsSelected) { PlaySfx("warning.wav"); MessageBox.Show(this, "Select at least one tracking feature."); return; }
        bool workerTrackingRequested = WorkerTrackingSelected;
        _starting = true;
        _cameraInputReady = false;
        var startCancellation = new CancellationTokenSource();
        _startCancellation = startCancellation;
        UpdateControlState();
        try
        {
            var missing = await TrackingPrerequisitesAsync();
            startCancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || Disposing) return;
            if (missing.Count > 0)
            {
                SetActionFeedback("Tracking needs a few prerequisites", string.Join("; ", missing), "Complete the listed requirements, then retry Start tracking.", true);
                PlaySfx("warning.wav");
                MessageBox.Show(this, "Before starting tracking, start or provide:\n\n• " +
                    string.Join("\n• ", missing), "Not ready");
                return;
            }

            AppendLog($"Face-tracking source for this session: {(_environment.SteamLinkSelected ? "Steam Link OSC (port 9015)" : "Virtual Desktop")}.");
            _gazeFailureHandled = false;
            _gazeRecoveryConfirmed = false;
            File.Delete(_stopFile);
            _start.Enabled = false; _stop.Enabled = true;
            _runStatus.Text = "● Starting…"; _runStatus.ForeColor = Warning;
            SetStatus(_inferenceStatus, StatusKind.Warning, (_tongue.Checked || _cameraCheekPuff.Checked) ? "Detecting…" : "Idle");
            _pupilBackendLabel = "Starting";
            SetStatus(_pupilStatus, StatusKind.Warning, _pupil.Checked ? "Starting…" : "Idle");
            if (_gaze.Checked)
            {
                _gazeStartupInProgress = true;
                Process? gazeProcess = null;
                var gazeReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gazeStartupSignal = gazeReadySignal;
                try
                {
                    var eye = (FileChoice)_eyeProfiles.SelectedItem!;
                    _runStatus.Text = "● Checking independent gaze compatibility…";
                    AppendLog("Checking the prepared eye model against this headset before changing tracking. If compatible, Meta trackingservice restarts briefly during apply and restore.");
                    var gazeArguments = new List<string> { "-RuntimePreview", "-VrcftOutput", "-CalibrationOutput", eye.Primary, "-StopFile", _stopFile };
                    if (!_cameraPreview.Checked) gazeArguments.Add("-NoWindow");
                    AppendLog(_cameraPreview.Checked ? "Independent gaze preview enabled; its display is limited to 20 FPS."
                        : "Independent gaze preview off; live gaze output continues without rendering a window.");
                    gazeProcess = StartManaged("Independent gaze", "native-eye-local-branch-test.ps1", gazeArguments.ToArray());
                    AppendLog("Waiting for a valid paired-eye gaze sample before starting cameras…");
                    bool gazeReady = await gazeReadySignal.Task.WaitAsync(TimeSpan.FromSeconds(90), startCancellation.Token);
                    if (!gazeReady || gazeProcess.HasExited)
                        throw new InvalidOperationException("Independent gaze stopped before a valid paired-eye sample arrived. See Activity for the exact error.");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (gazeProcess is { HasExited: false })
                    {
                        AppendLog("Independent gaze did not become ready. Requesting its stock-model cleanup before starting other cameras…");
                        File.WriteAllText(_stopFile, DateTimeOffset.Now.ToString("O"));
                        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        try { await gazeProcess.WaitForExitAsync(cleanupTimeout.Token); }
                        catch (OperationCanceledException)
                        {
                            throw new InvalidOperationException("Independent gaze cleanup is still running. Other cameras were not started; press Stop tracking and check Activity.", error);
                        }
                        if (!startCancellation.IsCancellationRequested && !_stopping) File.Delete(_stopFile);
                    }
                    await DisableFailedGazeAsync(error.Message);
                }
                finally
                {
                    if (ReferenceEquals(_gazeStartupSignal, gazeReadySignal)) _gazeStartupSignal = null;
                    _gazeStartupInProgress = false;
                }
                if (!_gazeFailureHandled && gazeProcess?.HasExited == true && !startCancellation.IsCancellationRequested)
                    await DisableFailedGazeAsync("The independent-gaze process exited during startup. See Activity.");
            }
            startCancellation.Token.ThrowIfCancellationRequested();
            FileChoice? lowerFaceModel = _tongueModels.SelectedItem as FileChoice;
            var cameraPlan = HubCameraTrackingLaunch.Create(new(
                _environment.TrackingSourceArgument, _tongue.Checked, _cameraCheekPuff.Checked,
                SelectedModelHasCameraCheeks(), lowerFaceModel?.Primary, lowerFaceModel?.Secondary,
                _fps.SelectedItem?.ToString() ?? "24", _smoothing.Value, VisibilityModeValue(),
                _pupil.Checked, PupilSensitivityValue(), _cameraPreview.Checked, _stopFile));
            if (cameraPlan.Required)
            {
                if ((_tongue.Checked || _cameraCheekPuff.Checked) && lowerFaceModel is not null)
                    AppendLog($"Lower-face model selected for this session: v{VersionFromPath(lowerFaceModel.Primary)} (gate: {Path.GetFileName(lowerFaceModel.Primary)}; direction: {Path.GetFileName(lowerFaceModel.Secondary)}).");
                AppendLog("Camera outputs for this session: " + cameraPlan.OutputDescription);
                var supervisedArguments = cameraPlan.Arguments.Concat(new[]
                {
                    "-CompanionPid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-CompanionStartFileTime", Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc()
                        .ToString(System.Globalization.CultureInfo.InvariantCulture)
                }).ToArray();
                StartManaged(_tongue.Checked || _cameraCheekPuff.Checked ? "Camera tracking" : "Pupil tracking",
                    "build-and-run.ps1", supervisedArguments);
            }
            StartControllerInput();
            startCancellation.Token.ThrowIfCancellationRequested();
            bool workersRunning = _trackingProcesses.Any(p => !p.HasExited);
            _nativeCheekOnlySession = !workerTrackingRequested;
            if (!workersRunning && !_nativeCheekOnlySession)
                throw new InvalidOperationException("The selected tracking processes did not remain running. Check Activity before retrying.");
            _cheekTrackingSession = new CheekTrackingSession();
            AppendLog("Cheek adjustments enabled for this tracking session. Stop tracking restores the streaming app's native cheek values.");
            if (!_gazeFailureHandled)
            {
                _runStatus.Text = (_hybridHands.Checked || _controllerTouchpad.Checked)
                    ? "● Waiting for valid controller input…" : cameraPlan.Required ? "● Waiting for camera output…" : "● Selected overrides active";
                _runStatus.ForeColor = (_hybridHands.Checked || _controllerTouchpad.Checked || cameraPlan.Required) ? Warning : Good;
            }
            UpdateControlState();
        }
        catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
        {
            AppendLog("Tracking startup cancelled by Stop tracking.");
        }
        catch (Exception error)
        {
            AppendLog("START FAILED: " + error.Message);
            var failure = HubActionFailure.Explain("Tracking", error.Message, error.Message);
            SetActionFeedback(failure.Title, error.Message, failure.NextStep, true);
            await StopTrackingAsync();
            PlaySfx("warning.wav");
            MessageBox.Show(this, error.Message, "Tracking did not start");
        }
        finally
        {
            if (startCancellation.IsCancellationRequested) EndCheekTrackingSession();
            _starting = false;
            if (!_nativeCheekOnlySession && !_trackingProcesses.Any(p => !p.HasExited)) EndCheekTrackingSession();
            if (ReferenceEquals(_startCancellation, startCancellation)) _startCancellation = null;
            startCancellation.Dispose();
            UpdateControlState();
        }
    }

    private async Task StopTrackingAsync()
    {
        EndCheekTrackingSession();
        if (_stopping) return;
        _stopping = true;
        bool gazeRestoreFailed = false;
        bool stopRequestFailed = false;
        _startCancellation?.Cancel();
        _runStatus.Text = "● Stopping cleanly…"; _runStatus.ForeColor = Warning;
        try
        {
            File.WriteAllText(_stopFile, DateTimeOffset.Now.ToString("O"));
            var deadline = DateTime.UtcNow.AddSeconds(18);
            while (_trackingProcesses.Any(p => !p.HasExited) && DateTime.UtcNow < deadline)
                await Task.Delay(250);
            if (_trackingProcesses.Any(p => !p.HasExited))
            {
                _trackingCleanupPending = true;
                AppendLog("Tracking cleanup is still running. The stop request remains active; press Stop tracking again if needed, or Q if the preview is still open.");
            }
            else
            {
                // Drain final stdout before inspecting the recovery marker. An
                // exit code alone cannot prove that the headset was restored.
                var drained = await Task.WhenAll(_trackingProcesses.Select(process =>
                    HubProcessResult.WaitForOutputDrainAsync(process, TimeSpan.FromSeconds(5))));
                if (drained.Any(complete => !complete))
                {
                    _trackingCleanupPending = true;
                    AppendLog("Tracking processes exited, but their final output has not finished. Cleanup remains unverified; keep the Hub open and press Stop tracking again.");
                    return;
                }
                _trackingCleanupPending = false;
                gazeRestoreFailed = !_gazeRecoveryConfirmed && _trackingProcesses.Any(process =>
                    process.StartInfo.ArgumentList.Any(argument =>
                        argument.EndsWith("native-eye-local-branch-test.ps1", StringComparison.OrdinalIgnoreCase)));
                File.Delete(_stopFile);
                AppendLog(gazeRestoreFailed
                    ? "Qpro live processes stopped, but eye-model recovery was not confirmed. Use Recover Qpro gaze in First-time setup and check Activity before starting gaze again."
                    : "Qpro live overrides stopped; native cheek values restored. Saved eyebrow and smirk adjustments and any Magisk modules remain active.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stopRequestFailed = true;
            AppendLog($"Stop request failed while writing or removing the local stop file: {error.Message}");
            MessageBox.Show(this,
                "Qpro could not send the stop request. Check Activity, then close the camera preview with Q if it is open. Keep the Hub open until the tracking processes finish.",
                "Tracking could not stop cleanly", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!_trackingCleanupPending) _trackingProcesses.RemoveAll(p => p.HasExited);
            _stopping = false; _start.Enabled = true; _stop.Enabled = false;
            _runStatus.Text = stopRequestFailed ? "● Stop request failed — check Activity"
                : _trackingCleanupPending || _trackingProcesses.Count != 0 ? "● Waiting for tracking cleanup"
                : gazeRestoreFailed ? "● Eye-model restore unconfirmed — check Activity"
                : "● Idle — Qpro live overrides off";
            _runStatus.ForeColor = _trackingProcesses.Count == 0 && !gazeRestoreFailed && !stopRequestFailed ? Good : Warning;
            if (_trackingProcesses.Count == 0) ResetInferenceStatus();
            UpdateControlState();
        }
    }

    private Process StartManaged(string label, string script, params string[] arguments)
    {
        var start = PowerShellStart(script, arguments, hidden: true);
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        // Keep a parent pipe open so hand adapters also stop if the Hub exits.
        start.RedirectStandardInput = label == "Hand/controller input";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void ReportLine(string line)
        {
            try
            {
                AppendLog($"[{label}] {line}");
                PostProcessUpdate(label, () =>
                {
                    if (!_trackingProcesses.Contains(process)) return;
                    if (!process.HasExited) HandleInferenceStatus(label, line);
                    // Restoration reports remain relevant after process exit;
                    // readiness output must not reactivate a stopped session.
                    if (!process.HasExited || line.StartsWith(HubControllerFeedback.CleanupPrefix, StringComparison.Ordinal))
                        ObserveControllerInput(label, line);
                });
                if (label == "Independent gaze" && line.StartsWith("GAZE_STREAM_READY ", StringComparison.Ordinal))
                    _gazeStartupSignal?.TrySetResult(true);
                if (label == "Independent gaze") ObserveGazeRecovery(line, allowNoSession: false);
            }
            catch (Exception error)
            {
                AppendLog($"[{label}] Output handling failed: {error}");
                PostProcessUpdate(label, () => SetActionFeedback(label + " output needs attention",
                    error.Message, "Copy diagnostics and check the Activity output before relying on tracking.", true));
            }
        }
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) ReportLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) ReportLine(e.Data); };
        process.Exited += (_, _) =>
        {
            if (label == "Independent gaze") _gazeStartupSignal?.TrySetResult(false);
            var stopWasRequested = _stopping || _trackingCleanupPending || _closingInProgress;
            int exitCode;
            try { exitCode = process.ExitCode; }
            catch (Exception error)
            {
                AppendLog($"[{label}] Exit status could not be read: {error.Message}");
                return;
            }
            PostProcessUpdate(label, () =>
            {
                    if (!_starting && !_nativeCheekOnlySession && !_trackingProcesses.Any(p => !p.HasExited))
                        EndCheekTrackingSession();
                    AppendLog($"[{label}] exited with code {exitCode}.");
                    if (label == "Independent gaze" && !stopWasRequested && !_stopping && !_closingInProgress)
                    {
                        if (!_gazeStartupInProgress && !_gazeFailureHandled)
                            _ = DisableFailedGazeAsync($"The independent-gaze process stopped with code {exitCode}.");
                        UpdateControlState();
                        return;
                    }
                    if (!stopWasRequested && !_stopping)
                    {
                        _runStatus.Text = $"● {label} stopped — check Activity";
                        _runStatus.ForeColor = Warning;
                        if (label == "Camera tracking") SetStatus(_inferenceStatus, StatusKind.Warning, "Stopped");
                        if (_pupil.Checked && label is ("Camera tracking" or "Pupil tracking")) SetStatus(_pupilStatus, StatusKind.Warning, "Stopped");
                    }
                    UpdateControlState();
            });
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {label}.");
            // Track it before attaching readers: if a reader fails, the caller's
            // Stop path must still request this child's headset cleanup.
            _trackingProcesses.Add(process);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            AppendLog($"Starting {label}…");
            return process;
        }
        catch
        {
            if (!_trackingProcesses.Contains(process)) process.Dispose();
            throw;
        }
    }

    private async Task DisableFailedGazeAsync(string reason)
    {
        if (_gazeFailureHandled || _stopping || _closingInProgress || IsDisposed || Disposing) return;
        _gazeRecoveryRunning = true;
        _gazeRecoveryConfirmed = false;
        _gazeFailureHandled = true;
        _gaze.Checked = false;
        AppendLog($"Independent Eye Gaze disabled automatically: {reason}");
        AppendLog("Tongue and pupil tracking will continue if selected. Checking that the temporary eye model was removed…");
        _runStatus.Text = "● Independent gaze disabled — checking stock eye model";
        _runStatus.ForeColor = Warning;
        UpdateControlState();
        try
        {
            var start = PowerShellStart("native-eye-local-branch-test.ps1", ["-RestoreIfActive"], hidden: true);
            var result = await HubProcessResult.RunAsync(start, TimeSpan.FromSeconds(35));
            foreach (var line in result.Diagnostics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                AppendLog("[Independent gaze recovery] " + line);
                if (!result.TimedOut && result.CleanupError is null) ObserveGazeRecovery(line, allowNoSession: true);
            }
            if (result.TimedOut) throw new TimeoutException("The stock eye-model recovery check timed out; restoration remains unconfirmed.");
            if (result.CleanupError is not null) throw new InvalidOperationException(result.CleanupError);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Stock eye-model recovery failed with code {result.ExitCode}.");
            if (!_gazeRecoveryConfirmed) throw new InvalidOperationException("The recovery script did not confirm the Qpro session state.");
            if (_stopping || IsDisposed || Disposing || (_starting && _startCancellation?.IsCancellationRequested == true)) return;
            _runStatus.Text = _trackingProcesses.Any(p => !p.HasExited)
                ? "● Independent gaze disabled — other tracking continues"
                : "● Independent gaze disabled — Qpro recovery checked";
        }
        catch (Exception error)
        {
            _gazeRecoveryConfirmed = false;
            AppendLog("Independent gaze recovery needs attention: " + error.Message);
            if (_stopping || IsDisposed || Disposing || (_starting && _startCancellation?.IsCancellationRequested == true)) return;
            _runStatus.Text = "● Independent gaze disabled — check eye-model recovery in Activity";
        }
        finally
        {
            _gazeRecoveryRunning = false;
            UpdateControlState();
        }
        _runStatus.ForeColor = Warning;
        UpdateControlState();
    }

    private void ObserveGazeRecovery(string line, bool allowNoSession)
    {
        if (line == "QPRO_GAZE_SESSION applied" || line == "QPRO_GAZE_RECOVERY unconfirmed")
            _gazeRecoveryConfirmed = false;
        else if (line == "QPRO_GAZE_RECOVERY restored" || allowNoSession && line == "QPRO_GAZE_RECOVERY none")
            _gazeRecoveryConfirmed = true;
    }

    private async Task RecoverGazeAsync()
    {
        if (UtilityActionIsBusy()) return;
        _gazeRecoveryConfirmed = false;
        _gazeRecoveryRunning = true;
        UpdateControlState();
        try
        {
            var succeeded = await RunUtilityAsync("Qpro gaze recovery", "native-eye-local-branch-test.ps1", "-RestoreIfActive");
            _runStatus.Text = succeeded ? "● Idle — Qpro gaze recovery checked" : "● Gaze recovery unconfirmed — check Activity";
            _runStatus.ForeColor = succeeded ? Good : Warning;
        }
        finally { _gazeRecoveryRunning = false; UpdateControlState(); }
    }

    private async Task InspectGazeAsync()
    {
        if (UtilityActionIsBusy()) return;
        _gazeInspectionConfirmed = false;
        _gazeInspectionResult = null;
        var succeeded = await RunUtilityAsync("Gaze setup check", "native-eye-local-branch-test.ps1", "-InspectOnly");
        var result = _gazeInspectionResult;
        MessageBox.Show(this, succeeded && result is not null ? result.PopupText() : HubGazeInspection.FailedPopupText,
            "Gaze setup result", MessageBoxButtons.OK,
            succeeded && result is { NeedsAttention: false } ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task ResetLegacyGazeAsync()
    {
        if (UtilityActionIsBusy()) return;
        if (MessageBox.Show(this,
            "Use this only for an older Qpro session without a recovery record. It chooses the normal, nonexperimental eye-model selection and briefly restarts headset tracking.\n\n" +
            "An active Magisk gaze module or an unverified model overlay blocks this reset. Disable the gaze module in Magisk and reboot first if you want ordinary headset tracking.\n\n" +
            "This does not uninstall the Qpro PC module or verify every factory setting. Continue?",
            "Reset legacy gaze", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _legacyGazeResetConfirmed = false;
        var succeeded = await RunUtilityAsync("Legacy gaze reset", "native-eye-local-branch-test.ps1", "-ResetLegacySelection", "-ConfirmLegacyReset");
        if (succeeded) _gaze.Checked = false;
        _runStatus.Text = succeeded ? "● Normal gaze selection checked" : "● Legacy gaze reset unconfirmed — check Activity";
        _runStatus.ForeColor = succeeded ? Good : Warning;
    }

    private void ResetInferenceStatus()
    {
        _pupilBackendLabel = "Starting";
        SetStatus(_inferenceStatus, StatusKind.Warning, "Idle");
        SetStatus(_pupilStatus, StatusKind.Warning, "Idle");
    }

    private void HandleInferenceStatus(string label, string line)
    {
        if (label is not ("Camera tracking" or "Pupil tracking")) return;
        if (IsDisposed || Disposing || _stopping) return;
        if (InvokeRequired) { PostProcessUpdate(label, () => HandleInferenceStatus(label, line)); return; }

        if (line.StartsWith("TRANSPORT_QUALITY ", StringComparison.Ordinal) &&
            Regex.Match(line, @"\bfps=(?<fps>\d+(?:\.\d+)?)").Groups["fps"] is { Success: true } frameRate &&
            double.TryParse(frameRate.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps) && fps > 0)
        {
            _cameraInputReady = true;
            UpdateSessionReadiness();
        }

        if (label == "Camera tracking")
        {
            var match = Regex.Match(line, @"INFERENCE_BACKEND feature=tongue backend=(?<backend>[\w-]+) device=(?<device>\S+) name=(?<name>.*)");
            if (match.Success)
            {
                var backend = match.Groups["backend"].Value;
                var name = match.Groups["name"].Value.Trim();
                var description = backend switch
                {
                    "amd-rocm" => "AMD ROCm",
                    "nvidia-cuda" => "NVIDIA CUDA",
                    "cpu" => "CPU",
                    _ => "Other (" + match.Groups["device"].Value + ")"
                };
                SetStatus(_inferenceStatus, StatusKind.Warning, description + " selected");
                _inferenceStatus.AccessibleDescription = name;
            }
        }
        if (_pupil.Checked)
        {
            var match = Regex.Match(line, @"INFERENCE_BACKEND feature=pupil backend=(?<backend>[\w-]+) device=(?<device>\S+) name=(?<name>.*)");
            if (match.Success)
            {
                _pupilBackendLabel = match.Groups["backend"].Value switch
                {
                    "amd-rocm" => "AMD ROCm",
                    "nvidia-cuda" => "NVIDIA CUDA",
                    "cpu" => "CPU",
                    _ => "Other"
                };
                SetStatus(_pupilStatus, StatusKind.Warning, _pupilBackendLabel + " (warming)");
                _pupilStatus.AccessibleDescription = match.Groups["name"].Value;
            }
        }
        if (_pupil.Checked && line.Contains("PUPIL_STATUS", StringComparison.Ordinal))
        {
            var leftValid = Regex.IsMatch(line, @"\bleft=\d");
            var rightValid = Regex.IsMatch(line, @"\bright=\d");
            var pupilLabel = _pupilBackendLabel + (leftValid && rightValid ? " (2 eyes)" :
                leftValid || rightValid ? " (1 eye)" : " (warming)");
            SetStatus(_pupilStatus, leftValid && rightValid ? StatusKind.Good : StatusKind.Warning, pupilLabel);
            _pupilStatus.AccessibleDescription = line;
        }
    }

    private async Task<bool> RunUtilityAsync(string label, string script, params string[] args)
    {
        if (_closingInProgress) return false;
        if (_starting || _stopping)
        {
            SetActionFeedback(label + " is waiting", "Tracking startup or cleanup is still running.", "Wait for it to finish, then retry this action.");
            return false;
        }
        if (_utilityActionRunning)
        {
            AppendLog($"{label} was not started because another setup, capture, or training action is running.");
            return false;
        }
        if (_trackingCleanupPending || LiveTrackingRunning) { MessageBox.Show(this, "Stop live tracking and finish its cleanup before starting this action."); return false; }
        var recentOutput = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var acceptingProgress = 1;
        var isControllerUtility = script.Equals("controller-input.ps1", StringComparison.OrdinalIgnoreCase);
        var isControllerCompatibility = isControllerUtility && args.Length >= 2 &&
            args[0].Equals("-Action", StringComparison.OrdinalIgnoreCase) && args[1].Equals("check", StringComparison.OrdinalIgnoreCase);
        var controllerResult = isControllerUtility ? new HubControllerUtilityResult() : null;
        var setupResult = HubSetupResult.Create(script, args);
        _utilityActionRunning = true;
        UpdateControlState();
        try
        {
            AppendLog($"Starting {label}…");
            SetActionFeedback("Starting " + label, "Launching the bundled helper and waiting for progress.", "Keep the Hub open. Activity will report the current step and any failure.");
            var isRuntimeSetup = script.Equals("setup-runtime.ps1", StringComparison.OrdinalIgnoreCase);
            var isGazeRecovery = script.Equals("native-eye-local-branch-test.ps1", StringComparison.OrdinalIgnoreCase) &&
                args.Contains("-RestoreIfActive", StringComparer.OrdinalIgnoreCase);
            var isGazeInspection = script.Equals("native-eye-local-branch-test.ps1", StringComparison.OrdinalIgnoreCase) &&
                args.Contains("-InspectOnly", StringComparer.OrdinalIgnoreCase);
            var isLegacyGazeReset = script.Equals("native-eye-local-branch-test.ps1", StringComparison.OrdinalIgnoreCase) &&
                args.Contains("-ResetLegacySelection", StringComparer.OrdinalIgnoreCase);
            if (isRuntimeSetup)
                AppendLog($"[PC runtime setup] Launching bundled setup script from {Path.Combine(_root, script)}. Keep the Hub open; it will report when PowerShell or a download is still running.");
            var start = PowerShellStart(script, args, hidden: true);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult();
            var startedAt = DateTime.UtcNow;
            var lastOutputTicks = startedAt.Ticks;
            var outputLineCount = 0;
            Exception? outputFailure = null;
            void ReportProcessLine(string line)
            {
                try { ObserveProcessLine(line); }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref outputFailure, error, null);
                    AppendLog($"[{label}] Output handling failed: {error}");
                }
            }
            void ObserveProcessLine(string line)
            {
                Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref outputLineCount);
                recentOutput.Enqueue(line);
                while (recentOutput.Count > 60) recentOutput.TryDequeue(out _);
                AppendLog($"[{label}] {line}");
                if (Volatile.Read(ref acceptingProgress) == 0) return;
                controllerResult?.Observe(line);
                setupResult?.Observe(line);
                PostProcessUpdate(label, () =>
                {
                    // Posted progress may arrive after the final result. It must
                    // not turn a completed or failed action back into "running".
                    if (Volatile.Read(ref acceptingProgress) == 0) return;
                    if ((isRuntimeSetup || script.Equals("Install-QproRocm.ps1", StringComparison.OrdinalIgnoreCase)) &&
                        Regex.IsMatch(line, @"^(Checking|Installing|Downloading|Verifying|Selected|Discrete|PC runtime|ROCm|NVIDIA GPU|AMD GPU|Existing shared)", RegexOptions.IgnoreCase))
                        SetActionFeedback(label + " is running", line.Length <= 240 ? line : line[..237] + "…", "Wait for the final checks. The environment is ready only when setup confirms success.");
                    if (!isControllerCompatibility) ObserveControllerInput(label, line);
                    HandleTrainingProgress(label, line);
                });
                if (isGazeRecovery) ObserveGazeRecovery(line, allowNoSession: true);
                if (isGazeInspection && line.StartsWith(HubGazeInspection.Prefix, StringComparison.Ordinal))
                    _gazeInspectionResult = HubGazeInspection.ParseLine(line);
                if (isGazeInspection && line == "QPRO_GAZE_INSPECTION complete") _gazeInspectionConfirmed = true;
                if (isLegacyGazeReset && line == "QPRO_GAZE_LEGACY_RESET complete") _legacyGazeResetConfirmed = true;
            }
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) ReportProcessLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) ReportProcessLine(e.Data); };
            if (!process.Start()) throw new InvalidOperationException($"Could not start {label}. See Activity for details.");
            if (isRuntimeSetup)
                AppendLog($"[PC runtime setup] PowerShell launched (PID {process.Id}). Waiting for the script's first progress message…");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var exitTask = exited.Task;
            if (isRuntimeSetup)
            {
                var startupWarningShown = false;
                while (!exitTask.IsCompleted)
                {
                    await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15)));
                    if (exitTask.IsCompleted) break;
                    var now = DateTime.UtcNow;
                    var quietFor = now - new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc);
                    if (quietFor.TotalSeconds < 15) continue;
                    var elapsed = now - startedAt;
                    var stage = Interlocked.CompareExchange(ref outputLineCount, 0, 0) == 0
                        ? "The setup script has not printed its first message yet."
                        : "No new setup output has arrived.";
                    AppendLog($"[PC runtime setup] Still running after {elapsed.TotalSeconds:0} seconds (PowerShell PID {process.Id}); {stage} Last output was {quietFor.TotalSeconds:0} seconds ago.");
                    SetActionFeedback("PC runtime setup is still running", $"Elapsed: {elapsed.TotalSeconds:0} seconds. {stage} Last progress: {quietFor.TotalSeconds:0} seconds ago.", "Keep the Hub open. If it stays silent, check Windows Security and copy diagnostics.");
                    if (!startupWarningShown && elapsed.TotalMinutes >= 1 && Interlocked.CompareExchange(ref outputLineCount, 0, 0) == 0)
                    {
                        startupWarningShown = true;
                        AppendLog("[PC runtime setup] PowerShell is open but the setup script has not begun reporting. This can indicate a blocked PowerShell startup. Keep this Activity log and check Windows Security or other security software for a blocked powershell.exe process.");
                    }
                }
            }
            await exitTask;
            // Drain callbacks asynchronously with a separate deadline after the
            // helper exits; a descendant retaining a pipe must not hang the Hub.
            var outputDrained = await HubProcessResult.WaitForOutputDrainAsync(process, TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref acceptingProgress, 0);
            if (!outputDrained)
                throw new InvalidOperationException("The helper exited, but its output did not finish. Its result remains unverified; check Activity before retrying.");
            if (outputFailure is not null)
                throw new InvalidOperationException("The helper output could not be read completely; its result remains unverified.", outputFailure);
            AppendLog($"{label} finished with code {process.ExitCode}{(isRuntimeSetup ? $" after {(DateTime.UtcNow - startedAt).TotalSeconds:0} seconds" : string.Empty)}.");
            if (isRuntimeSetup && Interlocked.CompareExchange(ref outputLineCount, 0, 0) == 0)
                AppendLog("[PC runtime setup] PowerShell exited without any script output. Check that the complete release ZIP was extracted, then share this Activity log and any Windows Security alert.");
            var controllerFeedback = controllerResult?.Complete(isControllerCompatibility, process.ExitCode);
            if (controllerFeedback?.IsError == true)
            {
                ShowControllerFeedback(controllerFeedback);
                AppendLog($"{label} needs attention: {controllerFeedback.Detail}");
                return false;
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{label} failed with code {process.ExitCode}. See Activity for the exact error and suggested fix.");
            if (setupResult?.Failure() is string setupFailure)
                throw new InvalidOperationException(setupFailure);
            if (script.Equals("uninstall-vrcft-eye-bridge.ps1", StringComparison.OrdinalIgnoreCase) && BridgeInstalled())
                throw new InvalidOperationException("A Qpro module is still installed after the uninstall helper completed. Close VRCFaceTracking and retry.");
            if (isControllerUtility && !isControllerCompatibility && setupResult is not null)
            {
                var uninstall = args.Contains("uninstall", StringComparer.OrdinalIgnoreCase);
                if (uninstall ? Directory.Exists(ControllerAddonRoot) :
                    !File.Exists(Path.Combine(ControllerComponentsRoot, "ready.json")) || !File.Exists(Path.Combine(ControllerAddonRoot, "qpro-owner.json")))
                    throw new InvalidOperationException("The controller helper reported completion, but its installed files did not match the result. Check Activity and repair the optional components.");
            }
            if (isGazeRecovery && !_gazeRecoveryConfirmed)
                throw new InvalidOperationException("The recovery script did not confirm the Qpro session state. Check Activity before starting independent gaze.");
            if (isGazeInspection && !_gazeInspectionConfirmed || isLegacyGazeReset && !_legacyGazeResetConfirmed)
                throw new InvalidOperationException("The gaze action did not confirm its result. Check Activity before restarting gaze.");
            if (isGazeInspection && _gazeInspectionResult is null)
                throw new InvalidOperationException("The gaze setup result was incomplete. Re-extract the complete ZIP and retry Check gaze setup.");
            ReloadProfiles();
            // The wireless connection script already verifies ADB and Magisk root.
            // Show its result without waiting on a second network status probe.
            if (label != "Connect wireless Quest") await RefreshStatusAsync();
            if (controllerFeedback is not null) ShowControllerFeedback(controllerFeedback);
            else SetActionFeedback(label + " completed", "The helper exited successfully and its required result checks passed.", label.StartsWith("Install ", StringComparison.Ordinal) && label.Contains("module", StringComparison.Ordinal) ? "Reopen VRCFaceTracking to load the installed module." : "Continue setup or choose your features on Live tracking.");
            return true;
        }
        catch (Exception error)
        {
            AppendLog($"{label} failed: {error.Message}");
            var failure = HubActionFailure.Explain(label, error.Message, string.Join("\n", recentOutput));
            SetActionFeedback(failure.Title, failure.Detail, failure.NextStep, true);
            if (label == "Connect wireless Quest")
                MessageBox.Show(this,
                    "The Quest did not connect over wireless ADB. Check its current Wi-Fi IP and port, keep the headset awake, and allow Shell / ADB Shell in Magisk if prompted. See Activity for the exact error.",
                    "Quest connection failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (!(script.Equals("native-eye-local-branch-test.ps1", StringComparison.OrdinalIgnoreCase) &&
                args.Contains("-InspectOnly", StringComparer.OrdinalIgnoreCase)))
                MessageBox.Show(this, failure.Detail + "\n\nNext: " + failure.NextStep + "\n\nOpen Activity for the detailed log.", failure.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref acceptingProgress, 0);
            _utilityActionRunning = false;
            UpdateControlState();
        }
    }

    private void BeginTrainingProgress(TongueDatasetKind kind)
    {
        _trainingProgressContainer.Visible = true;
        _trainingStage = 0;
        _trainingStageCount = 2;
        _trainingProgress.Value = 1;
        _trainingProgressStatus.Text = $"Preparing the selected {kind.ToString().ToLowerInvariant()} dataset…";
        _trainingProgressStatus.ForeColor = Warning;
    }

    private void HandleTrainingProgress(string label, string line)
    {
        if (!label.Contains("training", StringComparison.OrdinalIgnoreCase)) return;
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { PostProcessUpdate(label, () => HandleTrainingProgress(label, line)); return; }

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
                ? "CPU fallback active — training is working but slower. GPU owners can verify acceleration in First-time setup afterward."
                : "GPU acceleration active — beginning model training…";
            _trainingProgressStatus.ForeColor = device == "cpu" ? Warning : Good;
            return;
        }
        if (HubTrainingProgress.TryStage(line, out var trainingStage) && trainingStage is not null)
        {
            _trainingStage = trainingStage.Index;
            _trainingStageCount = trainingStage.Total;
            _trainingProgressStatus.Text = $"Training {trainingStage.Name} checkpoint · stage {_trainingStage} of {_trainingStageCount} · 0/{trainingStage.Epochs} epochs";
            _trainingProgressStatus.ForeColor = trainingStage.Device == "cpu" ? Warning : Good;
            _trainingProgress.Value = Math.Clamp((int)Math.Round(5 + 94.0 * (_trainingStage - 1) / _trainingStageCount), 1, 99);
            return;
        }
        if (!HubTrainingProgress.TryEpoch(line, out var epoch) || epoch is null) return;
        var current = epoch.Current;
        var total = epoch.Total;
        var stage = Math.Max(1, _trainingStage);
        var overall = 5 + 94.0 * ((stage - 1) + Math.Clamp((double)current / total, 0, 1)) / Math.Max(1, _trainingStageCount);
        _trainingProgress.Value = Math.Clamp((int)Math.Round(overall), 1, 99);
        _trainingProgressStatus.Text = $"Training {epoch.Focus} checkpoint · stage {stage} of {_trainingStageCount} · {current}/{total} epochs · {_trainingProgress.Value}%";
    }

    private void FinishTrainingProgress(bool succeeded)
    {
        _trainingProgress.Value = succeeded ? 100 : 0;
        _trainingProgressStatus.Text = succeeded
            ? "Training complete — the new model is ready in the model manager."
            : "Training stopped or failed — review Activity for the exact cause.";
        _trainingProgressStatus.ForeColor = succeeded ? Good : Bad;
    }
}
