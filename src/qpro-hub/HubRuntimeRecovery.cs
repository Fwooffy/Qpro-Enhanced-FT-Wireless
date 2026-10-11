using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly DarkButton _repairRuntimeButton = SetupButton("Repair runtime");
    private readonly DarkButton _checkRuntimeRecoveryButton = SetupButton("Find saved runtimes");
    private readonly DarkButton _restoreRuntimeButton = SetupButton("Restore saved runtime");
    private readonly ComboBox _runtimeRecoveryCopies = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly Label _runtimeRecoveryNote = Info("Repair reinstalls the private PC runtime's libraries. Restore checks a saved runtime before replacing the current one. Models, recordings and settings stay in place.");
    private bool _runtimeRecoveryChecking;

    private Control BuildRuntimeRecoveryControls()
    {
        var recovery = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Margin = Padding.Empty,
        };
        recovery.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        recovery.Controls.Add(_runtimeRecoveryNote);
        recovery.Controls.Add(_runtimeRecoveryCopies);
        recovery.Controls.Add(_checkRuntimeRecoveryButton);
        recovery.Controls.Add(_restoreRuntimeButton);
        recovery.Controls.Add(_repairRuntimeButton);
        _runtimeRecoveryCopies.SelectedIndexChanged += (_, _) => UpdateRuntimeRecoveryButtons(_setupRuntimeButton.Enabled);
        _checkRuntimeRecoveryButton.Click += async (_, _) => await RefreshRuntimeRecoveryCopiesAsync();
        _restoreRuntimeButton.Click += async (_, _) => await RestoreSavedRuntimeAsync();
        _repairRuntimeButton.Click += async (_, _) =>
        {
            if (_previewOnly || UtilityActionIsBusy()) return;
            await RunSetupStepAsync("PC runtime repair", "setup-runtime.ps1", "The private PC runtime was repaired and verified.",
                "Start tracking again. If an error repeats, copy the detailed Activity log.", "-Repair");
            await RefreshRuntimeRecoveryCopiesAsync();
        };
        Shown += async (_, _) => await RefreshRuntimeRecoveryCopiesAsync();
        UpdateRuntimeRecoveryButtons(false);
        return recovery;
    }

    private void UpdateRuntimeRecoveryButtons(bool enabled)
    {
        enabled &= !_runtimeRecoveryChecking;
        _repairRuntimeButton.Enabled = enabled;
        _checkRuntimeRecoveryButton.Enabled = enabled;
        _runtimeRecoveryCopies.Enabled = enabled && _runtimeRecoveryCopies.Items.Count > 0;
        _restoreRuntimeButton.Enabled = enabled && _runtimeRecoveryCopies.SelectedItem is RuntimeRecoveryChoice;
    }

    private async Task RefreshRuntimeRecoveryCopiesAsync()
    {
        if (_previewOnly || _runtimeRecoveryChecking || IsDisposed || _closingInProgress || _starting || _stopping ||
            _trackingCleanupPending || _gazeRecoveryRunning || _setupActionRunning || _utilityActionRunning || _datasetOperationBusy || LiveTrackingRunning) return;
        _runtimeRecoveryChecking = true;
        UpdateRuntimeRecoveryButtons(false);
        try
        {
            // This is an explicit metadata scan, not another periodic status or
            // interpreter probe. Restore performs full inventory and tensor checks.
            var start = PowerShellStart("runtime-recovery.ps1", ["-Action", "List"], hidden: true);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("Could not start the runtime recovery check.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new IOException("Finding saved runtimes took too long. Press Find saved runtimes to retry.");
            }
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "The recovery check failed. Retry after the current runtime setup finishes." : error.Trim());
            const string prefix = "QPRO_RUNTIME_RECOVERY ";
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (lines.Length != 1 || lines[0].Length > 1_048_576) throw new IOException("The recovery check returned an incomplete result.");
            var result = JsonSerializer.Deserialize<RuntimeRecoveryList>(lines[0][prefix.Length..]);
            if (result?.Format != "qpro-runtime-recovery-list-v1" || result.Copies is null || result.Copies.Length > 1024 || result.Unavailable < 0 || result.Interrupted < 0 ||
                result.Copies.Any(copy => !RuntimeRecoveryChoice.Valid(copy))) throw new IOException("The saved-runtime list could not be validated.");
            if (IsDisposed || Disposing || _closingInProgress) return;
            _runtimeRecoveryCopies.BeginUpdate();
            try
            {
                _runtimeRecoveryCopies.Items.Clear();
                foreach (var copy in result.Copies) _runtimeRecoveryCopies.Items.Add(new RuntimeRecoveryChoice(copy));
                if (_runtimeRecoveryCopies.Items.Count > 0) _runtimeRecoveryCopies.SelectedIndex = 0;
            }
            finally { _runtimeRecoveryCopies.EndUpdate(); }
            _runtimeRecoveryCopies.Visible = result.Copies.Length > 0;
            _restoreRuntimeButton.Visible = result.Copies.Length > 0;
            _runtimeRecoveryNote.ForeColor = result.Interrupted > 0 ? Warning : Muted;
            _runtimeRecoveryNote.Text = result.Interrupted > 0
                ? "An interrupted runtime restore was found. Restore a saved copy or use Repair runtime. Each restore validates the saved libraries first; models and settings stay in place."
                : result.Copies.Length > 0
                    ? $"{result.Copies.Length} saved runtime(s) available. Restore checks imports and tensors first and keeps the current runtime as another recovery copy. Repair reinstalls this release's libraries."
                    : "No ready saved runtime was found. Repair reinstalls this release's private runtime libraries. Models, recordings and settings stay in place.";
            if (result.Unavailable > 0) _runtimeRecoveryNote.Text += $" {result.Unavailable} incomplete or unready copy/copies cannot be restored from the Hub.";
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                _runtimeRecoveryNote.Text = "Saved runtimes could not be checked. Press Find saved runtimes to retry. " + error.Message;
                _runtimeRecoveryNote.ForeColor = Warning;
                AppendLog("[PC runtime recovery] " + error.Message);
            }
        }
        finally
        {
            _runtimeRecoveryChecking = false;
            if (!IsDisposed && !Disposing) UpdateRuntimeRecoveryButtons(_setupRuntimeButton.Enabled);
        }
    }

    private async Task RestoreSavedRuntimeAsync()
    {
        if (_previewOnly || UtilityActionIsBusy() || _runtimeRecoveryCopies.SelectedItem is not RuntimeRecoveryChoice selected) return;
        if (MessageBox.Show(this,
                $"Check and restore {selected}?\n\nQpro will verify the saved Python, libraries and tensor operations before replacing its PC runtime. The current runtime will be kept as a recovery copy. This does not change AMD ROCm environments, headset tracking, models or recordings.",
                "Restore saved PC runtime", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunSetupStepAsync("PC runtime restore", "runtime-recovery.ps1", "The saved PC runtime was verified and restored.",
            "Start tracking again. Component updates may offer this release's newer runtime libraries.", "-Action", "Restore", "-BackupName", selected.Copy.Name!);
        await RefreshRuntimeRecoveryCopiesAsync();
    }

    private sealed record RuntimeRecoveryCopy(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("completedUtc")] string? CompletedUtc,
        [property: JsonPropertyName("bytes")] long Bytes,
        [property: JsonPropertyName("backend")] string? Backend);
    private sealed record RuntimeRecoveryList(
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("copies")] RuntimeRecoveryCopy[]? Copies,
        [property: JsonPropertyName("unavailable")] int Unavailable,
        [property: JsonPropertyName("interrupted")] int Interrupted);
    private sealed record RuntimeRecoveryChoice(RuntimeRecoveryCopy Copy)
    {
        internal static bool Valid(RuntimeRecoveryCopy copy) => copy is not null && copy.Name is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(copy.Name, "^runtime-backup-[0-9a-f]{32}$") &&
            DateTimeOffset.TryParse(copy.CompletedUtc, out _) && copy.Bytes > 0 && copy.Backend is null or "cpu" or "cuda";
        public override string ToString() => $"{DateTimeOffset.Parse(Copy.CompletedUtc!).ToLocalTime():g} · {(Copy.Backend == "cuda" ? "NVIDIA CUDA" : "PC runtime")} · {Copy.Bytes / (1024D * 1024):N0} MB";
    }
}
