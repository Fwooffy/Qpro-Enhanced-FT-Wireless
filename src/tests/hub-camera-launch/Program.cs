using System.Text.Json.Nodes;
using QproFaceTracking.Hub;

int checks = 0;
try
{
    var plainMetadata = new JsonObject { ["version"] = 8 };
    var combinedMetadata = new JsonObject { ["version"] = 12, ["hasCameraCheeks"] = true,
        ["modelKind"] = "camera-cheeks-experimental", ["isExperimental"] = true };
    Check(!HubModelMetadata.HasCameraCheeks(plainMetadata), "plain tongue models have no camera cheek capability");
    Check(HubModelMetadata.HasCameraCheeks(combinedMetadata), "combined metadata advertises cheek capability");
    Check(!HubModelMetadata.HasCameraCheeks(new JsonObject { ["hasCameraCheeks"] = "true" }),
        "malformed string capability is not accepted");
    foreach (string source in new[] { "VirtualDesktop", "SteamLink" })
    foreach (bool tongue in new[] { false, true })
    foreach (bool pupil in new[] { false, true })
    foreach (bool preview in new[] { false, true })
    {
        var selection = Selection(source, tongue, pupil, preview);
        var plan = HubCameraTrackingLaunch.Create(selection);
        Check(plan.Required == (tongue || pupil), "only selected tongue/pupil features start a process");
        if (!plan.Required) continue;
        var arguments = plan.Arguments;
        Check(Value(arguments, "-TrackingSource") == source, "streaming source stays isolated");
        Check(Value(arguments, "-TrackingProfile") == "responsive", "existing options preserve full processing cadence");
        Check(arguments.Contains("-EnableTongueOutput") == tongue, "tongue output follows explicit selection");
        Check(!arguments.Contains("-EnableCheekOutput"), "retired camera cheek output is never enabled");
        Check(arguments.Contains("-TonguePreview") == tongue, "tongue weights load only for enabled tongue output");
        Check(arguments.Contains("-PupilOutput") == pupil, "pupil output remains independent");
        Check(arguments.Contains("-NoWindow") == !preview, "preview selection is preserved");
        Check(Value(arguments, "-MaxFps") == "24" && arguments.Count(value => value == "-MaxFps") == 1,
            "a combined process receives only one FPS setting");
        Check(Value(arguments, "-StopFile") == selection.StopFile, "all camera combinations use the same stop file");
        if (tongue)
        {
            Check(Value(arguments, "-TongueModelPath") == selection.GateModelPath,
                "selected visibility gate is retained including path spaces");
            Check(Value(arguments, "-TongueDirectionModelPath") == selection.DirectionModelPath,
                "selected combined direction model is retained including path spaces");
            Check(Value(arguments, "-TongueSmoothing") == "55", "the slider value reaches the receiver");
            Check(Value(arguments, "-TongueVisibilityMode") == "weighted", "visibility selection is retained");
        }
        else Check(!arguments.Contains("-TongueModelPath"), "pupil-only tracking does not load an unrelated model");
        Check(plan.OutputDescription.Contains("native face values") && !plan.OutputDescription.Contains("camera cheek"),
            "the startup message describes native cheek output without a retired feature");
    }

    var combinedOff = HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, true));
    foreach (var profile in new[] { "responsive", "balanced", "low-cpu" })
    {
        var plan = HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, true, false) with
            { TrackingProfile = profile });
        Check(Value(plan.Arguments, "-TrackingProfile") == profile, "processing profile reaches the launcher");
        Check(Value(plan.Arguments, "-MaxFps") == "24", "profile preserves the camera FPS cap");
        Check(!plan.Arguments.Contains("-TongueProcessingFps"), "profiles do not reduce tongue cadence");
    }
    Throws(() => HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, true, false) with
        { TrackingProfile = "unknown" }), "unknown processing profile gets actionable validation");
    Check(!combinedOff.Arguments.Contains("-EnableCheekOutput"), "a legacy combined model cannot enable retired camera output");
    Check(combinedOff.OutputDescription.Contains("native face values"), "the startup message describes the native cheek source");
    Throws(() => HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, true) with
        { DirectionModelPath = null }), "tongue output cannot silently fall back to the visibility gate");
    Throws(() => HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, true) with
        { GateModelPath = "" }), "missing gate is rejected");

    if (args.Length == 1)
    {
        var models = Path.GetFullPath(args[0]);
        var gate = Path.Combine(models, "qpro-stereo-tongue-v12-gate.pt");
        var direction = Path.Combine(models, "qpro-stereo-tongue-v12-direction.pt");
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(models, "qpro-stereo-tongue-v12.metadata.json")))?.AsObject();
        Check(File.Exists(gate) && File.Exists(direction), "actual v12 model pair exists");
        var plan = HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, false) with
            { GateModelPath = gate, DirectionModelPath = direction });
        Check(Value(plan.Arguments, "-TongueDirectionModelPath") == direction && !plan.Arguments.Contains("-EnableCheekOutput"),
            "actual legacy combined metadata and paths launch tongue output only");
    }
    Console.WriteLine($"Hub camera launch checks passed: {checks}. No processes, modules, or headset changes.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Hub camera launch check failed: {error}");
    return 1;
}

void Check(bool valid, string description)
{
    if (!valid) throw new InvalidOperationException(description);
    checks++;
}

void Throws(Action action, string description)
{
    try { action(); }
    catch (ArgumentException) { checks++; return; }
    throw new InvalidOperationException(description);
}

static string? Value(string[] arguments, string option)
{
    int index = Array.IndexOf(arguments, option);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static HubCameraTrackingSelection Selection(string source, bool tongue, bool pupil, bool preview) =>
    new(source, tongue, @"C:\release folder\models\qpro-stereo-tongue-v12-gate.pt",
        @"C:\release folder\models\qpro-stereo-tongue-v12-direction.pt", "24", 55, "weighted",
        pupil, "1.4", preview, @"C:\release folder\config\tracking.stop");
