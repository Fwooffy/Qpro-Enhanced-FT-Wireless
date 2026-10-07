using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly ComboBox _connectionMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _connectionModeNote = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly TableLayoutPanel _wirelessSetup = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false };
    private readonly TextBox _wirelessAddress = new() { Dock = DockStyle.Fill };
    private readonly TextBox _pairingEndpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _pairingCode = new() { Dock = DockStyle.Fill, MaxLength = 6, UseSystemPasswordChar = true };
    private readonly DarkButton _enableWirelessButton = SetupButton("Enable from USB");
    private readonly DarkButton _connectWirelessButton = SetupButton("Connect to Quest");
    private readonly DarkButton _pairWirelessButton = SetupButton("Pair and connect");
    private readonly DarkButton _disableWirelessButton = SetupButton("Disable Wi-Fi ADB");
    private readonly Label _setupAmdStatus = SetupStatusLabel();
    private readonly Label _amdGpuStatus = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(5, 3, 5, 8), Tag = "responsive-info" };
    private readonly DarkButton _setupAmdButton = SetupButton($"Install ROCm {HubRocmRuntime.InstallVersion}");
    private readonly CheckBox _experimentalWindows10Rocm = new()
    {
        Text = "Try ROCm on Windows 10 (experimental)", AutoSize = true,
        ForeColor = Muted, Margin = new Padding(5, 6, 5, 6), Checked = false,
        AccessibleDescription = "Optional Windows 10 22H2 experiment. AMD validates Windows 11. GPU training and inference checks still must pass."
    };
    private bool _amdGpuSupported;
    private bool _amdGpuLegacyEligible;
    private string? _amdGpuLatestTarget;
    private string? _amdGpuName;
    private bool _amdGpuDetected;
    private bool _rocmInstallRunning;
    private bool _connectionSelectionUpdating;
    private bool AmdInstallEligible => _amdGpuSupported && HubRocmOsPolicy.CanInstall(
        Environment.OSVersion.Version.Build, _experimentalWindows10Rocm.Checked);

    private void InitializeIntegratedSetup()
    {
        _setupAmdButton.Enabled = false;
        _experimentalWindows10Rocm.Visible = HubRocmOsPolicy.IsWindows10OptInEligible(Environment.OSVersion.Version.Build);
        _experimentalWindows10Rocm.CheckedChanged += (_, _) =>
        {
            UpdateAmdGpuStatus();
            UpdateSetupStepStyles();
        };
        _connectionMode.Items.AddRange(["USB cable", "Wireless ADB (Wi-Fi)"]);
        _connectionMode.SelectedIndex = _environment.WirelessSelected ? 1 : 0;
        _wirelessAddress.Text = _environment.GetSavedWirelessTarget() ?? "";
        _connectionMode.SelectedIndexChanged += async (_, _) =>
        {
            if (_previewOnly) return;
            if (_connectionSelectionUpdating) return;
            if (_setupActionRunning || _utilityActionRunning || _datasetOperationBusy ||
                _starting || _stopping || _trackingProcesses.Any(process => !process.HasExited))
            {
                _connectionSelectionUpdating = true;
                try { _connectionMode.SelectedIndex = _environment.WirelessSelected ? 1 : 0; }
                finally { _connectionSelectionUpdating = false; }
                return;
            }
            try
            {
                _environment.SelectConnection(_connectionMode.SelectedIndex == 1);
                UpdateConnectionModeUi();
                AppendLog($"Quest connection selected: {(_environment.WirelessSelected ? "wireless ADB" : "USB")}.");
                await RefreshStatusAsync();
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Could not save connection choice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _connectionSelectionUpdating = true;
                _connectionMode.SelectedIndex = _environment.WirelessSelected ? 1 : 0;
                _connectionSelectionUpdating = false;
            }
        };
        _enableWirelessButton.Click += async (_, _) => { await RunConnectionStepAsync("Enable wireless ADB from USB", "Enable-QproWireless.ps1"); };
        _connectWirelessButton.Click += async (_, _) =>
        {
            var target = NormalizeQuestAddress(_wirelessAddress.Text);
            if (target is null) return;
            _wirelessAddress.Text = target;
            if (await RunConnectionStepAsync("Connect wireless Quest", "Connect-QproWireless.ps1", "-AdbTarget", target))
                MessageBox.Show(this,
                    $"Connected to your Quest at {target}.\n\nWireless ADB and Magisk root are ready. Continue with the next setup card; pairing is not needed.",
                    "Quest connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        _pairWirelessButton.Click += async (_, _) =>
        {
            var target = NormalizeQuestAddress(_wirelessAddress.Text);
            if (target is null) return;
            if (string.Equals(target, _environment.GetSavedWirelessTarget(), StringComparison.OrdinalIgnoreCase)
                && await HasQuestAsync())
            {
                AppendLog("Wireless Quest is already connected. Pairing is unnecessary; verifying ADB and Magisk root instead.");
                await RunConnectionStepAsync("Connect wireless Quest", "Connect-QproWireless.ps1", "-AdbTarget", target);
                return;
            }
            var endpoint = NormalizeQuestAddress(_pairingEndpoint.Text, requirePort: true);
            if (endpoint is null) return;
            if (string.Equals(endpoint, target, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "The pairing address must use the temporary port shown with the six-digit code. " +
                    "The regular Quest IP:port is for Connect to Quest.",
                    "Use the pairing port", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(_pairingCode.Text, "^[0-9]{6}$"))
            {
                MessageBox.Show(this, "Enter the six-digit code shown in the headset pairing dialog.", "Pairing code needed");
                return;
            }
            try
            {
                await RunConnectionStepAsync("Pair wireless Quest", "Pair-QproWireless.ps1",
                    "-PairingEndpoint", endpoint, "-AdbTarget", target, "-PairingCode", _pairingCode.Text);
            }
            finally { _pairingCode.Clear(); }
        };
        _disableWirelessButton.Click += async (_, _) => { await RunConnectionStepAsync("Disable wireless ADB", "Disable-QproWireless.ps1"); };
        _setupAmdButton.Click += async (_, _) =>
        {
            if (!BackendReady())
            {
                MessageBox.Show(this, "Install the PC runtime first, then install AMD ROCm.", "PC runtime needed");
                return;
            }
            if (!AmdInstallEligible)
            {
                var reason = _amdGpuSupported
                    ? HubRocmOsPolicy.BlockedReason(Environment.OSVersion.Version.Build, _experimentalWindows10Rocm.Checked)
                    : "AMD ROCm requires an eligible discrete Radeon GPU. On an NVIDIA-only PC, use Install runtime for NVIDIA CUDA; integrated graphics cannot use this ROCm setup.";
                MessageBox.Show(this, reason, "ROCm setup unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var windows10Notice = HubRocmOsPolicy.IsWindows10OptInEligible(Environment.OSVersion.Version.Build)
                ? "Windows 10 support is experimental and is outside AMD's supported Windows configuration. Setup may fail with your card or driver. Your current PC runtime remains available.\n\n"
                : "";
            if (MessageBox.Show(this,
                    windows10Notice + $"Install ROCm {HubRocmRuntime.InstallVersion} for Qpro's experimental AMD acceleration. The download is large. Setup tests GPU training and model inference before enabling the new runtime. Existing verified ROCm runtimes stay available if those checks fail; Qpro can also use its PC runtime.\n\nInstall ROCm {HubRocmRuntime.InstallVersion} on this PC?",
                    $"Install ROCm {HubRocmRuntime.InstallVersion}", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            _rocmInstallRunning = true;
            _experimentalWindows10Rocm.Enabled = false;
            try
            {
                await RunSetupStepAsync($"AMD ROCm {HubRocmRuntime.InstallVersion} setup", "Install-QproRocm.ps1",
                    $"ROCm {HubRocmRuntime.InstallVersion} passed its GPU checks. Tongue inference and training will use it automatically.",
                    "You can now start tracking or train a personal model.",
                    HubRocmOsPolicy.InstallArguments(Environment.OSVersion.Version.Build, _experimentalWindows10Rocm.Checked));
            }
            finally
            {
                _rocmInstallRunning = false;
                _experimentalWindows10Rocm.Enabled = !_setupActionRunning;
                UpdateSetupStepStyles();
            }
        };
        UpdateConnectionModeUi();
        if (!_previewOnly) _ = DetectAmdGpuAsync();
    }

    private void UpdateConnectionModeUi()
    {
        _wirelessSetup.Visible = _environment.WirelessSelected;
        _connectionModeNote.Text = _environment.WirelessSelected
            ? "Enter the Quest's Wi-Fi address below. The headset and PC must be on the same trusted network."
            : "Connect the rooted Quest by USB and approve its debugging prompt in the headset.";
        _connectionModeNote.ForeColor = Muted;
    }

    private string? NormalizeQuestAddress(string value, bool requirePort = false)
    {
        value = value.Trim();
        var parts = value.Split(':');
        if ((!requirePort && parts.Length == 1) && IPAddress.TryParse(parts[0], out var ipOnly)
            && ipOnly.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return value + ":5555";
        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && int.TryParse(parts[1], out var port) && port is >= 1024 and <= 65535)
            return value;
        MessageBox.Show(this, requirePort
            ? "Enter the exact IPv4 address and port shown by Wireless debugging, for example 192.168.1.50:37123."
            : "Enter the Quest's IPv4 address, with its ADB port if it is not 5555.",
            "Invalid Quest address");
        return null;
    }

    private async Task<bool> RunConnectionStepAsync(string label, string script, params string[] args)
    {
        if (!TryBeginSetupProgress(label)) return false;
        var succeeded = await RunUtilityAsync(label, script, args);
        FinishSetupProgress(succeeded, label);
        if (!succeeded) return false;
        if (script.Equals("Disable-QproWireless.ps1", StringComparison.OrdinalIgnoreCase))
        {
            _wirelessAddress.Clear();
            _connectionMode.SelectedIndex = 0;
        }
        else _wirelessAddress.Text = _environment.GetSavedWirelessTarget() ?? _wirelessAddress.Text;
        AppendLog($"{label} completed. The Hub will use the selected connection for tracking.");
        if (script.Equals("Connect-QproWireless.ps1", StringComparison.OrdinalIgnoreCase))
            _ = RefreshStatusAsync();
        else
            await RefreshStatusAsync();
        return true;
    }

    private bool LatestRocmEnvironmentExists() => HubRocmRuntime.Candidates(_root, legacy: false)
        .Any(path => File.Exists(Path.Combine(path, "Scripts", "python.exe")));
    private bool LegacyRocmEnvironmentExists() => HubRocmRuntime.Candidates(_root, legacy: true)
        .Any(path => File.Exists(Path.Combine(path, "Scripts", "python.exe")));
    private string? LatestRocmInstalledVersion() => HubRocmRuntime.Candidates(_root, legacy: false)
        .Select(path => HubRocmRuntime.ReadyVersion(path, legacy: false, _amdGpuLatestTarget))
        .OfType<string>()
        .OrderByDescending(version => version, StringComparer.Ordinal)
        .FirstOrDefault();
    private bool LegacyRocmInstalled() => _amdGpuLegacyEligible && HubRocmRuntime.Candidates(_root, legacy: true)
        .Any(path => HubRocmRuntime.IsReady(path, legacy: true, _amdGpuLatestTarget));

    private async Task DetectAmdGpuAsync()
    {
        _amdGpuStatus.Text = "Checking for an eligible discrete Radeon GPU…";
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-Command", "Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name + '|' + $_.PNPDeviceID }" })
                info.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("GPU query did not start");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException("GPU query timed out");
            }
            var controllers = (await output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var separator = line.IndexOf('|');
                    return (Name: separator < 0 ? line : line[..separator],
                        PnpId: separator < 0 ? string.Empty : line[(separator + 1)..]);
                }).ToArray();
            if (process.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
            // Prefer the same discrete adapter as the installer when a PC has
            // more than one: first an old 7.2.1-listed card, then another mapped card.
            var match = controllers.FirstOrDefault(controller => IsLegacyAmdAdapter(controller.Name, controller.PnpId));
            if (match.Name is null)
                match = controllers.FirstOrDefault(controller => LatestRocmTarget(controller.Name, controller.PnpId) is not null);
            _amdGpuLatestTarget = match.Name is not null ? LatestRocmTarget(match.Name, match.PnpId) : null;
            _amdGpuName = match.Name?.Trim();
            _amdGpuLegacyEligible = match.Name is not null && IsLegacyAmdAdapter(match.Name, match.PnpId);
            _amdGpuDetected = controllers.Any(controller => controller.PnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)
                || controller.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                || controller.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
            var nvidiaDetected = controllers.Any(controller => controller.PnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)
                || controller.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            _amdGpuSupported = _amdGpuLatestTarget is not null;
            _amdGpuStatus.Text = match.Name is not null
                ? $"ROCm {HubRocmRuntime.InstallVersion} install target: {_amdGpuName} ({_amdGpuLatestTarget})"
                : nvidiaDetected
                    ? "NVIDIA GPU detected. Use Install runtime for CUDA; an AMD integrated GPU does not support this ROCm setup."
                    : _amdGpuDetected
                        ? "No eligible AMD discrete GPU found. Integrated graphics cannot use this ROCm setup."
                        : "No supported AMD GPU detected. AMD ROCm is unavailable on this PC.";
            UpdateAmdGpuStatus();
        }
        catch (Exception error)
        {
            _amdGpuStatus.Text = "GPU detection unavailable. AMD ROCm is disabled until the supported GPU can be detected.";
            AppendLog("GPU detection unavailable: " + error.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            _amdGpuSupported = false;
            _amdGpuLegacyEligible = false;
            _amdGpuLatestTarget = null;
            _amdGpuName = null;
            _amdGpuDetected = false;
        }
        if (IsDisposed || Disposing) return;
        UpdateSetupStepStyles();
        SetSetupButtonsEnabled(true);
    }

    private void UpdateAmdGpuStatus()
    {
        if (!_amdGpuSupported || _amdGpuName is null) return;
        var build = Environment.OSVersion.Version.Build;
        var reason = HubRocmOsPolicy.BlockedReason(build, _experimentalWindows10Rocm.Checked);
        _amdGpuStatus.Text = $"ROCm {HubRocmRuntime.InstallVersion} install target: {_amdGpuName} ({_amdGpuLatestTarget})." +
            (reason is not null ? " " + reason : HubRocmOsPolicy.IsWindows10OptInEligible(build)
                ? " Windows 10 experiment enabled; GPU training and inference must pass."
                : " GPU training and inference must pass before Qpro uses this runtime.");
        _experimentalWindows10Rocm.Enabled = !_setupActionRunning && !_rocmInstallRunning;
    }

    private static string NormalizeAmdName(string name)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(name, @"\((?:TM|R)\)", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "[™®]", " ");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();
        if (normalized.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase)) normalized = normalized[4..];
        var model = System.Text.RegularExpressions.Regex.Match(normalized,
            @"^(?:Radeon\s*)?RX\s*(\d{4})\s*(XTX|XT|GRE)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return model.Success
            ? $"Radeon RX {model.Groups[1].Value}" + (model.Groups[2].Success ? " " + model.Groups[2].Value : "")
            : normalized;
    }

    private static string? LatestRocmTarget(string name, string pnpId)
    {
        if (!pnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return null;
        var normalized = NormalizeAmdName(name);
        // Match an exact discrete model with a published TheRock target. Do not
        // treat the whole RX 6000, 7000, or 9000 series as interchangeable.
        return normalized.ToUpperInvariant() switch
        {
            "RADEON RX 6950 XT" or "RADEON RX 6900 XT" or "RADEON RX 6800 XT" or "RADEON RX 6800" => "gfx1030",
            "RADEON RX 6750 XT" or "RADEON RX 6700 XT" or "RADEON RX 6700" => "gfx1031",
            "RADEON RX 6650 XT" or "RADEON RX 6600 XT" or "RADEON RX 6600" => "gfx1032",
            "RADEON RX 7900 XTX" or "RADEON RX 7900 XT" or "RADEON RX 7900 GRE"
                or "RADEON PRO W7900" or "RADEON PRO W7900 DUAL SLOT" => "gfx1100",
            "RADEON RX 7800 XT" or "RADEON RX 7700 XT" or "RADEON RX 7700" => "gfx1101",
            "RADEON RX 7600 XT" or "RADEON RX 7600" => "gfx1102",
            "RADEON RX 9070 XT" or "RADEON RX 9070" or "RADEON RX 9070 GRE"
                or "RADEON AI PRO R9700" => "gfx1201",
            "RADEON RX 9060 XT" or "RADEON RX 9060" => "gfx1200",
            _ => null,
        };
    }

    private static bool IsLegacyAmdAdapter(string name, string pnpId)
    {
        // An integrated Radeon may share the system with an NVIDIA card. Match
        // the discrete model itself, never merely the AMD vendor name.
        if (!pnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return false;
        var normalized = NormalizeAmdName(name);
        var supported = new[]
        {
            "Radeon RX 9070 XT", "Radeon RX 9070", "Radeon AI PRO R9700",
            "Radeon RX 9060 XT", "Radeon RX 7900 XTX", "Radeon PRO W7900",
            "Radeon PRO W7900 Dual Slot", "Radeon RX 7700",
        };
        return supported.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}
