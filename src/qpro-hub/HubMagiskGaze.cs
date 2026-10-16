using System.Text;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

// The startup check only reads bounded Magisk metadata. Module scripts are
// inspected as text; they are never sourced or executed.
internal static class HubMagiskGazeScan
{
    internal const string ModulePrefix = "__QPRO_GAZE_MODULE__=";
    internal const string Complete = "__QPRO_GAZE_MODULE_SCAN_COMPLETE__";
    internal const string Script = """
        test -d /data/adb/modules && test -r /data/adb/modules && test -x /data/adb/modules || exit 1
        count=0
        for d in /data/adb/modules/* /data/adb/modules/.[!.]* /data/adb/modules/..?*; do
          test -d "$d" || continue
          count=$((count + 1))
          test "$count" -le 128 || exit 1
          test -r "$d/module.prop" || exit 1
          test -e "$d/disable" && continue
          gaze=0
          case "${d##*/}" in questpro_independent_gaze) gaze=1 ;; esac
          for f in "$d/module.prop" "$d/system.prop" "$d/service.sh" "$d/post-fs-data.sh" "$d/sepolicy.rule"; do
            test -f "$f" || continue
            test -r "$f" || exit 1
            size=$(wc -c < "$f") || exit 1
            test "$size" -le 65536 || exit 1
            grep -qiE 'independent.*(eye|gaze)|eyetracking|eye_tracking|bolt[.]ptl|libtrackingengines|social_filtering' "$f"
            result=$?
            test "$result" -gt 1 && exit 1
            test "$result" -eq 0 && gaze=1
          done
          if test "$gaze" -eq 1; then printf '__QPRO_GAZE_MODULE__=%s\n' "${d##*/}"; fi
        done
        echo __QPRO_GAZE_MODULE_SCAN_COMPLETE__
        """;

    internal static string RootShellRequest => "printf %s " +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Script.Replace("\r\n", "\n") + "\n")) +
        " | base64 -d | su -c sh";

    internal static bool TryParse(string output, out IReadOnlyList<string> modules)
    {
        modules = [];
        if (output.Length > 32768) return false;
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines.Length > 129 || lines[^1] != Complete) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines[..^1])
        {
            if (!line.StartsWith(ModulePrefix, StringComparison.Ordinal)) return false;
            var id = line[ModulePrefix.Length..];
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_.-]{1,128}$") || id is "." or ".." || !found.Add(id)) return false;
        }
        modules = found.Order(StringComparer.Ordinal).ToArray();
        return true;
    }
}

// A disappearing USB result is not evidence that a previously detected module
// went away. A different verified target or transport starts a fresh context.
internal sealed class HubMagiskGazeState
{
    private string? _target;
    private bool _wireless;
    private bool _hasContext;
    private bool _attempted;
    private long _generation;
    internal bool Active { get; private set; }

    internal void SelectContext(string? target, bool wireless)
    {
        if (!_hasContext || wireless != _wireless || target is not null && target != _target)
        {
            Active = false;
            _attempted = false;
            _target = target;
            _wireless = wireless;
            _hasContext = true;
            _generation++;
        }
    }

    internal long? BeginScan(string? target, bool wireless)
    {
        SelectContext(target, wireless);
        if (target is null || _attempted) return null;
        _attempted = true;
        return _generation;
    }

    internal bool AcceptScan(long generation, string target, bool wireless, bool active)
    {
        if (generation != _generation || target != _target || wireless != _wireless) return false;
        Active = active;
        return true;
    }

    internal void Observe(string? target, bool wireless, bool active)
    {
        SelectContext(target, wireless);
        Active = active;
        _attempted = true;
        _generation++;
    }

    internal void RetryScan()
    {
        _attempted = false;
        _generation++;
    }

    internal void RetryAfterTargetLoss(long generation, string target, bool wireless)
    {
        // A transient loss discards the result, not the last confirmed method.
        // Only its own pending scan can reopen the attempt; an older completion
        // must not cancel a newer target or an explicit Reconnect retry.
        if (generation != _generation || target != _target || wireless != _wireless) return;
        RetryScan();
    }
}

internal sealed partial class HubForm
{
    private readonly HubMagiskGazeState _magiskGazeState = new();
    private bool _updatingMagiskGazeSelection;
    private bool _magiskGazeConflictReported;

    private bool HasActiveMagiskGaze
    {
        get
        {
            _magiskGazeState.SelectContext(GetConfiguredAdbTarget(), _environment.WirelessSelected);
            return _magiskGazeState.Active;
        }
    }

