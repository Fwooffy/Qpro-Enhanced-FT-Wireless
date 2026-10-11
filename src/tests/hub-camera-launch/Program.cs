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
    foreach (bool camera in new[] { false, true })
    foreach (bool pupil in new[] { false, true })
    foreach (bool preview in new[] { false, true })
    {
        var selection = Selection(source, tongue, camera, pupil, preview);
        var plan = HubCameraTrackingLaunch.Create(selection);
        Check(plan.Required == (tongue || camera || pupil), "only selected camera features start a process");
        if (!plan.Required) continue;
        var arguments = plan.Arguments;
        Check(Value(arguments, "-TrackingSource") == source, "streaming source stays isolated");
        Check(Value(arguments, "-TrackingProfile") == "responsive", "existing options preserve full processing cadence");
        Check(arguments.Contains("-EnableTongueOutput") == tongue, "tongue output follows explicit selection");
        Check(arguments.Contains("-EnableCheekOutput") == camera, "camera cheeks follow explicit selection");
        Check(arguments.Contains("-TonguePreview") == (tongue || camera), "camera cheeks work without enabling tongue output");
        Check(arguments.Contains("-PupilOutput") == pupil, "pupil output remains independent");
        Check(arguments.Contains("-NoWindow") == !preview, "preview selection is preserved");
        Check(Value(arguments, "-MaxFps") == "24" && arguments.Count(value => value == "-MaxFps") == 1,
            "a combined process receives only one FPS setting");
        Check(Value(arguments, "-StopFile") == selection.StopFile, "all camera combinations use the same stop file");
        if (tongue || camera)
        {
            Check(Value(arguments, "-TongueModelPath") == selection.GateModelPath,
                "selected visibility gate is retained including path spaces");
            Check(Value(arguments, "-TongueDirectionModelPath") == selection.DirectionModelPath,
                "selected combined direction model is retained including path spaces");
            Check(Value(arguments, "-TongueSmoothing") == "55", "the slider value reaches the receiver");
            Check(Value(arguments, "-TongueVisibilityMode") == "weighted", "visibility selection is retained");
        }
        else Check(!arguments.Contains("-TongueModelPath"), "pupil-only tracking does not load an unrelated model");
        Check(plan.OutputDescription.Contains(camera ? "camera cheek puff ON" : "camera cheek puff OFF"),
            "the startup message reflects effective camera cheek output");
    }

    var combinedOff = HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, false, true));
    foreach (var profile in new[] { "responsive", "balanced", "low-cpu" })
    {
        var plan = HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, true, false) with
            { TrackingProfile = profile });
        Check(Value(plan.Arguments, "-TrackingProfile") == profile, "processing profile reaches the launcher");
        Check(Value(plan.Arguments, "-MaxFps") == "24", "profile preserves the camera FPS cap");
        Check(!plan.Arguments.Contains("-TongueProcessingFps"), "profiles do not reduce tongue cadence");
    }
    Throws(() => HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, true, false) with
        { TrackingProfile = "unknown" }), "unknown processing profile gets actionable validation");
    Check(!combinedOff.Arguments.Contains("-EnableCheekOutput"), "a combined model alone does not opt into camera output");
    Check(combinedOff.OutputDescription.Contains("native-source adjustments"), "off explicitly describes the native cheek source");
    Throws(() => HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, true, false, true) with
        { ModelHasCameraCheeks = false }), "plain model cannot silently send camera cheeks");
    Throws(() => HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", false, true, false, true) with
        { DirectionModelPath = null }), "camera output cannot silently fall back to the visibility gate");
    Throws(() => HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", true, false, false, true) with
        { GateModelPath = "" }), "missing gate is rejected");
    Check(HubCameraTrackingLaunch.DescribeCheekSource(true, false, false).Contains("available but OFF"),
        "model selection and output selection are clearly distinguished");
    Check(HubCameraTrackingLaunch.DescribeCheekSource(true, true, false).Contains("next start"),
        "an enabled stopped feature is not described as already loaded");
    Check(HubCameraTrackingLaunch.DescribeCheekSource(false, true, false).Contains("unavailable"),
        "a stale checked selection gets actionable feedback");

    if (args.Length == 1)
    {
        var models = Path.GetFullPath(args[0]);
        var gate = Path.Combine(models, "qpro-stereo-tongue-v12-gate.pt");
        var direction = Path.Combine(models, "qpro-stereo-tongue-v12-direction.pt");
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(models, "qpro-stereo-tongue-v12.metadata.json")))?.AsObject();
        Check(File.Exists(gate) && File.Exists(direction), "actual v12 model pair exists");
        var plan = HubCameraTrackingLaunch.Create(Selection("VirtualDesktop", false, true, false, false) with
            { GateModelPath = gate, DirectionModelPath = direction, ModelHasCameraCheeks = HubModelMetadata.HasCameraCheeks(metadata) });
        Check(Value(plan.Arguments, "-TongueDirectionModelPath") == direction && plan.Arguments.Contains("-EnableCheekOutput"),
            "actual bundled v12 metadata and paths enable the camera-only launch");
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

static HubCameraTrackingSelection Selection(string source, bool tongue, bool camera, bool pupil, bool preview) =>
    new(source, tongue, camera, true, @"C:\release folder\models\qpro-stereo-tongue-v12-gate.pt",
        @"C:\release folder\models\qpro-stereo-tongue-v12-direction.pt", "24", 55, "weighted",
        pupil, "1.4", preview, @"C:\release folder\config\tracking.stop");
