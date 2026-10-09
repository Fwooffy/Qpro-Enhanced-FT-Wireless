using System.Text.Json.Nodes;
using QproFaceTracking.Hub;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static JsonObject Diagnostic(bool supported = true, string validation = "live-reference") => JsonNode.Parse("""
{
  "format": "qpro-gaze-compatibility-diagnostic-v1",
  "firmware": {
    "model": "Quest Pro", "productDevice": "seacliff", "buildIncremental": "51503870024400340",
    "buildDisplayId": "207.0.0.218", "buildFingerprint": "synthetic-fixture/seacliff:release"
  },
  "firmwareApproved": true,
  "engine": {
    "path": "/odm/lib64/libtrackingengines.so", "size": 47418280,
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "profile": "fixture-profile"
  },
  "engineSupported": true, "engineProfileValidation": "live-reference", "engineCompatibilityReason": null,
  "modelPath": "/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/bolt/bolt.ptl",
  "modelPathMounted": false, "modelDiscoveryError": null,
  "gazeEnvironment": {
    "scanComplete": true, "modules": [], "relevantMounts": [], "transparentOverlayMounts": [],
    "overlayfsOdmUpperState": "absent", "overlayfsOdmUpperInspectedPath": "/dev/mount_overlayfs/upper/odm",
    "experimentalModelProperty": "false"
  },
  "gazeEnvironmentError": null, "gazePreflightPassed": true,
  "modelPatchValidated": false, "headsetTrackingChanged": false
}
""")!.AsObject().Also(report =>
{
    report["engineSupported"] = supported;
    report["engineProfileValidation"] = supported ? validation : null;
    if (!supported)
    {
        report["engine"]!.AsObject().Remove("profile");
        report["engineCompatibilityReason"] = "SHA-256 differs from supported profiles for this engine size.";
    }
});

static HubCompatibilityReport Parse(JsonObject report) => HubCompatibilityReport.TryParse(report.ToJsonString(), out var value)
    ? value! : throw new Exception("A complete diagnostic was rejected.");
static void Reject(JsonObject report, string reason) => Check(!HubCompatibilityReport.TryParse(report.ToJsonString(), out _), reason);

var live = Parse(Diagnostic());
Check(live.CanPrepareGaze && !live.NeedsAttention && live.BuildIncremental == "51503870024400340", "Live profile identity was lost.");
Check(live.Summary.Contains("still needs preparation") && !live.Summary.Contains("gaze is ready", StringComparison.OrdinalIgnoreCase),
    "A read-only engine check was presented as a prepared/live patch.");
var analysis = Parse(Diagnostic(validation: "firmware-analysis"));
Check(analysis.CanPrepareGaze && analysis.NeedsAttention && analysis.Summary.Contains("Live behavior is not yet verified"),
    "Firmware analysis was confused with a live reference.");
var unsupported = Parse(Diagnostic(supported: false));
Check(unsupported.FirmwareApproved && !unsupported.EngineSupported && !unsupported.CanPrepareGaze &&
    unsupported.NextStep.Contains("Leave Independent Eye Gaze off"), "Firmware approval bypassed exact engine support.");
var discovery = Diagnostic();
discovery["modelPath"] = null; discovery["modelPathMounted"] = null; discovery["modelDiscoveryError"] = "Model selection is ambiguous.";
Check(!Parse(discovery).CanPrepareGaze && Parse(discovery).Summary.Contains("could not be selected"), "Model ambiguity became ready.");
var mountReadError = Diagnostic();
mountReadError["modelPathMounted"] = null; mountReadError["modelDiscoveryError"] = "Could not read model mount state.";
Check(!Parse(mountReadError).CanPrepareGaze, "A model path was mistaken for a verified mount check.");
var blocked = Diagnostic();
blocked["gazePreflightPassed"] = false; blocked["gazeEnvironmentError"] = "Active Magisk gaze module.";
blocked["gazeEnvironment"]!["modules"] = JsonNode.Parse("""
[{"directory":"fixture-module","id":"fixture-module","name":"Fixture gaze","enabled":true,"pendingRemoval":false,"gazeRelevant":true,"relevantFiles":["service.sh"]}]
""");
var magisk = Parse(blocked);
Check(!magisk.CanPrepareGaze && magisk.HasActiveMagiskGaze && !magisk.NeedsAttention &&
    magisk.NextStep.Contains("Skip Check gaze setup and Prepare gaze") && magisk.NextStep.Contains("Leave Independent Eye Gaze off"),
    "A Magisk method required Hub gaze setup or bypassed its conflict guard.");
