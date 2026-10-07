using QproFaceTracking.Hub;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

if (args.Length == 2 && args[0] == "--fixture")
{
    if (args[1] == "hold-pipe")
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        return 0;
    }
    if (args[1] == "pipe-parent")
    {
        using var child = Process.Start(Fixture("hold-pipe")) ?? throw new Exception("Pipe holder could not start.");
        Console.WriteLine("holder pid=" + child.Id);
        return 0;
    }
    Console.WriteLine("fixture stdout before exit");
    Console.Error.WriteLine("fixture stderr before exit");
    if (args[1] == "timeout")
    {
        Console.WriteLine("fixture pid=" + Environment.ProcessId);
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
    Console.Write("final stdout without newline");
    Console.Error.Write("final stderr without newline");
    return args[1] == "failure" ? 7 : 0;
}

static void Expect(string evidence, string expected)
{
    var result = HubActionFailure.Explain("Setup", "exit 1", evidence);
    if (!result.Title.Contains(expected, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.NextStep))
        throw new Exception($"Wrong guidance for {evidence}: {result}");
}
Expect("device unauthorized", "permission");
Expect("Grant Magisk Superuser access", "root access");
Expect("Unsupported tracking-engine size", "not compatible");
Expect("Controller ABI is ambiguous", "not compatible");
Expect("Close VRCFaceTracking and wait", "still open");
Expect("Qpro modules were removed, but saved-module recovery needs attention", "saved module needs attention");
Expect("Controller removal failed. Keep SteamVR closed.", "Controller removal needs attention");
Expect("Close SteamVR before installing or uninstalling", "SteamVR is still open");
Expect("WinError 206: filename or extension is too long", "path is too long");
Expect("DLL load failed: WinError 126", "library could not load");
Expect("ModuleNotFoundError: no module named tongue_visibility_calibration", "file is missing");
Expect("PyTorch did not detect a supported discrete Radeon GPU", "GPU was not verified");
Expect("", "did not complete");
var unknown = HubActionFailure.Explain("PC runtime", "final import check failed", "Unknown import failure");
if (unknown.NextStep.Contains("Visual C++", StringComparison.Ordinal)) throw new Exception("Unknown failures must not invent a DLL diagnosis.");
Expect("uid=0(root) Superuser permission granted\nUnknown import failure", "did not complete");
Console.WriteLine("PASS: 15 action failure guidance cases");

foreach (var (script, arguments, report) in new[]
{
    ("controller-input.ps1", Array.Empty<string>(), "CONTROLLER_SETUP {\"phase\":\"complete\"}"),
    ("controller-input.ps1", new[] { "-Action", "uninstall" }, "CONTROLLER_SETUP {\"phase\":\"uninstalled\"}"),
    ("controller-input.ps1", new[] { "-Action", "uninstall" }, "CONTROLLER_SETUP {\"phase\":\"already-uninstalled\"}"),
    ("uninstall-vrcft-eye-bridge.ps1", Array.Empty<string>(), "QPRO_MODULE_UNINSTALL {\"removed\":1,\"restored\":1,\"remainingQpro\":0,\"recoveryIssues\":[]}")
})
{
    var result = HubSetupResult.Create(script, arguments)!;
    if (result.Failure() is null) throw new Exception("An exit without completion evidence became successful.");
    result.Observe(report);
    if (result.Failure() is not null) throw new Exception("A complete setup result was rejected: " + result.Failure());
    result.Observe(report);
    if (result.Failure() is null) throw new Exception("Ambiguous duplicate completion reports became successful.");
}
foreach (var report in new[]
{
    "QPRO_MODULE_UNINSTALL {\"removed\":1,\"restored\":0,\"remainingQpro\":0,\"recoveryIssues\":[\"occupied target\"]}",
    "QPRO_MODULE_UNINSTALL {\"removed\":1,\"restored\":0,\"remainingQpro\":1,\"recoveryIssues\":[]}",
    "QPRO_MODULE_UNINSTALL {\"removed\":1,\"removed\":0,\"restored\":0,\"remainingQpro\":0,\"recoveryIssues\":[]}",
    "QPRO_MODULE_UNINSTALL {\"removed\":1}",
    "QPRO_MODULE_UNINSTALL []"
})
{
    var result = HubSetupResult.Create("uninstall-vrcft-eye-bridge.ps1", [])!;
    result.Observe(report);
    if (result.Failure() is null) throw new Exception("An incomplete module recovery result became successful.");
}
var wrongAction = HubSetupResult.Create("controller-input.ps1", ["-Action", "uninstall"])!;
wrongAction.Observe("CONTROLLER_SETUP {\"phase\":\"complete\"}");
if (wrongAction.Failure() is null) throw new Exception("An install report was accepted for uninstallation.");
Console.WriteLine("PASS: setup completion evidence, duplicate/malformed reports and unresolved recovery");

if (!HubTrainingProgress.TryStage("TRAIN_STAGE index=2 total=3 name=direction epochs=20 device=cpu", out var stage) ||
    stage != new HubTrainingStage(2, 3, "direction", 20, "cpu"))
    throw new Exception("Valid training stage was not preserved.");