    private async Task DetectMagiskGazeOnceAsync(bool connected)
    {
        if (_previewOnly || !connected || _closingInProgress || IsDisposed || Disposing) return;
        var target = GetConfiguredAdbTarget();
        var wireless = _environment.WirelessSelected;
        var generation = _magiskGazeState.BeginScan(target, wireless);
        var adb = FindAdb();
        if (generation is null || target is null || adb is null) return;
        try
        {
            var result = await RunAdbProbeAsync(adb, ["-s", target, "shell", HubMagiskGazeScan.RootShellRequest], 8);
            if (_closingInProgress || IsDisposed || Disposing) return;
            var currentTarget = GetConfiguredAdbTarget();
            if (wireless != _environment.WirelessSelected || target != currentTarget)
            {
                if (wireless == _environment.WirelessSelected && currentTarget is null)
                    _magiskGazeState.RetryAfterTargetLoss(generation.Value, target, wireless);
                return;
            }
            if (!result.Completed || result.ExitCode != 0 || !HubMagiskGazeScan.TryParse(result.Output, out var modules))
            {
                AppendLog("[Gaze method] WARNING: The optional Magisk module check did not complete. The last confirmed gaze method was kept. Reconnect USB or check headset compatibility to retry; other features can still be used.");
                return;
            }
            if (!_magiskGazeState.AcceptScan(generation.Value, target, wireless, modules.Count > 0)) return;
            if (modules.Count == 0 && _lastGazeStatusObservation?.Inspection?.NeedsAttention != true &&
                (_lastGazeStatusObservation?.Compatibility?.HasActiveMagiskGaze == true ||
                 _lastGazeStatusObservation?.Inspection?.HasActiveMagiskGaze == true))
                _lastGazeStatusObservation = null;
            UpdateGazeSetupStatus();
            AppendLog(modules.Count > 0
                ? "[Gaze method] Independent eye gaze is provided by Magisk (" + string.Join(", ", modules) + "). Hub gaze is disabled; skip Check gaze setup and Prepare gaze. Pupil, eyebrow and tongue features remain separate."
                : "[Gaze method] No active Magisk gaze module was found. Hub independent gaze remains optional.");
        }
        catch (Exception error)
        {
            AppendLog("[Gaze method] WARNING: The optional Magisk module check could not complete: " + error.Message);
        }
    }

    private void ApplyMagiskGazeControlGate(bool sessionEditable)
    {
        var magisk = HasActiveMagiskGaze;
        if (magisk && sessionEditable && _gaze.Checked && !_updatingMagiskGazeSelection)
        {
            _updatingMagiskGazeSelection = true;
            try
            {
                _gaze.Checked = false;
                if (!_previewOnly && _environment.IndependentGazeEnabled)
                    _environment.SelectIndependentGaze(false);
            }
            catch (Exception error) { AppendLog("Could not save independent gaze preference: " + error.Message); }
            finally { _updatingMagiskGazeSelection = false; }
        }
        _gaze.Enabled = sessionEditable && !magisk;
        _eyeProfiles.Enabled = sessionEditable && !magisk && _gaze.Checked;
        var label = magisk ? "Independent eye gaze + convergence (provided by Magisk)" : "Independent eye gaze + convergence";
        if (_gaze.Text != label) _gaze.Text = label;
        _gaze.AccessibleDescription = magisk
            ? "Provided by the detected Magisk gaze module. Hub gaze is disabled. Skip Check gaze setup and Prepare gaze; pupil and eyebrow controls work separately. After changing Magisk modules, reboot and run Check headset compatibility to refresh detection."
            : "Optional temporary independent gaze provided by Qpro Companion.";

        // Changing the next selection must not imply that an existing worker
        // stopped or that its saved headset state has already been restored.
        if (magisk && !sessionEditable && _gaze.Checked && !_magiskGazeConflictReported)
        {
            _magiskGazeConflictReported = true;
            AppendLog("[Gaze method] WARNING: Magisk gaze was detected during a Qpro gaze session. Press Stop tracking and allow cleanup to finish. Hub gaze will be switched off afterward; Magisk is not changed.");
            SetActionFeedback("Two gaze methods need attention",
                "Magisk gaze was detected while Qpro gaze was selected. The running session has not been silently changed.",
                "Press Stop tracking and wait for cleanup. Hub gaze will then stay off; keep using Magisk gaze with your other features.",
                severity: ActivitySeverity.Warning);
        }
        if (!magisk || sessionEditable) _magiskGazeConflictReported = false;
    }
}