Check(magisk.Summary.Contains("Magisk gaze module") && !magisk.Summary.Contains("needs attention"),
    "A detected Magisk module was reported as a failed required setup.");
var magiskUnknownEngine = Diagnostic(supported: false);
magiskUnknownEngine["gazeEnvironment"] = blocked["gazeEnvironment"]!.DeepClone();
magiskUnknownEngine["gazeEnvironmentError"] = "Active Magisk gaze module.";
magiskUnknownEngine["gazePreflightPassed"] = false;
Check(!Parse(magiskUnknownEngine).CanPrepareGaze && !Parse(magiskUnknownEngine).NeedsAttention &&
    Parse(magiskUnknownEngine).NextStep.Contains("Skip Check gaze setup and Prepare gaze"),
    "An unsupported Hub engine forced gaze setup on the separate Magisk method.");
var disabledModule = blocked.DeepClone().AsObject();
disabledModule["gazeEnvironment"]!["modules"]![0]!["enabled"] = false;
disabledModule["gazeEnvironmentError"] = null; disabledModule["gazePreflightPassed"] = true;
Check(Parse(disabledModule).CanPrepareGaze && !Parse(disabledModule).HasActiveMagiskGaze,
    "A disabled Magisk module was mistaken for the active gaze method.");
blocked["gazePreflightPassed"] = true; blocked["gazeEnvironmentError"] = null;
Reject(blocked, "An active gaze module bypassed preflight.");

foreach (var safetyFlag in new[] { "modelPatchValidated", "headsetTrackingChanged" })
{
    var invalid = Diagnostic(); invalid[safetyFlag] = true; Reject(invalid, "A mutating/validated result entered the read-only parser.");
    invalid[safetyFlag] = "false"; Reject(invalid, "A string safety flag was accepted.");
    invalid.Remove(safetyFlag); Reject(invalid, "A missing safety flag was accepted.");
}
var unknown = Diagnostic(); unknown["firmware"]!["serial"] = "synthetic-private-id"; Reject(unknown, "A serial-bearing report was accepted.");
var wrongHeadset = Diagnostic(); wrongHeadset["firmware"]!["model"] = "Quest 3"; wrongHeadset["firmware"]!["productDevice"] = "eureka";
Reject(wrongHeadset, "An unsupported headset was accepted.");
var badHash = Diagnostic(); badHash["engine"]!["sha256"] = "invalid"; Reject(badHash, "A malformed engine hash was accepted.");
var unknownTier = Diagnostic(); unknownTier["engineProfileValidation"] = "assumed"; Reject(unknownTier, "Unknown validation tier was accepted.");
var traversal = Diagnostic(); traversal["modelPath"] = "/odm/etc/eyetracking/runtime/models/../bolt/bolt.ptl"; Reject(traversal, "A model traversal path was accepted.");
var controlText = Diagnostic(); controlText["firmware"]!["buildIncremental"] = "fixture\u0000build"; Reject(controlText, "Control text entered UI identity.");
var oversizedText = Diagnostic(); oversizedText["firmware"]!["model"] = new string('x', 129); Reject(oversizedText, "An oversized identity was accepted.");
var wrongBoolean = Diagnostic(); wrongBoolean["engineSupported"] = 1; Reject(wrongBoolean, "Numeric boolean was accepted.");
foreach (var invalid in new[] { "{broken", "[]", "{}", new string('x', 262145),
    Diagnostic().ToJsonString().Replace("\"engineSupported\":true", "\"engineSupported\":true,\"engineSupported\":false") })
    Check(!HubCompatibilityReport.TryParse(invalid, out _), "Malformed, duplicate or oversized diagnostic was accepted.");