foreach (var invalid in new[]
{
    "TRAIN_STAGE index=99999999999999999 total=3 name=direction epochs=20 device=cpu",
    "TRAIN_STAGE index=1 total=99999999999999999 name=direction epochs=20 device=cpu",
    "TRAIN_STAGE index=1 total=3 name=direction epochs=99999999999999999 device=cpu",
    "TRAIN_STAGE index=0 total=3 name=direction epochs=20 device=cpu",
    "TRAIN_STAGE index=4 total=3 name=direction epochs=20 device=cpu",
    "TRAIN_STAGE index=1 total=0 name=direction epochs=20 device=cpu"
})
    if (HubTrainingProgress.TryStage(invalid, out _)) throw new Exception("Accepted invalid training stage: " + invalid);
if (!HubTrainingProgress.TryEpoch("TRAIN_EPOCH current=0 total=20 focus=direction", out var epoch) ||
    epoch != new HubTrainingEpoch(0, 20, "direction"))
    throw new Exception("A valid initial epoch was not preserved.");
if (!HubTrainingProgress.TryEpoch("TRAIN_EPOCH current=2147483647 total=2147483647 focus=direction", out _))
    throw new Exception("Valid Int32 boundaries should remain usable.");
foreach (var invalid in new[]
{
    "TRAIN_EPOCH current=99999999999999999 total=20 focus=direction",
    "TRAIN_EPOCH current=1 total=99999999999999999 focus=direction",
    "TRAIN_EPOCH current=21 total=20 focus=direction",
    "TRAIN_EPOCH current=0 total=0 focus=direction",
    "TRAIN_EPOCH current=-1 total=20 focus=direction"
})
    if (HubTrainingProgress.TryEpoch(invalid, out _)) throw new Exception("Accepted invalid training epoch: " + invalid);
Console.WriteLine("PASS: 14 bounded training progress cases");

static ProcessStartInfo Fixture(string mode)
{
    var executable = Environment.ProcessPath ?? throw new Exception("Test executable path was unavailable.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--fixture");
    start.ArgumentList.Add(mode);
    return start;
}

var failed = await HubProcessResult.RunAsync(Fixture("failure"), TimeSpan.FromSeconds(10));
if (failed.ExitCode != 7 || failed.TimedOut || failed.CleanupError is not null ||
    !failed.Output.Contains("final stdout without newline", StringComparison.Ordinal) ||
    !failed.Error.Contains("final stderr without newline", StringComparison.Ordinal) ||
    !failed.Diagnostics.Contains("fixture stdout before exit", StringComparison.Ordinal) ||
    !failed.Diagnostics.Contains("fixture stderr before exit", StringComparison.Ordinal))
    throw new Exception("Nonzero exits must preserve both complete output streams: " + failed);

var watch = Stopwatch.StartNew();
var timeout = await HubProcessResult.RunAsync(Fixture("timeout"), TimeSpan.FromSeconds(2));
if (!timeout.TimedOut || timeout.ExitCode is null || watch.Elapsed > TimeSpan.FromSeconds(10) ||
    !timeout.Diagnostics.Contains("fixture stdout before exit", StringComparison.Ordinal) ||
    !timeout.Diagnostics.Contains("fixture stderr before exit", StringComparison.Ordinal))
    throw new Exception("Timed-out helper was not terminated with its preceding diagnostics: " + timeout);
var pid = int.Parse(Regex.Match(timeout.Output, @"fixture pid=(\d+)").Groups[1].Value);
try
{
    using var child = Process.GetProcessById(pid);
    if (!child.HasExited) throw new Exception("Timed-out fixture remained running.");
}
catch (ArgumentException) { /* The terminated fixture was already reaped. */ }

try
{
    await HubProcessResult.RunAsync(new ProcessStartInfo(Path.Combine(Path.GetTempPath(), "qpro-missing-" + Guid.NewGuid()))
        { UseShellExecute = false }, TimeSpan.FromSeconds(2));
    throw new Exception("A missing helper must report its start failure.");
}
catch (Win32Exception) { }
Console.WriteLine("PASS: helper failure diagnostics, timeout termination and start failure");

using (var process = new Process { StartInfo = Fixture("pipe-parent") })
{
    var captured = new System.Collections.Concurrent.ConcurrentQueue<string>();
    process.StartInfo.RedirectStandardOutput = process.StartInfo.RedirectStandardError = true;
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) captured.Enqueue(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) captured.Enqueue(e.Data); };
    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    while (!process.HasExited) await Task.Delay(20);
    if (await HubProcessResult.WaitForOutputDrainAsync(process, TimeSpan.FromMilliseconds(300)))
        throw new Exception("An inherited pipe must prevent output from being reported as drained.");
    var holderId = int.Parse(Regex.Match(string.Join("\n", captured), @"holder pid=(\d+)").Groups[1].Value);
    using var holder = Process.GetProcessById(holderId);
    holder.Kill(entireProcessTree: true);
    if (!await HubProcessResult.WaitForOutputDrainAsync(process, TimeSpan.FromSeconds(3)))
        throw new Exception("Output draining must finish after the inherited pipe closes.");
}
Console.WriteLine("PASS: bounded output drain and successful retry after inherited pipe closes");
return 0;
