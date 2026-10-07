using QproFaceTracking.Hub;

var checks = 0;
void Check(HubActivityClassifier classifier, string line, ActivitySeverity expected)
{
    var actual = classifier.Classify(line);
    if (actual != expected) throw new Exception($"Expected {expected}, got {actual}: {line}");
    checks++;
}

// Standalone output and actual Hub process-completion formats.
foreach (var line in new[]
{
    "Hub ready.", "[Camera tracking] Frame 42 received", "No errors", "0 errors, 0 warnings.",
    "[PC runtime setup] error=0", "errors: 0", "Successful output: error handling initialized.",
    "[Camera tracking] Error handling is enabled", "No errors were reported.",
    "Completed without errors", "No warnings", "warnings=0", "Warnings were not found",
    "GPU is not unsupported", "No unsupported devices found", "No unverified mounts were found.",
    "Cleanup does not need attention", "GPU fallback is not needed", "CPU fallback=0",
    "PC runtime setup finished with code 0 after 17 seconds.", "[Camera tracking] exited with code 0.",
    "Worker finished with code0", "Worker exited with code -0.", "[error handling] Ready",
    "ROCm 7.2.1 fallback", "Older extracted-release ROCm environments are kept as fallbacks.",
    "TRAIN_EPOCH current=1 total=20 focus=direction", "Loss error=0.002", "Loss error=1.002", "Error report saved successfully.",
    "No failed checks", "Failure handling initialized successfully", "[INFO] 0 errors",
    "ERROR: no errors", "WARNING: no warnings", "ERROR: No errors were reported.",
    "ERROR: error=0; validation completed successfully.", "WARNING: No warnings were reported.", "", "   "
}) Check(new(), line, ActivitySeverity.Normal);

foreach (var line in new[]
{
    "[PC runtime setup] ERROR: Could not find a version that satisfies the requirement torch",
    "ModuleNotFoundError: No module named 'cv2'", "RuntimeError: unsupported GPU",
    "torch.cuda.OutOfMemoryError: allocation failed", "System.IO.IOException: device unavailable",
    "KeyboardInterrupt", "Unhandled exception. System.InvalidOperationException: failure",
    "START FAILED: ADB process did not start", "PC runtime setup failed: access denied",
    "[Camera tracking] Output handling failed: System.IO.IOException",
    "PC runtime setup finished with code 1 after 23 seconds.", "[Camera tracking] exited with code -1.",
    "Worker exited with code-1", "Worker exited with code 4294967295.",
    "[Setup] + CategoryInfo : ObjectNotFound: (python:String) [], CommandNotFoundException",
    "[Setup] + FullyQualifiedErrorId : CommandNotFoundException",
    "At D:\\Qpro\\setup.ps1:line:21 char:3", "CONTROLLER_ERROR {\"message\":\"Stopped\"}",
    "HANDS_CLEANUP_FAILED {\"error\":\"Recovery required\"}", "2 errors", "errors=1",
    "No errors in download; verification failed", "0 warnings; install failed", "[ERROR] Connection lost",
    "[PC runtime setup] [ERROR] Connection lost", "Could not copy diagnostics: access denied",
    "[Setup] Get-Item : The path was not found.", "[Setup] setup.ps1 : The package was not found.",
    "[Setup] python : Traceback (most recent call last):", "ERROR: GPU failed; falling back to CPU",
    "[Camera tracking] receiver.py: error: unrecognized arguments: --camera-preview",
    "ERROR: no errors during download; verification failed", "START FAILED: Unsupported GPU",
    "ERROR: Connection lost; 0 warnings"
}) Check(new(), line, ActivitySeverity.Error);

foreach (var line in new[]
{
    "WARNING: CUDA is unavailable. CPU fallback is active; training can take substantially longer.",
    "[Camera tracking] GPU fallback active", "CUDA failed; falling back to CPU",
    "Using CPU fallback", "GPU initialization unavailable; using fallback", "Falling back to CPU",
    "Unsupported tracking-engine size", "Cleanup remains unverified; keep the Hub open.",
    "Independent gaze recovery needs attention: model still mounted",
    "Qpro removed; saved module needs attention", "The GPU was not verified", "GPU is not supported",
    "No errors; GPU fallback active", "No warnings; cleanup remains unverified", "[WARN] Slow GPU",
    "[Compatibility] This version is not compatible with independent gaze",
    "[Compatibility] Independent gaze engine not validated · sample.", "No validated eye profile found.",
    "2 warnings", "WARNING: GPU initialization failed; continuing on CPU", "ERROR: no errors, GPU fallback active"
}) Check(new(), line, ActivitySeverity.Warning);

