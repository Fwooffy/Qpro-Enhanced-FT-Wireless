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
    if (args[1] == "module-failure")
    {
        Console.WriteLine("Starting camera tracking fixture");
        Console.Error.Write("The Qpro Virtual Desktop module was not found in its current module folder. Close VRCFaceTracking, install the Qpro Virtual Desktop module in First-time setup, then reopen VRCFaceTracking.");
        return 1;
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

var failureChecks = 0;
void Expect(string evidence, string expected, string action = "Setup")
{
    var result = HubActionFailure.Explain(action, "exit 1", evidence);
    if (!result.Title.Contains(expected, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.NextStep))
        throw new Exception($"Wrong guidance for {evidence}: {result}");
    failureChecks++;
}
Expect("device unauthorized", "permission");
Expect("Grant Magisk Superuser access", "root access");
Expect("Unsupported tracking-engine size", "not compatible");
Expect("Controller ABI is ambiguous", "not compatible");
foreach (var conflict in new[]
{
    "An active Magisk gaze module was found (independent_gaze). Use one independent gaze method at a time. Leave Independent Eye Gaze off in the Hub while that module is active, or disable the module in Magisk and reboot before preparing the Hub's temporary method. No headset tracking was changed.",
    "Another headset gaze method is enabled in Magisk: independent_gaze. Leave Independent Eye Gaze unchecked in the Hub, or disable that gaze module in Magisk and reboot before using the Hub method. No tracking was changed."
})
{
    Expect(conflict, "Another gaze method", "Local gaze preparation");
    var result = HubActionFailure.Explain("Local gaze preparation", "exit 1", conflict);
    if (!result.NextStep.Contains("Independent Eye Gaze off", StringComparison.Ordinal) ||
        !result.NextStep.Contains("Skip Check gaze setup and Prepare gaze", StringComparison.Ordinal) ||
        !result.NextStep.Contains("disable the Magisk gaze module and reboot", StringComparison.Ordinal))
        throw new Exception("Magisk conflicts must explain both choices without requiring Hub gaze checks: " + result);
}
foreach (var setting in new[] { "hand_tracking_enabled", "multimodal_hands_and_controllers_enabled" })
    Expect("HANDS_INCOMPATIBLE {\"error\":\"Enable headset hand tracking and Singularity's Simultaneous Hands & Controllers; could not verify " + setting + ".\"}",
        "settings could not be verified", "Hand/controller input");
foreach (var setting in new[] { "hand_tracking_enabled", "multimodal_hands_and_controllers_enabled", "simultaneous_hands_and_controllers_mode" })
    Expect("Could not read one supported current-user value for " + setting + ". Check the headset connection and root access, then check compatibility again.",
        "settings could not be verified", "Hand/controller compatibility");
Expect("Headset current-user setting simultaneous_hands_and_controllers_mode must be 1. In Singularity, turn on Simultaneous Hands & Controllers, then check compatibility again.",
    "settings could not be verified", "Hand/controller compatibility");
var handSettings = HubActionFailure.Explain("Hand/controller input", "exit 4",
    "HANDS_INCOMPATIBLE {\"error\":\"could not verify multimodal_hands_and_controllers_enabled.\"}");
if (!handSettings.NextStep.Contains("Live optical input and adapter checks still need to pass", StringComparison.Ordinal))
    throw new Exception("Enabling hand settings must not promise tracking compatibility: " + handSettings);
Expect("Enable headset hand tracking; could not verify hand_tracking_enabled.\nHANDS_INCOMPATIBLE {\"error\":\"controller-selection adapter validation failed: Error: Controller ABI is ambiguous or unsupported (0 validated callers)\"}",
    "not compatible", "Hand/controller input");
Expect("Could not read one supported current-user value for hand_tracking_enabled.\nThis Virtual Desktop Streamer driver has no validated hand profile.",
    "not compatible", "Hand/controller compatibility");
Expect("Headset reports hand_tracking_enabled inactive before startup; Qpro will check live optical input after requesting Virtual Desktop multimodal mode.\nAn unrelated worker error.",
    "did not complete", "Hand/controller input");
var cacheError = "This prepared tongue cache uses an older native TongueOut mapping. Regenerate the cache from its original .qpcap, .qplabel.jsonl and .qpsession.json files before training. The Hub rebuilds it when you retry training; command-line users should rerun the matching prepare_tongue_stills.py or prepare_tongue_training.py command. Existing trained checkpoints remain usable.";
Expect(cacheError, "training cache needs to be refreshed", "Lower-face training");
var sourceError = "The factory TongueOut layout is ambiguous: this older label file does not identify Virtual Desktop or Steam Link. Regenerate its cache with --tracking-source VirtualDesktop or --tracking-source SteamLink, choosing the app used for that recording. For a new capture, update the matching Qpro module and record again.";
Expect(sourceError, "streaming source is unknown", "Lower-face training");
Expect("The native tongue source override conflicts with the recorded trackingSource", "source does not match", "Lower-face training");
Expect("The alternate Virtual Desktop tongue layout needs the full 70-channel factory schema. Restart the matching Qpro module and record the capture again.",
    "factory reference is incomplete", "Lower-face training");
foreach (var error in new[] { cacheError, sourceError })
{
    var result = HubActionFailure.Explain("Lower-face training", "exit 1", error);
    if (result.NextStep.Contains("Install runtime", StringComparison.OrdinalIgnoreCase) ||
        result.NextStep.Contains("reinstall", StringComparison.OrdinalIgnoreCase))
        throw new Exception("Capture mapping errors must not send users to runtime installation: " + result);
}
Expect("Close VRCFaceTracking and wait", "still open");
Expect("Qpro modules were removed, but saved-module recovery needs attention", "saved module needs attention");
Expect("Controller removal failed. Keep SteamVR closed.", "Controller removal needs attention");
Expect("Close SteamVR before installing or uninstalling", "SteamVR is still open");
Expect("WinError 206: filename or extension is too long", "path is too long");
Expect("DLL load failed: WinError 126", "library could not load");
Expect("ModuleNotFoundError: No module named 'tongue_visibility_calibration'", "could not be imported");
Expect("ModuleNotFoundError: No module named 'cv2'", "runtime package");
Expect("ModuleNotFoundError: No module named 'torchgen'", "runtime package", "AMD ROCm and PyTorch");
Expect("ModuleNotFoundError: No module named 'frida'", "runtime package", "Hand/controller components");
Expect("Missing Qpro script: C:\\QproFixture\\train_tongue_model.py", "file is missing");
Expect("The Qpro Virtual Desktop module was not found in its current module folder: C:\\QproFixture\\CustomLibs\\d6a8eeb2-3490-4d4f-bec1-9d5909da08ea. Close VRCFaceTracking, install the Qpro Virtual Desktop module in First-time setup, then reopen VRCFaceTracking.", "module needs installation or repair");
Expect("The Qpro Steam Link VRCFaceTracking module is not installed. Close VRCFaceTracking, use First-time setup > Install Steam Link module, then reopen VRCFaceTracking.", "module needs installation or repair");
Expect("The installed Qpro module card needs repair: The installed DLL does not match its module card's file hash. Close VRCFaceTracking, install the Qpro Steam Link module in First-time setup, then reopen VRCFaceTracking.", "module needs installation or repair");
Expect("The installed Qpro DLL differs from this app's module, even if both show the same version number. Close VRCFaceTracking, install the Qpro Virtual Desktop module in First-time setup, then reopen VRCFaceTracking.", "module needs installation or repair");
Expect("The selected Qpro DLL is not the only installed Qpro module. Close VRCFaceTracking, install the Qpro Virtual Desktop module in First-time setup, then reopen VRCFaceTracking.", "module needs installation or repair");
Expect("An alternate or legacy Qpro module slot is still present: 000-Qpro.SteamLink.dll. Close VRCFaceTracking, install the Qpro Virtual Desktop module in First-time setup, then reopen VRCFaceTracking.", "module needs installation or repair");
Expect("This app's packaged Qpro module is missing or unreadable. Extract the complete release ZIP, then retry.", "file is missing");
Expect("An unrelated action failed. Close VRCFaceTracking before trying a replacement module.", "did not complete");
Expect("PyTorch did not detect a supported discrete Radeon GPU", "GPU was not verified");
Expect("AMD packages imported successfully, but the discrete GPU check failed. See the detected adapter, driver or device error above.", "GPU was not verified");
Expect("AMD package/import verification failed before GPU detection. See the exact missing package, import or DLL error above. This does not mean the discrete GPU is unsupported.", "packages could not be verified");
Expect("No matching distribution found for amd-torch-device-gfx1031", "packages could not be verified");
Expect("The Quest is unavailable at its selected USB address.", "connection is unavailable");
Expect("error: device offline", "connection is unavailable");
Expect("A Windows native dependency could not load. Install or repair Microsoft Visual C++ Redistributable x64.", "library could not load");
Expect("", "did not complete");
var unknown = HubActionFailure.Explain("PC runtime", "final import check failed", "Unknown import failure");
if (unknown.NextStep.Contains("Visual C++", StringComparison.Ordinal)) throw new Exception("Unknown failures must not invent a DLL diagnosis.");
Expect("uid=0(root) Superuser permission granted\nUnknown import failure", "did not complete");
var downloadAfterProbe = HubActionFailure.Explain("AMD ROCm and PyTorch", "Installing AMD packages failed.",
    "WARNING: Existing ROCm packages need installation or repair: AMD PyTorch import failed before GPU detection. ModuleNotFoundError: No module named 'torchgen'. Setup will continue.\nERROR: No matching distribution found for requested wheel");
if (!downloadAfterProbe.Title.Contains("packages could not be verified", StringComparison.Ordinal))
    throw new Exception("An expected pre-install import probe must not misdiagnose the final download failure: " + downloadAfterProbe);
var pipAdvice = HubActionFailure.Explain("PC runtime", "Updating pip failed. For VCRUNTIME or missing DLL errors, repair Microsoft Visual C++.", "Download connection refused.");
if (pipAdvice.Title.Contains("library could not load", StringComparison.Ordinal))
    throw new Exception("Conditional DLL repair advice must not invent a missing library diagnosis.");
Console.WriteLine($"PASS: {failureChecks + 3} action failure guidance cases");

var expectedStop = HubTrackingExitFeedback.Complete("Camera tracking", 0, true, true, "STOP_REQUESTED");
var cancelledStart = HubTrackingExitFeedback.Complete("Camera tracking", 1, true, true, "startup was cancelled");
var stoppedIncomplete = HubTrackingExitFeedback.Complete("Pupil tracking", 1, true, false, "output still draining");
if (expectedStop is not null || cancelledStart is not null || stoppedIncomplete is not null)
    throw new Exception("A requested Stop or cancelled startup must not create an unexpected-worker failure.");
var selfExit = HubTrackingExitFeedback.Complete("Pupil tracking", 0, false, true, "PUPIL_OUTPUT_OFF");
if (selfExit is null || selfExit.IsError || !selfExit.Title.Contains("stopped", StringComparison.Ordinal))
    throw new Exception("An unexpected successful exit must explain that tracking stopped without claiming setup failed.");
var failedIncomplete = HubTrackingExitFeedback.Complete("Camera tracking", -1, false, false, "output still draining");
if (failedIncomplete?.IsError != true || !failedIncomplete.Detail.Contains("diagnosis may be incomplete", StringComparison.Ordinal))
    throw new Exception("An unexpected failure with incomplete output must retain the evidence limit.");
Console.WriteLine("PASS: 5 tracking-exit feedback cases (Stop, cancellation, self-exit and incomplete output)");

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

var moduleFailure = await HubProcessResult.RunAsync(Fixture("module-failure"), TimeSpan.FromSeconds(10));
var moduleFeedback = HubTrackingExitFeedback.Complete("Camera tracking", moduleFailure.ExitCode ?? -1,
    false, moduleFailure.CleanupError is null, moduleFailure.Diagnostics);
if (moduleFailure.ExitCode != 1 || moduleFeedback?.IsError != true ||
    !moduleFeedback.Title.Contains("module needs installation or repair", StringComparison.Ordinal))
    throw new Exception("The worker summary lost its final stderr failure: " + moduleFeedback);

// Exercise the live worker's EOF path, including a very short-lived process
// with its actual error on stderr and no terminating newline.
using (var worker = new Process { StartInfo = Fixture("module-failure"), EnableRaisingEvents = true })
{
    var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var summary = new TaskCompletionSource<HubTrackingExitFeedback?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var publishResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.StartInfo.RedirectStandardOutput = worker.StartInfo.RedirectStandardError = true;
    worker.OutputDataReceived += (_, e) => { if (e.Data is null) outputClosed.TrySetResult(); else lines.Enqueue(e.Data); };
    worker.ErrorDataReceived += (_, e) => { if (e.Data is null) errorClosed.TrySetResult(); else lines.Enqueue(e.Data); };
    worker.Exited += async (_, _) =>
    {
        try
        {
            var drained = await HubProcessResult.WaitForOutputDrainAsync(outputClosed.Task, errorClosed.Task, TimeSpan.FromSeconds(3));
            await publishResult.Task;
            summary.TrySetResult(HubTrackingExitFeedback.Complete("Camera tracking", worker.ExitCode, false, drained, string.Join("\n", lines)));
        }
        catch (Exception error) { summary.TrySetException(error); }
    };
    worker.Start();
    worker.BeginOutputReadLine();
    worker.BeginErrorReadLine();
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!worker.HasExited && DateTime.UtcNow < deadline) await Task.Delay(10);
    if (!worker.HasExited || summary.Task.IsCompleted)
        throw new Exception("The fast-exit fixture did not exercise startup before the diagnostic handoff.");
    var startupResult = HubTrackingExitFeedback.WaitForStartupAsync(summary.Task, CancellationToken.None);
    if (startupResult.IsCompleted) throw new Exception("Startup discarded its pending final diagnostic result.");
    publishResult.TrySetResult();
    var result = await startupResult;
    if (result?.IsError != true || !result.Title.Contains("module needs installation or repair", StringComparison.Ordinal))
        throw new Exception("The live-worker EOF path produced feedback before reading its final error: " + result);
    var startupError = new HubTrackingStartupException(result);
    if (startupError.Feedback != result || startupError.Message != result.Detail)
        throw new Exception("The startup error discarded the drained worker result before cleanup.");
}
var cancelledHandoff = new TaskCompletionSource<HubTrackingExitFeedback?>(TaskCreationOptions.RunContinuationsAsynchronously);
using (var cancelledStartup = new CancellationTokenSource())
{
    var handoff = HubTrackingExitFeedback.WaitForStartupAsync(cancelledHandoff.Task, cancelledStartup.Token);
    cancelledStartup.Cancel();
    try { await handoff; throw new Exception("Stop during the startup handoff produced stale failure feedback."); }
    catch (OperationCanceledException) when (cancelledStartup.IsCancellationRequested) { }
    cancelledHandoff.TrySetResult(moduleFeedback);
    if (!handoff.IsCanceled) throw new Exception("A delayed result replaced the cancelled startup outcome.");
}
Console.WriteLine("PASS: fast worker-exit startup handoff retains final stderr and respects Stop cancellation");
var incompleteOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
if (await HubProcessResult.WaitForOutputDrainAsync(Task.CompletedTask, incompleteOutput.Task, TimeSpan.FromMilliseconds(100)))
    throw new Exception("The live-worker EOF path must time out if a pipe stays open.");
incompleteOutput.TrySetResult();
if (!await HubProcessResult.WaitForOutputDrainAsync(Task.CompletedTask, incompleteOutput.Task, TimeSpan.FromSeconds(1)))
    throw new Exception("The live-worker EOF path did not finish once both streams closed.");

var watch = Stopwatch.StartNew();
var timeout = await HubProcessResult.RunAsync(Fixture("timeout"), TimeSpan.FromSeconds(2));
if (!timeout.TimedOut || timeout.ExitCode is null || watch.Elapsed > TimeSpan.FromSeconds(10) ||
    !timeout.Diagnostics.Contains("fixture stdout before exit", StringComparison.Ordinal) ||
    !timeout.Diagnostics.Contains("fixture stderr before exit", StringComparison.Ordinal))
    throw new Exception("Timed-out helper was not terminated with its preceding diagnostics: " + timeout);
if (HubTrackingExitFeedback.Complete("Camera tracking", timeout.ExitCode ?? -1, true,
    timeout.CleanupError is null, timeout.Diagnostics) is not null)
    throw new Exception("A terminated startup fixture must not be reported as a spontaneous tracking error.");
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
