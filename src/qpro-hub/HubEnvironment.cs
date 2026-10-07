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

namespace QproFaceTracking.Hub;

internal sealed class HubEnvironment
{
    private readonly string _root;
    private string? _usbSerial;
    private readonly HubAdbSession _adbSession = new();
    private readonly object _runtimeProbeLock = new();
    private readonly RuntimeProbeState _configuredRuntimeProbe = new();
    private readonly RuntimeProbeState _sharedRuntimeProbe = new();

    private sealed class RuntimeProbeState
    {
        internal string? Identity;
        internal Task<bool>? Task;
        internal DateTime StartedUtc;
        internal bool LastKnownReady;
    }
    internal bool WirelessSelected { get; private set; }
    internal bool SteamLinkSelected { get; private set; }
    internal string TrackingSourceArgument => SteamLinkSelected ? "SteamLink" : "VirtualDesktop";
    // Preview fixtures set only this in-memory value; preferences are untouched.
    internal void SetPreviewTrackingSource(bool steamLink) => SteamLinkSelected = steamLink;
    private static string TrackingSourcePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QproFaceTracking", "config", "tracking-source.txt");
    internal bool CameraPreviewEnabled { get; private set; }
    internal bool IndependentGazeEnabled { get; private set; }
    internal bool HasOpenedBefore => File.Exists(Path.Combine(_root, "config", "hub-opened.txt"));

    internal HubEnvironment(string root)
    {
        _root = root;
        var modePath = Path.Combine(_root, "config", "connection-mode.txt");
        if (File.Exists(modePath))
        {
            try { WirelessSelected = File.ReadAllText(modePath).Trim().Equals("wireless", StringComparison.OrdinalIgnoreCase); }
            catch { WirelessSelected = false; }
        }
        else
        {
            WirelessSelected = File.Exists(Path.Combine(_root, "config", "wireless-headset.json"));
        }
        ReloadTrackingSource();
        var previewPath = Path.Combine(_root, "config", "camera-preview.txt");
        if (File.Exists(previewPath))
        {
            try { CameraPreviewEnabled = File.ReadAllText(previewPath).Trim().Equals("on", StringComparison.OrdinalIgnoreCase); }
            catch { CameraPreviewEnabled = false; }
        }
        var gazePath = Path.Combine(_root, "config", "independent-gaze.txt");
        if (File.Exists(gazePath))
        {
            try { IndependentGazeEnabled = !File.ReadAllText(gazePath).Trim().Equals("off", StringComparison.OrdinalIgnoreCase); }
            catch { IndependentGazeEnabled = false; }
        }
    }

