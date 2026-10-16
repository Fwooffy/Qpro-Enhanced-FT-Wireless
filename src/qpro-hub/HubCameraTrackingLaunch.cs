namespace QproFaceTracking.Hub;

internal sealed record HubCameraTrackingSelection(
    string TrackingSource,
    bool TongueEnabled,
    string? GateModelPath,
    string? DirectionModelPath,
    string MaxFps,
    int Smoothing,
    string VisibilityMode,
    bool PupilEnabled,
    string PupilSensitivity,
    bool ShowPreview,
    string StopFile,
    string TrackingProfile = "responsive");

internal sealed record HubCameraTrackingPlan(string[] Arguments, string OutputDescription)
{
    internal bool Required => Arguments.Length > 0;
}

// The Hub and the headless regression test use this same argument builder.
// Model selection supplies the tongue weights; output switches still come
// from the user's explicit feature selections.
internal static class HubCameraTrackingLaunch
{
    internal static HubCameraTrackingPlan Create(HubCameraTrackingSelection selection)
    {
        if (!selection.TongueEnabled && !selection.PupilEnabled)
            return new([], "No camera overrides selected.");
        if (selection.TrackingSource is not ("VirtualDesktop" or "SteamLink"))
            throw new ArgumentException("Choose Virtual Desktop or Steam Link before starting camera tracking.");
        if (selection.TongueEnabled && (string.IsNullOrWhiteSpace(selection.GateModelPath) ||
                          string.IsNullOrWhiteSpace(selection.DirectionModelPath)))
            throw new ArgumentException("The selected tongue model needs both its visibility gate and direction checkpoint.");
        if (string.IsNullOrWhiteSpace(selection.StopFile))
            throw new ArgumentException("Camera tracking needs a local stop file.");
        if (selection.TrackingProfile is not ("responsive" or "balanced" or "low-cpu"))
            throw new ArgumentException("Choose Responsive, Balanced or Low CPU before starting camera tracking.");

        var arguments = new List<string> { "-TrackingSource", selection.TrackingSource,
            "-TrackingProfile", selection.TrackingProfile };
        if (selection.TongueEnabled)
        {
            arguments.AddRange(["-TonguePreview", "-MaxFps", selection.MaxFps,
                "-TongueSmoothing", selection.Smoothing.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-TongueVisibilityMode", selection.VisibilityMode,
                "-TongueModelPath", selection.GateModelPath!,
                "-TongueDirectionModelPath", selection.DirectionModelPath!]);
            if (selection.TongueEnabled) arguments.Add("-EnableTongueOutput");
        }
        if (selection.PupilEnabled)
        {
            arguments.AddRange(["-PupilOutput", "-PupilSensitivity", selection.PupilSensitivity]);
            if (!selection.TongueEnabled) arguments.AddRange(["-MaxFps", selection.MaxFps]);
        }
        if (!selection.ShowPreview) arguments.Add("-NoWindow");
        arguments.AddRange(["-StopFile", selection.StopFile]);
        string description = $"Tongue output {(selection.TongueEnabled ? "ON" : "OFF")}; " +
            $"pupil output {(selection.PupilEnabled ? "ON" : "OFF")}. " +
            "Cheeks use the streaming app's native face values and the Hub's individual 1/0 toggles.";
        return new(arguments.ToArray(), description);
    }
}