static JsonObject HandCheck(bool compatible = true) => JsonNode.Parse("""
{"compatible":true,"androidVersion":"1.34.22.0","pcDriverSha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
"fridaVersion":"17.18.0","settings":{"hand_tracking_enabled":"false","multimodal_hands_and_controllers_enabled":"false","simultaneous_hands_and_controllers_mode":"1"},
"settingSource":"current-user","experimental":true,"runtimeValidated":false,"problems":[],"warnings":["Preliminary hand setting is inactive; live input will be checked."],"componentsReady":true,"componentProblems":[]}
""")!.AsObject().Also(report => report["compatible"] = compatible);
var hands = HandCheck();
Check(HubControllerFeedback.TryParseCheckLine(HubControllerFeedback.CheckPrefix + hands.ToJsonString(), out var handResult) &&
    !handResult!.IsError && handResult.Detail.Contains("live optical input has not been verified") &&
    handResult.NextStep.Contains("warning"), "Preliminary inactive values became a blocking menu failure or live readiness.");
hands["compatible"] = false; hands["problems"] = new JsonArray("This Virtual Desktop Streamer driver has no validated hand profile.");
Check(HubControllerFeedback.TryParseCheckLine(HubControllerFeedback.CheckPrefix + hands.ToJsonString(), out handResult) &&
    handResult!.IsError && handResult.Detail.Contains("no validated hand profile"), "The actual incompatible version detail was hidden.");
hands["componentsReady"] = false; hands["componentProblems"] = new JsonArray("Matching Frida is missing.");
Check(HubControllerFeedback.TryParseCheckLine(HubControllerFeedback.CheckPrefix + hands.ToJsonString(), out handResult) &&
    handResult!.NextStep.Contains("install the hand/controller components"), "Missing components lost their repair action.");
hands["compatible"] = true;
Check(!HubControllerFeedback.TryParseCheckLine(HubControllerFeedback.CheckPrefix + hands.ToJsonString(), out _), "Contradictory hand evidence was accepted.");
foreach (var property in new[] { "androidVersion", "pcDriverSha256" })
{
    var invalid = HandCheck(); invalid[property] = null;
    Check(!HubControllerFeedback.TryParseCheckLine(HubControllerFeedback.CheckPrefix + invalid.ToJsonString(), out _),
        "Missing native identity was presented as compatible.");
}
var invalidHandHash = HandCheck(); invalidHandHash["pcDriverSha256"] = "invalid";
Check(!HubControllerFeedback.TryParseCheckLine(HubControllerFeedback.CheckPrefix + invalidHandHash.ToJsonString(), out _), "Malformed driver hash was accepted.");
var invalidHandMode = HandCheck(); invalidHandMode["settings"]!["simultaneous_hands_and_controllers_mode"] = "0";
Check(!HubControllerFeedback.TryParseCheckLine(HubControllerFeedback.CheckPrefix + invalidHandMode.ToJsonString(), out _), "Contradictory mode was accepted.");

static HubControllerFeedback Cleanup(string json) => HubControllerFeedback.ParseCleanupLine(HubControllerFeedback.CleanupPrefix + json);
Check(Cleanup("""{"confirmed":false,"restoration":"unconfirmed","reader":"stopped","problems":["Adapter restore failed."]}""").IsError,
    "A failed adapter restoration became successful because reader stopped.");
Check(Cleanup("""{"confirmed":true,"restoration":"unconfirmed","reader":"stopped","problems":[]}""").IsError, "Unconfirmed restoration was ignored.");
Check(Cleanup("""{"confirmed":true,"restoration":"confirmed","reader":"stopped","problems":["Failed."]}""").IsError, "Cleanup problems were ignored.");
Check(Cleanup("""{"workerPid":42,"restoration":"still-running"}""").State == ControllerFeedbackState.Waiting, "Pending worker became a final success.");
Check(Cleanup("""{"reader":"stopped","inputs":"disabled"}""").State == ControllerFeedbackState.Waiting, "Interim worker report became final confirmation.");
Check(Cleanup("""{"confirmed":true,"restoration":"confirmed","reader":"stopped","problems":[]}""").State == ControllerFeedbackState.Restored,
    "Explicit final restoration was not recognized.");
