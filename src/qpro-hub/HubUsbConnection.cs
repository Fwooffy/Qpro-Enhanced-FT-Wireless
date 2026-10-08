using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

// Keeps a verified physical USB selection separate from Android's debugging
// authorization. Saving a serial never makes an offline device ready to use.
internal sealed class HubUsbConnection
{
    internal delegate Task<(bool Completed, int ExitCode, string Output)> ProbeRunner(
        IEnumerable<string> arguments, int timeoutSeconds);

    private const string ConfigFormat = "qpro-usb-headset-v1";
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(30);
    private readonly string _configPath;
    private readonly ProbeRunner _probe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly object _stateLock = new();
    private string? _rememberedSerial;
    private string? _verifiedSerial;
    private string _statusReason;
    private string? _persistenceWarning;
    private string? _failureDetail;
    private DateTimeOffset? _lastReconnectAttempt;
    private DateTimeOffset? _lastServerStartAttempt;
    private long _selectionGeneration;
    private bool _initialEnumerationComplete;

    internal HubUsbConnection(string configPath, ProbeRunner probe,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, Task>? delay = null)
    {
        _configPath = Path.GetFullPath(configPath);
        if (!Path.GetFileName(_configPath).Equals("usb-headset.json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The USB selection file must be named usb-headset.json.", nameof(configPath));
        _probe = probe;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? (duration => Task.Delay(duration));
        _rememberedSerial = LoadRememberedSerial();
        _statusReason = _rememberedSerial is null
            ? "Connect your Quest Pro by USB and approve debugging in the headset."
            : "The remembered Quest Pro has not been checked in this session.";
    }

    internal string? RememberedSerial { get { lock (_stateLock) return _rememberedSerial; } }
    internal string? VerifiedSerial { get { lock (_stateLock) return _verifiedSerial; } }
    internal string StatusReason { get { lock (_stateLock) return _statusReason; } }
    internal string? PersistenceWarning { get { lock (_stateLock) return _persistenceWarning; } }
    internal string? FailureDetail { get { lock (_stateLock) return _failureDetail; } }

    internal void ClearVerification()
    {
        lock (_stateLock)
        {
            _selectionGeneration++;
            _verifiedSerial = null;
            _failureDetail = null;
            _statusReason = "The remembered USB headset needs a fresh connection check.";
        }
    }

    internal async Task<bool> ProbeAsync(bool allowRecovery = false, bool forceReconnect = false)
    {
        await _probeGate.WaitAsync();
        long generation;
        string? remembered;
        lock (_stateLock)
        {
            generation = _selectionGeneration;
            remembered = _rememberedSerial;
            if (forceReconnect) _verifiedSerial = null;
            _failureDetail = null;
        }
        try
        {
            var devices = await ListDevicesAsync(generation, remembered, forceReconnect);
            if (devices is null)
                return Fail(generation, "The ADB server did not finish its USB connection check. Keep the headset awake and use Reconnect USB to retry.");

            UsbDevice? selected;
            if (remembered is not null)
            {
                selected = devices.SingleOrDefault(device => device.Serial == remembered);
                if (selected is null)
                    return Fail(generation, "The remembered Quest Pro is not connected by USB. Check the cable and keep the headset awake; use Forget USB headset before selecting a different headset.");
            }
            else
            {
                if (devices.Count == 0)
                    return Fail(generation, "No USB headset was found. Connect your Quest Pro with a data cable and keep it awake.");
                if (devices.Count > 1)
                    return Fail(generation, "More than one USB device is connected. Disconnect the other Android devices for the first Quest Pro check.");
                selected = devices[0];
            }

            if (selected.State == "unauthorized")
                return Fail(generation, "USB debugging needs approval. Put on the Quest Pro, accept the debugging prompt and choose Always allow from this computer if available.");
            if (selected.State is not ("device" or "offline"))
                return Fail(generation, "The USB device is not ready for Android commands. Keep the Quest Pro awake and approve USB debugging.");

            // A remembered serial was physically confirmed before saving. A new
            // selection must pass -d, which cannot select a TCP or mDNS device.
            if (remembered is null)
            {
                if (selected.State != "device")
                    return Fail(generation, "The USB device is offline. Keep the headset awake and retry; it must be authorized before Qpro can remember it.");
                var physical = await RunProbeAsync(["-d", "get-serialno"], 3, generation);
                if (!Succeeded(physical) || ExtractSerial(physical.Output) != selected.Serial)
                    return Fail(generation, "The connection could not be confirmed as a single physical USB headset. Disconnect other Android devices and retry.");
            }

            var shouldReconnect = forceReconnect || selected.State == "offline" && allowRecovery;
            if (selected.State == "offline" && !shouldReconnect)
                return Fail(generation, "The remembered USB headset is offline. Keep it awake, then use Reconnect USB to refresh this connection.");
            if (shouldReconnect)
            {
                // A connected device can report its identity before an explicit
                // refresh. Do not reconnect a newly discovered phone or headset
                // of another type merely because it is the only USB device.
                if (selected.State == "device")
                {
                    var beforeModel = await RunProbeAsync(["-s", selected.Serial, "shell", "getprop", "ro.product.model"], 3, generation);
                    var beforeProduct = await RunProbeAsync(["-s", selected.Serial, "shell", "getprop", "ro.product.device"], 3, generation);
                    if (!Succeeded(beforeModel) || !Succeeded(beforeProduct))
                        return Fail(generation, "The USB connection could not be identified before reconnecting. Keep the headset awake and retry.");
                    if (!IsQuestPro(beforeModel.Output, beforeProduct.Output))
                        return Fail(generation, "The connected USB device is not a Meta Quest Pro. Qpro has not reconnected it.");
                }
                // Do not use reconnect offline: ADB applies that command to all
                // offline transports, even when a serial option is supplied.
                if (!BeginReconnect(generation))
                    return Fail(generation, "USB reconnect was tried recently. Keep the headset awake and wait a few seconds before trying again.");
                var reconnect = await RunProbeAsync(["-s", selected.Serial, "reconnect"], 3, generation);
                if (!Succeeded(reconnect))
                    return Fail(generation, "ADB could not refresh the selected USB connection. Keep the headset awake and retry after a short wait.");
                await _delay(TimeSpan.FromMilliseconds(400));
                devices = await ListDevicesAsync(generation, remembered, false, afterReconnect: true);
                var reconnectSerial = selected.Serial;
                selected = devices?.SingleOrDefault(device => device.Serial == reconnectSerial);
                if (selected is null || selected.State != "device")
                    return Fail(generation, selected?.State == "unauthorized"
                        ? "USB reconnected, but debugging still needs approval inside the headset."
                        : "USB reconnect finished, but the Quest Pro is not ready yet. Keep it awake and check the cable if it stays offline.");
            }

            var model = await RunProbeAsync(["-s", selected.Serial, "shell", "getprop", "ro.product.model"], 3, generation);
            var product = await RunProbeAsync(["-s", selected.Serial, "shell", "getprop", "ro.product.device"], 3, generation);
            if (!Succeeded(model) || !Succeeded(product))
                return Fail(generation, "The USB connection responded, but the headset identity could not be read. Keep it awake and retry.");
            if (!IsQuestPro(model.Output, product.Output))
                return Fail(generation, "The connected USB device is not a Meta Quest Pro. Qpro's camera and gaze features require a Quest Pro.");

            lock (_stateLock)
            {
                if (_selectionGeneration != generation) return false;
                if (_rememberedSerial is null)
                {
                    _rememberedSerial = selected.Serial;
                    _persistenceWarning = SaveRememberedSerial(selected.Serial);
                }
                else if (forceReconnect && _persistenceWarning is not null)
                    _persistenceWarning = SaveRememberedSerial(selected.Serial);
                _verifiedSerial = selected.Serial;
                _failureDetail = null;
                _statusReason = _persistenceWarning is null ? "USB Quest Pro connected and remembered."
                    : "USB Quest Pro connected. Its selection could not be saved; check folder permissions.";
                return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or JsonException or OperationCanceledException)
        {
            return Fail(generation, "The USB connection check could not finish. Keep the headset awake and retry.");
        }
        finally { _probeGate.Release(); }
    }

    internal void Forget()
    {
        lock (_stateLock)
        {
            // Invalidate an outstanding probe so it cannot save the selection
            // again after the user clears it.
            _selectionGeneration++;
            _rememberedSerial = _verifiedSerial = null;
            _lastReconnectAttempt = null;
            _persistenceWarning = null;
            _failureDetail = null;
            _statusReason = "USB headset selection forgotten. Connect one Quest Pro and check again.";
            try
            {
                EnsureNoLinks(_configPath);
                if (File.Exists(_configPath)) File.Delete(_configPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _persistenceWarning = "The saved USB selection could not be removed. Check folder permissions.";
                _statusReason = _persistenceWarning;
            }
        }
    }

    private bool BeginReconnect(long generation)
    {
        lock (_stateLock)
        {
            if (_selectionGeneration != generation) return false;
            var now = _clock();
            if (_lastReconnectAttempt is { } last && now - last < ReconnectCooldown) return false;
            _lastReconnectAttempt = now;
            return true;
        }
    }

    private bool Fail(long generation, string reason)
    {
        lock (_stateLock)
        {
            if (_selectionGeneration == generation)
            {
                _verifiedSerial = null;
                _statusReason = reason;
            }
        }
        return false;
    }

    private sealed record UsbDevice(string Serial, string State);

    private async Task<List<UsbDevice>?> ListDevicesAsync(long generation, string? remembered,
        bool forceReconnect, bool afterReconnect = false)
    {
        bool initial;
        lock (_stateLock)
        {
            if (_selectionGeneration != generation) return null;
            initial = !afterReconnect && !_initialEnumerationComplete;
            if (!afterReconnect) _initialEnumerationComplete = true;
        }
        // Start the daemon separately on reopening. A short devices timeout can
        // otherwise kill its freshly spawned daemon along with the client.
        if (initial)
        {
            if (!BeginServerStart(generation, force: true)) return null;
            var server = await RunProbeAsync(["start-server"], 8, generation);
            if (!Succeeded(server)) return null;
        }
        var result = await RunProbeAsync(["devices", "-l"], initial ? 5 : 3, generation);
        var devices = ParseDevices(result);
        var missing = devices is null || devices.Count == 0 ||
            remembered is not null && !devices.Any(device => device.Serial == remembered);
        if (afterReconnect || !missing || result.Output.Contains("stopped for Hub shutdown", StringComparison.OrdinalIgnoreCase))
            return devices;

        // Closing the Hub deliberately stops ADB. Its next startup can take
        // longer than an ordinary status query, and USB enumeration may arrive
        // just after the daemon starts. Give startup one bounded second chance.
        if (!initial)
        {
            if ((!forceReconnect && Succeeded(result)) || !BeginServerStart(generation, forceReconnect))
                return devices;
            var server = await RunProbeAsync(["start-server"], 8, generation);
            if (!Succeeded(server)) return null;
        }
        await _delay(TimeSpan.FromMilliseconds(400));
        lock (_stateLock) { if (_selectionGeneration != generation) return null; }
        return ParseDevices(await RunProbeAsync(["devices", "-l"], 5, generation));
    }

    private async Task<(bool Completed, int ExitCode, string Output)> RunProbeAsync(
        string[] arguments, int timeoutSeconds, long generation)
    {
        var result = await _probe(arguments, timeoutSeconds);
        if (!Succeeded(result))
        {
            var displayArguments = arguments.ToArray();
            var output = result.Output.Trim();
            if (displayArguments.Length > 1 && displayArguments[0] == "-s")
            {
                output = output.Replace(displayArguments[1], "[selected USB headset]", StringComparison.Ordinal);
                displayArguments[1] = "[selected USB headset]";
            }
            var detail = "ADB " + string.Join(" ", displayArguments) +
                (result.Completed ? $" exited with code {result.ExitCode}." : $" did not complete (limit {timeoutSeconds} seconds).") +
                (output.Length == 0 ? " No diagnostic output was returned." : "\n" + output);
            lock (_stateLock)
                if (_selectionGeneration == generation && _failureDetail is null)
                    _failureDetail = detail[..Math.Min(detail.Length, 4096)];
        }
        return result;
    }

    private bool BeginServerStart(long generation, bool force)
    {
        lock (_stateLock)
        {
            if (_selectionGeneration != generation) return false;
            var now = _clock();
            if (!force && _lastServerStartAttempt is { } last && now - last < ReconnectCooldown) return false;
            _lastServerStartAttempt = now;
            return true;
        }
    }

    private static List<UsbDevice>? ParseDevices((bool Completed, int ExitCode, string Output) result)
    {
        if (!Succeeded(result) || result.Output.Length > 131_072) return null;
        var devices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IsUsbSerial(parts[0]) ||
                parts[1] is not ("device" or "offline" or "unauthorized" or "recovery" or "bootloader" or "no")) continue;
            if (devices.TryGetValue(parts[0], out var state) && state != parts[1]) return null;
            devices[parts[0]] = parts[1];
            if (devices.Count > 256) return null;
        }
        return devices.Select(pair => new UsbDevice(pair.Key, pair.Value)).ToList();
    }

    private static bool Succeeded((bool Completed, int ExitCode, string Output) result)
        => result.Completed && result.ExitCode == 0;

    private static bool IsQuestPro(string model, string product)
        => model.Trim().Equals("Quest Pro", StringComparison.OrdinalIgnoreCase) ||
            product.Trim().Equals("seacliff", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractSerial(string output)
    {
        var values = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(IsUsbSerial).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static bool IsUsbSerial(string value)
        => Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant) &&
            !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("adb-", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("._adb", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("._tcp", StringComparison.OrdinalIgnoreCase);

    private string? LoadRememberedSerial()
    {
        try
        {
            EnsureNoLinks(_configPath);
            if (!File.Exists(_configPath) || new FileInfo(_configPath).Length > 4096) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
            var root = document.RootElement;
            return root.TryGetProperty("format", out var format) && format.GetString() == ConfigFormat &&
                root.TryGetProperty("physicalUsbConfirmed", out var physical) && physical.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("serial", out var serial) && serial.ValueKind == JsonValueKind.String &&
                serial.GetString() is { } value && IsUsbSerial(value) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException) { return null; }
    }

    private string? SaveRememberedSerial(string serial)
    {
        string? temporary = null;
        try
        {
            EnsureNoLinks(_configPath);
            var directory = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(directory);
            EnsureNoLinks(_configPath);
            temporary = Path.Combine(directory, ".usb-headset-" + Guid.NewGuid().ToString("N") + ".tmp");
            var text = JsonSerializer.Serialize(new
            {
                format = ConfigFormat, serial, physicalUsbConfirmed = true,
                checkedUtc = _clock().ToString("O")
            });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) writer.Write(text);
            File.Move(temporary, _configPath, overwrite: true);
            temporary = null;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "The USB connection is ready, but Qpro could not save its selection in this folder.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The USB selection path contains a link.");
    }
}