// Windows PowerShell multi-line records, including interleaved independent output.
var powershell = new HubActivityClassifier();
foreach (var (line, expected) in new[]
{
    ("[PC runtime setup] Install-QproRuntime.ps1 : Cannot find the requested file.", ActivitySeverity.Error),
    ("[PC runtime setup]     The requested path was unavailable.", ActivitySeverity.Error),
    ("[PC runtime setup] At D:\\Qpro\\setup.ps1:line:42 char:1", ActivitySeverity.Error),
    ("[Camera tracking] TRAIN_EPOCH current=1 total=20 focus=direction", ActivitySeverity.Normal),
    ("[PC runtime setup] + Install-QproRuntime", ActivitySeverity.Error),
    ("[PC runtime setup] + ~~~~~~~~~~~~~~~~~~~", ActivitySeverity.Error),
    ("[PC runtime setup]     + CategoryInfo          : ObjectNotFound: (path:String) [], ItemNotFoundException", ActivitySeverity.Error),
    ("[PC runtime setup]     + FullyQualifiedErrorId : PathNotFound", ActivitySeverity.Error),
    ("[Camera tracking]   Camera ready", ActivitySeverity.Normal),
    ("[PC runtime setup] Verification restarted.", ActivitySeverity.Normal),
    ("[PC runtime setup]   Download complete.", ActivitySeverity.Normal)
}) Check(powershell, line, expected);

// Python traceback context stays with its own process, including chained exceptions.
var python = new HubActivityClassifier();
foreach (var (line, expected) in new[]
{
    ("[Camera tracking] Traceback (most recent call last):", ActivitySeverity.Error),
    ("[Camera tracking]   File \"receiver.py\", line 42, in <module>", ActivitySeverity.Error),
    ("[PC runtime setup]   Download complete", ActivitySeverity.Normal),
    ("[Camera tracking]     import cv2", ActivitySeverity.Error),
    ("[Camera tracking]     ^^^^^^^^^^", ActivitySeverity.Error),
    ("[Camera tracking] ModuleNotFoundError: No module named 'cv2'", ActivitySeverity.Error),
    ("[Camera tracking] ", ActivitySeverity.Normal),
    ("[Camera tracking] During handling of the above exception, another exception occurred:", ActivitySeverity.Error),
    ("[Camera tracking] Traceback (most recent call last):", ActivitySeverity.Error),
    ("[Camera tracking]   File \"receiver.py\", line 44, in <module>", ActivitySeverity.Error),
    ("[Camera tracking] RuntimeError: camera unavailable", ActivitySeverity.Error),
    ("[Camera tracking] exited with code -1.", ActivitySeverity.Error),
    ("[Camera tracking]   Camera ready", ActivitySeverity.Normal)
}) Check(python, line, expected);

var lifecycle = new HubActivityClassifier();
Check(lifecycle, "[PC runtime setup] Traceback (most recent call last):", ActivitySeverity.Error);
Check(lifecycle, "PC runtime setup finished with code 0 after 1 seconds.", ActivitySeverity.Normal);
Check(lifecycle, "[PC runtime setup]   Ready", ActivitySeverity.Normal);
Check(lifecycle, "[PC runtime setup] Traceback (most recent call last):", ActivitySeverity.Error);
Check(lifecycle, "Starting PC runtime setup…", ActivitySeverity.Normal);
Check(lifecycle, "[PC runtime setup]   Ready", ActivitySeverity.Normal);
Check(lifecycle, "Traceback (most recent call last):", ActivitySeverity.Error);
Check(lifecycle, "  raise RuntimeError('missing')", ActivitySeverity.Error);
Check(lifecycle, "[Other process]   Normal progress", ActivitySeverity.Normal);
lifecycle.Reset();
Check(lifecycle, "  Normal progress", ActivitySeverity.Normal);
Check(lifecycle, "[PC runtime setup]   Normal progress", ActivitySeverity.Normal);

Console.WriteLine($"PASS: {checks} activity severity checks (standalone signals, negation, multiline errors, interleaving and reset).");
