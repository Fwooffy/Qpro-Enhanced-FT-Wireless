namespace QproFaceTracking.Hub;

internal sealed record HubCameraTrackingSelection(
    string TrackingSource,
    bool TongueEnabled,
    bool CameraCheeksEnabled,
    bool ModelHasCameraCheeks,
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
// Choosing a combined model supplies its weights; output switches still come
// from the user's explicit feature selections.
internal static class HubCameraTrackingLaunch
{
    internal static HubCameraTrackingPlan Create(HubCameraTrackingSelection selection)
    {
        bool lowerFace = selection.TongueEnabled || selection.CameraCheeksEnabled;
        if (!lowerFace && !selection.PupilEnabled)
            return new([], "No camera overrides selected.");
        if (selection.TrackingSource is not ("VirtualDesktop" or "SteamLink"))
            throw new ArgumentException("Choose Virtual Desktop or Steam Link before starting camera tracking.");
        if (selection.CameraCheeksEnabled && !selection.ModelHasCameraCheeks)
            throw new ArgumentException("Camera cheek puff requires a model with trained cheek outputs. Choose a tongue + cheeks model, or turn Camera cheek puff off.");
        if (lowerFace && (string.IsNullOrWhiteSpace(selection.GateModelPath) ||
                          string.IsNullOrWhiteSpace(selection.DirectionModelPath)))
            throw new ArgumentException("The selected lower-face model needs both its visibility gate and direction checkpoint.");
        if (string.IsNullOrWhiteSpace(selection.StopFile))
            throw new ArgumentException("Camera tracking needs a local stop file.");
        if (selection.TrackingProfile is not ("responsive" or "balanced" or "low-cpu"))
            throw new ArgumentException("Choose Responsive, Balanced or Low CPU before starting camera tracking.");

        var arguments = new List<string> { "-TrackingSource", selection.TrackingSource,
            "-TrackingProfile", selection.TrackingProfile };
        if (lowerFace)
        {
            arguments.AddRange(["-TonguePreview", "-MaxFps", selection.MaxFps,
                "-TongueSmoothing", selection.Smoothing.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-TongueVisibilityMode", selection.VisibilityMode,
                "-TongueModelPath", selection.GateModelPath!,
                "-TongueDirectionModelPath", selection.DirectionModelPath!]);
            if (selection.TongueEnabled) arguments.Add("-EnableTongueOutput");
            if (selection.CameraCheeksEnabled) arguments.Add("-EnableCheekOutput");
        }
        if (selection.PupilEnabled)
        {
            arguments.AddRange(["-PupilOutput", "-PupilSensitivity", selection.PupilSensitivity]);
            if (!lowerFace) arguments.AddRange(["-MaxFps", selection.MaxFps]);
        }
        if (!selection.ShowPreview) arguments.Add("-NoWindow");
        arguments.AddRange(["-StopFile", selection.StopFile]);
        string description = $"Tongue output {(selection.TongueEnabled ? "ON" : "OFF")}; " +
            $"camera cheek puff {(selection.CameraCheeksEnabled ? "ON" : "OFF")}; " +
            $"pupil output {(selection.PupilEnabled ? "ON" : "OFF")}. " +
            (selection.CameraCheeksEnabled
                ? "Camera cheeks use the selected direction checkpoint; native-source cheeks are the fallback when camera output stops."
                : "Cheeks use the installed module's native-source adjustments.");
        return new(arguments.ToArray(), description);
    }

    internal static string DescribeCheekSource(bool modelHasCameraCheeks, bool enabled, bool sessionRunning)
    {
        if (enabled && !modelHasCameraCheeks)
            return "Camera cheek puff unavailable: this model has tongue outputs only. Select a tongue + cheeks model, or turn Camera cheek puff off.";
        if (enabled)
            return sessionRunning
                ? "Camera cheek output requested for this session. If camera output stops, the module returns to native-source cheeks."
                : "Camera cheek puff ON for the next start. The selected model supplies separate camera cheek strengths.";
        return modelHasCameraCheeks
            ? "Camera cheeks available but OFF. Enable Camera cheek puff to use them; selecting this model alone keeps native-source cheeks."
            : "Native-source cheeks. Select a tongue + cheeks model to make Camera cheek puff available.";
    }
}
