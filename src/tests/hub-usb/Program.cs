using System.Collections.Concurrent;
using System.Text.Json;
using QproFaceTracking.Hub;

// All commands below use fixtures. This executable never opens a real ADB
// server or device and does not modify a user's installed Qpro configuration.
// Keep fixtures beside the test executable. Windows sandbox temp folders can
// themselves be redirected links, which production configuration rejects.
var temporaryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "usb-fixtures"));
var fixtureRoot = Path.Combine(temporaryRoot, "qpro-usb-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
string Config(string name) => Path.Combine(fixtureRoot, name, "usb-headset.json");
HubUsbConnection Create(string path, AdbFixture fixture, Func<DateTimeOffset>? clock = null)
    => new(path, fixture.Run, clock, _ => Task.CompletedTask);
async Task<string> SavedSelection(string name, AdbFixture fixture)
{
    var path = Config(name);
    Check(await Create(path, fixture).ProbeAsync(), "Could not prepare the verified USB fixture.");
    fixture.Commands.Clear();
    return path;
}

try
{
    var fixture = new AdbFixture();
    var path = Config("remember");
    var usb = Create(path, fixture);
    Check(usb.RememberedSerial is null && usb.VerifiedSerial is null, "Fresh selection was already trusted.");
    Check(await usb.ProbeAsync(), "Authorized physical Quest Pro was not accepted.");
    Check(usb.RememberedSerial == AdbFixture.Quest && usb.VerifiedSerial == AdbFixture.Quest,
        "Successful verification did not pin the Quest.");
    Check(fixture.Commands[0].Arguments.SequenceEqual(["start-server"]) && fixture.Commands[0].Timeout == 8,
        "Cold startup did not start the server with its own longer timeout.");
    Check(fixture.Commands[1].Arguments.SequenceEqual(["devices", "-l"]) && fixture.Commands[1].Timeout == 5,
        "Cold enumeration did not use the startup timeout.");
    Check(fixture.Commands.Any(command => command.Arguments.SequenceEqual(["-d", "get-serialno"])),
        "First USB selection was not physically confirmed.");
    using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
    {
        Check(saved.RootElement.GetProperty("format").GetString() == "qpro-usb-headset-v1", "Unexpected saved format.");
        Check(saved.RootElement.GetProperty("physicalUsbConfirmed").GetBoolean(), "Physical confirmation was not saved.");
    }
    var reopened = Create(path, fixture);
    Check(reopened.RememberedSerial == AdbFixture.Quest && reopened.VerifiedSerial is null,
        "Reopening lost the selection or incorrectly reported it ready.");
    fixture.Commands.Clear();
    fixture.Devices.Add(new("PHONEFIXTURE", "device", "Phone", "phone"));
    Check(await reopened.ProbeAsync(), "Remembered Quest failed with an unrelated phone attached.");
    Check(reopened.VerifiedSerial == AdbFixture.Quest, "The connected phone replaced the Quest.");
    Check(!fixture.Commands.Any(command => command.Arguments.Contains("PHONEFIXTURE")), "The phone was queried.");
    Check(!fixture.Commands.Any(command => command.Arguments.SequenceEqual(["-d", "get-serialno"])),
        "A remembered physical selection depended on there being just one USB device.");
    fixture.Commands.Clear();
    fixture.Devices.RemoveAll(device => device.Serial == AdbFixture.Quest);
    Check(!await reopened.ProbeAsync(), "Missing remembered Quest fell back to the phone.");
    Check(reopened.VerifiedSerial is null && reopened.RememberedSerial == AdbFixture.Quest,
        "Absence did not clear readiness while preserving selection.");
    Check(!fixture.Commands.Any(command => command.Arguments.Contains("reconnect")), "A missing device triggered a reconnect.");
    Check(reopened.StatusReason.Contains("remembered", StringComparison.OrdinalIgnoreCase), "Absence explanation was lost.");
    reopened.Forget();
    Check(!File.Exists(path) && reopened.RememberedSerial is null && reopened.VerifiedSerial is null,
        "Forget did not clear the private selection.");

    var unauthorized = new AdbFixture();
    unauthorized.Devices[0] = unauthorized.Devices[0] with { State = "unauthorized" };
    var authorization = Create(Config("unauthorized"), unauthorized);
    Check(!await authorization.ProbeAsync(forceReconnect: true), "Unauthorized Quest was reported ready.");
    Check(authorization.StatusReason.Contains("approval", StringComparison.OrdinalIgnoreCase), "Authorization guidance was missing.");
    Check(!File.Exists(Config("unauthorized")), "Unauthorized device was saved.");
    Check(!unauthorized.Commands.Any(command => command.Arguments.Contains("reconnect")), "Authorization was bypassed with reconnect.");

    var wrong = new AdbFixture();
    wrong.Devices[0] = new(AdbFixture.Quest, "device", "Quest 3", "eureka");
    var wrongSelection = Create(Config("wrong"), wrong);
    Check(!await wrongSelection.ProbeAsync(forceReconnect: true), "A different Quest model was accepted.");
    Check(!File.Exists(Config("wrong")) && wrongSelection.RememberedSerial is null, "Wrong model changed selection.");
    Check(!wrong.Commands.Any(command => command.Arguments.Contains("reconnect")), "Wrong model was reconnected before validation.");

    var incompatiblePhysical = new AdbFixture { PhysicalSerial = "OTHERUSB" };
    Check(!await Create(Config("wrong-transport"), incompatiblePhysical).ProbeAsync(),
        "A mismatching physical USB transport was accepted.");
    Check(!File.Exists(Config("wrong-transport")), "Wrong transport was remembered.");
    var duplicate = new AdbFixture();
    duplicate.Devices.Add(new("PHONEFIXTURE", "device", "Phone", "phone"));
    var ambiguous = Create(Config("multiple"), duplicate);
    Check(!await ambiguous.ProbeAsync(), "First selection silently chose among multiple USB devices.");
    Check(ambiguous.StatusReason.Contains("More than one", StringComparison.Ordinal), "Ambiguous-device guidance was missing.");
    Check(!duplicate.Commands.Any(command => command.Arguments.Contains("getprop")), "Ambiguous selection queried a device.");

    var wireless = new AdbFixture();
    wireless.Devices.Clear();
    wireless.Devices.AddRange([
        new("192.168.1.5:5555", "device", "Quest Pro", "seacliff"),
        new("emulator-5554", "device", "Quest Pro", "seacliff"),
        new("adb-FAKE._adb-tls-connect._tcp.", "device", "Quest Pro", "seacliff"),
        new("FAKE._adb-tls-connect._tcp", "device", "Quest Pro", "seacliff")]);
    var noUsb = Create(Config("wireless"), wireless);
    Check(!await noUsb.ProbeAsync(), "A wireless/emulator transport was accepted as USB.");
    Check(!wireless.Commands.Any(command => command.Arguments.Contains("get-serialno") || command.Arguments.Contains("getprop")),
        "Excluded transports were queried as USB.");
    Check(!File.Exists(Config("wireless")), "An excluded transport was saved.");
    wireless.Devices.Add(new(AdbFixture.Quest, "device", "Quest Pro", "seacliff"));
    Check(await noUsb.ProbeAsync(), "Real USB Quest was blocked by Wi-Fi/emulator entries.");
    Check(noUsb.VerifiedSerial == AdbFixture.Quest, "Wrong serial was chosen alongside excluded transports.");

    var now = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
    var offlineFixture = new AdbFixture();
    var offlinePath = await SavedSelection("offline", offlineFixture);
    var recovering = Create(offlinePath, offlineFixture, () => now);
    offlineFixture.Devices[0] = offlineFixture.Devices[0] with { State = "offline" };
    Check(!await recovering.ProbeAsync(), "Read-only status repaired an offline transport automatically.");
    Check(offlineFixture.Commands.All(command => !command.Arguments.Contains("reconnect")), "Default probe reconnected a transport.");
    offlineFixture.OnReconnect = () => offlineFixture.Devices[0] = offlineFixture.Devices[0] with { State = "device" };
    Check(await recovering.ProbeAsync(allowRecovery: true), "Bounded targeted offline recovery failed.");
    var reconnects = offlineFixture.Commands.Where(command => command.Arguments.Contains("reconnect")).ToArray();
    Check(reconnects.Length == 1 && reconnects[0].Arguments.SequenceEqual(["-s", AdbFixture.Quest, "reconnect"]),
        "Recovery did not target exactly the remembered Quest.");
    Check(offlineFixture.Commands.All(command => !command.Arguments.Contains("kill-server") &&
        !command.Arguments.SequenceEqual(["reconnect", "offline"])), "Recovery reset unrelated ADB transports.");
    offlineFixture.Devices[0] = offlineFixture.Devices[0] with { State = "offline" };
    var beforeCooldown = offlineFixture.Commands.Count(command => command.Arguments.Contains("reconnect"));
    Check(!await recovering.ProbeAsync(allowRecovery: true), "Reconnect cooldown was ignored.");
    Check(offlineFixture.Commands.Count(command => command.Arguments.Contains("reconnect")) == beforeCooldown,
        "Cooldown still sent a reconnect command.");
    now += TimeSpan.FromSeconds(31);
    Check(await recovering.ProbeAsync(allowRecovery: true), "Recovery did not become available after cooldown.");
    var beforeNormal = offlineFixture.Commands.Count(command => command.Arguments.Contains("reconnect"));
    Check(await recovering.ProbeAsync(allowRecovery: true), "Ordinary connected status became unavailable.");
    Check(offlineFixture.Commands.Count(command => command.Arguments.Contains("reconnect")) == beforeNormal,
        "Allowing offline recovery reconnected a healthy transport.");
    now += TimeSpan.FromSeconds(31);
    Check(await recovering.ProbeAsync(forceReconnect: true), "Explicit reconnect of a healthy Quest failed.");
    Check(offlineFixture.Commands.Count(command => command.Arguments.Contains("reconnect")) == beforeNormal + 1,
        "Explicit reconnect did not refresh the connection.");

    var failedReconnectFixture = new AdbFixture();
    var failedReconnectPath = await SavedSelection("reconnect-failure", failedReconnectFixture);
    failedReconnectFixture.Devices[0] = failedReconnectFixture.Devices[0] with { State = "offline" };
    failedReconnectFixture.Override = (arguments, _) => arguments.Contains("reconnect")
        ? Task.FromResult<(bool, int, string)>((true, 1, "fixture transport could not reconnect")) : null;
    var failedReconnect = Create(failedReconnectPath, failedReconnectFixture);
    Check(!await failedReconnect.ProbeAsync(allowRecovery: true), "Failed reconnect was reported successful.");
    Check(failedReconnect.VerifiedSerial is null && failedReconnect.RememberedSerial == AdbFixture.Quest,
        "Failed reconnect corrupted selection/readiness.");
    Check(failedReconnect.FailureDetail?.Contains("fixture transport could not reconnect", StringComparison.Ordinal) == true &&
        !failedReconnect.FailureDetail.Contains(AdbFixture.Quest, StringComparison.Ordinal), "Failure detail lost the error or exposed the selected serial.");

    foreach (var text in new[] { "not-json", "{}", "{\"format\":3}",
        "{\"format\":\"qpro-usb-headset-v1\",\"physicalUsbConfirmed\":false,\"serial\":\"QUESTFIXTURE\"}",
        "{\"format\":\"qpro-usb-headset-v1\",\"physicalUsbConfirmed\":true,\"serial\":\"192.168.1.5:5555\"}",
        new string('x', 4097) })
    {
        var malformedPath = Config("malformed-" + checks);
        Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
        File.WriteAllText(malformedPath, text);
        var malformed = Create(malformedPath, new AdbFixture());
        Check(malformed.RememberedSerial is null, "Invalid private configuration was trusted.");
        Check(await malformed.ProbeAsync(), "Invalid private configuration blocked a fresh physical verification.");
    }

    var badParent = Path.Combine(fixtureRoot, "parent-is-a-file");
    File.WriteAllText(badParent, "fixture");
    var unwritable = Create(Path.Combine(badParent, "usb-headset.json"), new AdbFixture());
    Check(await unwritable.ProbeAsync(), "Persistence failure prevented use of a verified USB connection.");
    Check(unwritable.PersistenceWarning is not null && unwritable.VerifiedSerial == AdbFixture.Quest,
        "Persistence failure was hidden or readiness lost.");
    File.Delete(badParent);
    Check(await unwritable.ProbeAsync(forceReconnect: true), "Explicit verification did not retry repaired persistence.");
    Check(unwritable.PersistenceWarning is null && File.Exists(Path.Combine(badParent, "usb-headset.json")),
        "Repaired folder permissions did not save the remembered USB device.");

    var cold = new AdbFixture();
    cold.Override = (arguments, timeout) => arguments.SequenceEqual(["start-server"])
        ? Task.FromResult<(bool, int, string)>((timeout >= 8, timeout >= 8 ? 0 : -1, "fixture slow daemon")) : null;
    Check(await Create(Config("slow-server"), cold).ProbeAsync(), "Slow daemon startup used the old short timeout.");
    Check(cold.Commands.Count(command => command.Arguments.SequenceEqual(["start-server"])) == 1,
        "Successful startup restarted the server repeatedly.");
    var delayed = new AdbFixture();
    var enumerations = 0;
    delayed.Override = (arguments, _) => arguments.SequenceEqual(["devices", "-l"]) && ++enumerations == 1
        ? Task.FromResult<(bool, int, string)>((true, 0, "List of devices attached\n")) : null;
    var delayedUsb = Create(Config("delayed-enumeration"), delayed);
    Check(await delayedUsb.ProbeAsync() && enumerations == 2, "Late USB enumeration did not get one bounded retry.");
    await delayedUsb.ProbeAsync();
    Check(delayed.Commands.Count(command => command.Arguments.SequenceEqual(["start-server"])) == 1,
        "Regular status polling ran start-server again.");

    var badServer = new AdbFixture();
    badServer.Override = (arguments, _) => arguments.SequenceEqual(["start-server"])
        ? Task.FromResult<(bool, int, string)>((false, -1, new string('e', 5000))) : null;
    var badServerUsb = Create(Config("failed-server"), badServer);
    Check(!await badServerUsb.ProbeAsync(), "Failed daemon startup was accepted.");
    Check(badServer.Commands.Count == 1, "Failed daemon startup queued extra commands.");
    Check(badServerUsb.FailureDetail is { Length: <= 4096 } detail && detail.Contains("start-server", StringComparison.Ordinal),
        "Startup diagnostic did not retain a bounded command failure.");

    var retryFixture = new AdbFixture();
    var retryClock = now;
    var retryUsb = Create(Config("later-server-retry"), retryFixture, () => retryClock);
    Check(await retryUsb.ProbeAsync(), "Could not prepare later daemon-loss check.");
    retryClock += TimeSpan.FromSeconds(31);
    var failNextList = true;
    retryFixture.Override = (arguments, _) => arguments.SequenceEqual(["devices", "-l"]) && failNextList
        ? FailedList() : null;
    Task<(bool, int, string)> FailedList()
    {
        failNextList = false;
        return Task.FromResult((false, -1, "fixture daemon did not answer"));
    }
    Check(await retryUsb.ProbeAsync(), "A later server failure did not receive a bounded startup retry.");
    Check(retryUsb.FailureDetail is null, "A successful retry left stale error diagnostics.");
    Check(retryFixture.Commands.Count(command => command.Arguments.SequenceEqual(["start-server"])) == 2,
        "Later daemon recovery did not start it exactly once.");

    foreach (var forget in new[] { false, true })
    {
        var slow = new AdbFixture();
        var slowPath = Config("inflight-" + forget);
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.Override = (arguments, _) => arguments.LastOrDefault() == "ro.product.model" ? SlowModel() : null;
        async Task<(bool, int, string)> SlowModel()
        {
            modelStarted.SetResult();
            await releaseModel.Task;
            return (true, 0, "Quest Pro");
        }
        var invalidated = Create(slowPath, slow);
        var pending = invalidated.ProbeAsync();
        await modelStarted.Task;
        if (forget) invalidated.Forget(); else invalidated.ClearVerification();
        releaseModel.SetResult();
        Check(!await pending, "Invalidated in-flight probe reported success.");
        Check(invalidated.VerifiedSerial is null && invalidated.RememberedSerial is null && !File.Exists(slowPath),
            "An in-flight probe restored or saved a selection after it was cleared.");
    }
    var keepRemembered = new AdbFixture();
    var keepPath = await SavedSelection("clear-current", keepRemembered);
    var keep = Create(keepPath, keepRemembered);
    Check(await keep.ProbeAsync(), "Could not prepare mode-switch check.");
    var beforeClear = File.ReadAllText(keepPath);
    keep.ClearVerification();
    Check(keep.VerifiedSerial is null && keep.RememberedSerial == AdbFixture.Quest && File.ReadAllText(keepPath) == beforeClear,
        "Clearing current verification changed the remembered device.");

    Check(await keep.ProbeAsync(), "Could not prepare healthy in-flight readiness check.");
    var healthyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var healthyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    keepRemembered.Override = (arguments, _) => arguments.LastOrDefault() == "ro.product.model" ? HealthyModel() : null;
    async Task<(bool, int, string)> HealthyModel()
    {
        healthyStarted.SetResult();
        await healthyRelease.Task;
        return (true, 0, "Quest Pro");
    }
    var healthyPending = keep.ProbeAsync();
    await healthyStarted.Task;
    Check(keep.VerifiedSerial == AdbFixture.Quest, "A pending healthy status tick unpinned the verified target.");
    healthyRelease.SetResult();
    Check(await healthyPending && keep.VerifiedSerial == AdbFixture.Quest, "Healthy recheck lost its pinned target.");

    foreach (var blockedCommand in new[] { "start-server", "devices" })
    {
        var commands = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = false;
        var adbSession = new HubAdbSession(async (_, arguments, _, cancellation) =>
        {
            var command = arguments.First();
            commands.Enqueue(command);
            if (command == blockedCommand)
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                finally { drained = true; }
            }
            if (command == "kill-server") Check(drained, "Shutdown ran before the pending helper probe drained.");
            return (true, 0, "");
        });
        var shuttingDown = new HubUsbConnection(Config("shutdown-" + blockedCommand),
            (arguments, timeout) => adbSession.ProbeAsync("fixture-adb", arguments, timeout), delay: _ => Task.CompletedTask);
        var pending = shuttingDown.ProbeAsync();
        await started.Task;
        var stop = await adbSession.StopServerAsync("fixture-adb");
        Check(stop.Completed && !await pending, "Shutdown did not cancel the USB helper check.");
        Check(commands.Last() == "kill-server" && commands.Count(command => command == "kill-server") == 1,
            "Helper restarted or re-enumerated ADB after shutdown.");
        Check(!await shuttingDown.ProbeAsync(forceReconnect: true) && commands.Last() == "kill-server",
            "A later forced helper probe restarted a suspended ADB session.");
    }

    Console.WriteLine($"USB connection checks passed: {checks} assertions; physical identity, persistence, cold startup, targeted recovery, invalidation and shutdown.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("USB connection checks failed: " + error);
    return 1;
}
finally
{
    var resolved = Path.GetFullPath(fixtureRoot);
    if (resolved.StartsWith(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(resolved).StartsWith("qpro-usb-check-", StringComparison.Ordinal))
        Directory.Delete(resolved, recursive: true);
}

sealed class AdbFixture
{
    internal const string Quest = "QUESTFIXTURE";
    internal sealed record Device(string Serial, string State, string Model, string Product);
    internal sealed record Command(string[] Arguments, int Timeout);
    internal List<Device> Devices = [new(Quest, "device", "Quest Pro", "seacliff")];
    internal List<Command> Commands = [];
    internal string PhysicalSerial = Quest;
    internal Action? OnReconnect;
    internal Func<string[], int, Task<(bool Completed, int ExitCode, string Output)>?>? Override;

    internal async Task<(bool Completed, int ExitCode, string Output)> Run(IEnumerable<string> values, int timeout)
    {
        var arguments = values.ToArray();
        Commands.Add(new(arguments, timeout));
        var replacement = Override?.Invoke(arguments, timeout);
        if (replacement is not null) return await replacement;
        if (arguments.SequenceEqual(["start-server"])) return (true, 0, "");
        if (arguments.SequenceEqual(["devices", "-l"]))
            return (true, 0, "List of devices attached\n" + string.Join("\n", Devices.Select(device =>
                $"{device.Serial}\t{device.State} product:fixture model:fixture device:fixture transport_id:1")));
        if (arguments.SequenceEqual(["-d", "get-serialno"])) return (true, 0, PhysicalSerial);
        if (arguments.Length > 2 && arguments[0] == "-s")
        {
            var device = Devices.SingleOrDefault(value => value.Serial == arguments[1]);
            if (device is null) return (true, 1, "fixture device not found");
            if (arguments[2] == "reconnect") { OnReconnect?.Invoke(); return (true, 0, "reconnecting"); }
            if (arguments.Last() == "ro.product.model") return (true, 0, device.Model);
            if (arguments.Last() == "ro.product.device") return (true, 0, device.Product);
        }
        throw new InvalidOperationException("Unexpected fixture command: " + string.Join(" ", arguments));
    }
}