foreach (var invalid in new[] { "{broken", "{}", "[]", """{"confirmed":"true","restoration":"confirmed","reader":"stopped","problems":[]}""",
    """{"confirmed":false,"confirmed":true,"restoration":"confirmed","reader":"stopped","problems":[]}""",
    """{"confirmed":true,"restoration":"confirmed","reader":"stopped","problems":false}""" })
    Check(Cleanup(invalid).IsError, "Malformed or incomplete cleanup became successful.");

var validHandLine = HubControllerFeedback.CheckPrefix + HandCheck().ToJsonString();
var missingUtilityReport = new HubControllerUtilityResult();
Check(missingUtilityReport.Complete(true, 0)?.IsError == true, "A zero exit without a report became compatible.");
Check(missingUtilityReport.Complete(true, 1) is null, "An early helper failure lost its actual diagnostic to a missing-report message.");
var validUtilityReport = new HubControllerUtilityResult(); validUtilityReport.Observe(validHandLine);
Check(validUtilityReport.Complete(true, 0)?.State == ControllerFeedbackState.Checked &&
    validUtilityReport.Complete(true, 0)?.IsError == false, "A single bounded precheck was not retained.");
Check(validUtilityReport.Complete(true, 1) is null, "A valid report hid the helper's failed exit.");
var malformedThenValid = new HubControllerUtilityResult();
malformedThenValid.Observe("HANDS_CHECK {broken"); malformedThenValid.Observe(validHandLine);
Check(malformedThenValid.Complete(true, 0)?.IsError == true, "A later valid line erased malformed evidence.");
var duplicateUtilityReport = new HubControllerUtilityResult();
duplicateUtilityReport.Observe(validHandLine); duplicateUtilityReport.Observe(validHandLine);
Check(duplicateUtilityReport.Complete(true, 0)?.IsError == true, "Duplicate checks became one authoritative result.");
var blockedUtilityReport = new HubControllerUtilityResult();
blockedUtilityReport.Observe(HubControllerFeedback.CheckPrefix + HandCheck(false).ToJsonString());
Check(blockedUtilityReport.Complete(true, 0)?.IsError == true, "An incompatible result was cleared by a zero exit.");
var failedCleanupThenSuccess = new HubControllerUtilityResult();
failedCleanupThenSuccess.Observe("""CONTROLLER_CLEANUP {"confirmed":false,"restoration":"unconfirmed","reader":"stopped","problems":["Restore failed."]}""");
failedCleanupThenSuccess.Observe("""CONTROLLER_CLEANUP {"confirmed":true,"restoration":"confirmed","reader":"stopped","problems":[]}""");
Check(failedCleanupThenSuccess.Complete(false, 0)?.IsError == true, "A later cleanup success erased restoration failure evidence.");
var pendingCleanup = new HubControllerUtilityResult();
pendingCleanup.Observe("""CONTROLLER_CLEANUP {"workerPid":42,"restoration":"still-running"}""");
Check(pendingCleanup.Complete(false, 0)?.IsError == true, "A helper exit cleared pending restoration.");
var completedCleanup = new HubControllerUtilityResult();
completedCleanup.Observe("""CONTROLLER_CLEANUP {"confirmed":true,"restoration":"confirmed","reader":"stopped","problems":[]}""");
Check(completedCleanup.Complete(false, 0)?.State == ControllerFeedbackState.Restored, "Explicit cleanup confirmation was not retained.");
var concurrentReports = new HubControllerUtilityResult();
Parallel.Invoke(() => concurrentReports.Observe(validHandLine), () => concurrentReports.Observe(validHandLine));
Check(concurrentReports.Complete(true, 0)?.IsError == true, "Concurrent stream callbacks lost duplicate-result evidence.");

Console.WriteLine("PASS: bounded compatibility reports, exact engine tiers, actionable controller feedback, confirmed cleanup and result-gated utility success.");

static class FixtureExtensions
{
    internal static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