    internal void SelectCameraPreview(bool enabled)
    {
        var directory = Path.Combine(_root, "config");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "camera-preview.txt"), enabled ? "on" : "off");
        CameraPreviewEnabled = enabled;
    }

    internal void SelectIndependentGaze(bool enabled)
    {
        var directory = Path.Combine(_root, "config");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "independent-gaze.txt"), enabled ? "on" : "off");
        IndependentGazeEnabled = enabled;
    }

    internal void MarkOpened()
    {
        var directory = Path.Combine(_root, "config");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "hub-opened.txt"), "opened");
    }

    internal void SelectConnection(bool wireless)
    {
        var directory = Path.Combine(_root, "config");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "connection-mode.txt"), wireless ? "wireless" : "usb");
        WirelessSelected = wireless;
        _usbSerial = null;
    }

    internal void SelectTrackingSource(bool steamLink)
    {
        var directory = Path.GetDirectoryName(TrackingSourcePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(TrackingSourcePath, steamLink ? "steam-link" : "virtual-desktop");
        SteamLinkSelected = steamLink;
    }

    internal void ReloadTrackingSource()
    {
        try
        {
            SteamLinkSelected = File.Exists(TrackingSourcePath) &&
                File.ReadAllText(TrackingSourcePath).Trim().Equals("steam-link", StringComparison.OrdinalIgnoreCase);
        }
        catch { SteamLinkSelected = false; }
    }

    internal bool TrackingSourceRequiresVrcftRestart()
    {
        if (!File.Exists(TrackingSourcePath)) return false;
        var selectedAt = File.GetLastWriteTimeUtc(TrackingSourcePath);
        foreach (var process in Process.GetProcessesByName("VRCFaceTracking"))
        {
            using (process)
            {
                try
                {
                    if (process.StartTime.ToUniversalTime() < selectedAt) return true;
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { return true; }
            }
        }
        return false;
    }

    internal string? GetConfiguredAdbTarget()
    {
        if (!WirelessSelected) return _usbSerial;
        var saved = GetSavedWirelessTarget();
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        foreach (var value in new[] { Environment.GetEnvironmentVariable("QPRO_ADB_TARGET"), Environment.GetEnvironmentVariable("ANDROID_SERIAL") })
            if (!string.IsNullOrWhiteSpace(value) && value.Contains(':')) return value.Trim();
        return null;
    }

    internal string? GetSavedWirelessTarget()
    {
        var configPath = Path.Combine(_root, "config", "wireless-headset.json");
        try
        {
            if (File.Exists(configPath))
                return JsonNode.Parse(File.ReadAllText(configPath))?["adbTarget"]?.GetValue<string>()?.Trim();
        }
        catch { /* An invalid local config must not crash the hub. */ }
        return null;
    }

    internal async Task<bool> HasQuestAsync()
    {
        var adb = FindAdb();
        if (adb is null) return false;
        if (!WirelessSelected)
        {
            var devices = await RunAdbProbeAsync(adb, ["devices"], 3);
            if (!devices.Completed || devices.ExitCode != 0) return false;
            var usb = devices.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[1].Equals("device", StringComparison.OrdinalIgnoreCase)
                    && !parts[0].Contains(':'))
                .Select(parts => parts[0]).ToArray();
            _usbSerial = usb.Length == 1 ? usb[0] : null;
            return _usbSerial is not null;
        }
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
        return false;
    }

    internal Task<(bool Completed, int ExitCode, string Output)> RunAdbProbeAsync(
        string adb, IEnumerable<string> arguments, int timeoutSeconds = 4)
        => _adbSession.ProbeAsync(adb, arguments, timeoutSeconds);

    internal Task SuspendAdbProbesAsync() => _adbSession.SuspendProbesAsync();
    internal void ResumeAdbProbes() => _adbSession.ResumeProbes();
    internal async Task<(bool Completed, int ExitCode, string Output)> StopAdbServerAsync()
    {
        await _adbSession.SuspendProbesAsync();
        var adb = FindAdb();
        return adb is null ? (false, -1, "ADB executable is missing; server shutdown could not be confirmed.")
            : await _adbSession.StopServerAsync(adb);
    }

    internal string? FindAdb()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("QPRO_ADB"),
            Path.Combine(_root, "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string CustomLibsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "CustomLibs");
    internal bool BridgeInstalled() => HubModuleInstallation.HasInstalled(CustomLibsPath);

    internal bool CurrentBridgeInstalled()
    {
        var supplied = Path.Combine(_root, "vrcft-gaze-bridge", "bin", "Release", "net10.0", "Qpro.GazeBridge.dll");
        return HubModuleInstallation.IsCurrent(CustomLibsPath, supplied, SteamLinkSelected);
    }
    internal bool BackendReady() => FindPythonRuntime() is not null;
    internal bool EyeModelReady()
    {
        var prepared = Path.Combine(_root, "research", "seacliff_eye_model");
        return File.Exists(Path.Combine(prepared, "bolt-independent-axes.ptl"))
            && File.Exists(Path.Combine(prepared, "bolt-independent-axes.manifest.json"));
    }
    internal string? FindPythonRuntime()
    {
        var configuredPython = Environment.GetEnvironmentVariable("QPRO_PYTHON");
        if (!string.IsNullOrWhiteSpace(configuredPython) && File.Exists(configuredPython))
        {
            var configuredReady = VerifiedPythonRuntime(configuredPython, "configured", _configuredRuntimeProbe);
            if (configuredReady is not null) return configuredReady;
        }
        // A stale explicit override should not hide a healthy Qpro runtime.

        var sharedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "runtime");
        var sharedPython = Path.Combine(sharedRoot, ".venv", "Scripts", "python.exe");
        var sharedReadyMarker = Path.Combine(sharedRoot, "runtime-ready.json");
        if (!File.Exists(sharedPython) || !File.Exists(sharedReadyMarker)) return null;
        try
        {
            var markerFile = new FileInfo(sharedReadyMarker);
            if (markerFile.Length > 4096) return null;
            var markerText = File.ReadAllText(sharedReadyMarker);
            var marker = JsonNode.Parse(markerText);
            var recordedPython = marker?["python"]?.GetValue<string>();
            return marker?["format"]?.GetValue<string>() == "qpro-runtime-ready-v1"
                && !string.IsNullOrWhiteSpace(recordedPython)
                && string.Equals(Path.GetFullPath(recordedPython), Path.GetFullPath(sharedPython), StringComparison.OrdinalIgnoreCase)
                ? VerifiedPythonRuntime(sharedPython, markerText, _sharedRuntimeProbe) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? VerifiedPythonRuntime(string python, string markerIdentity, RuntimeProbeState cache)
    {
        try
        {
            var executable = new FileInfo(python);
            var identity = $"{executable.FullName}|{executable.Length}|{executable.LastWriteTimeUtc.Ticks}|{markerIdentity}";
            lock (_runtimeProbeLock)
            {
                var now = DateTime.UtcNow;
                if (cache.Identity != identity)
                {
                    cache.Identity = identity;
                    cache.LastKnownReady = false;
                    cache.StartedUtc = now;
                    cache.Task = Task.Run(() => ProbePythonRuntimeAsync(executable.FullName));
                    return null;
                }
                if (cache.Task?.IsCompleted == true)
                    cache.LastKnownReady = cache.Task.IsCompletedSuccessfully && cache.Task.Result;
                // A changed readiness marker invalidates the cached result;
                // timed retries also notice repairs made outside the Hub.
                var retryAfter = cache.LastKnownReady ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
                // Importing PyTorch may take seconds. Start it off the UI thread,
                // and reuse the result across the Hub's frequent status refreshes.
                if (cache.Task is null ||
                    (cache.Task.IsCompleted && now - cache.StartedUtc >= retryAfter))
                {
                    cache.StartedUtc = now;
                    cache.Task = Task.Run(() => ProbePythonRuntimeAsync(executable.FullName));
                    // Keep the previous verified status while the periodic
                    // recheck runs; only a changed marker clears it at once.
                }
                return cache.LastKnownReady ? executable.FullName : null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<bool> ProbePythonRuntimeAsync(string python)
    {
        try
        {
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import cv2,numpy,torch; assert hasattr(cv2,'namedWindow')");
            // Bound both process execution and pipe draining. An import may
            // leave a descendant holding stdout/stderr after Python exits.
            // An incomplete drain is not a verified runtime and must complete
            // the cached task so a later status refresh can retry.
            var result = await HubProcessResult.RunAsync(start, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return !result.TimedOut && result.CleanupError is null && result.ExitCode == 0;
        }
        catch { return false; }
    }
}
