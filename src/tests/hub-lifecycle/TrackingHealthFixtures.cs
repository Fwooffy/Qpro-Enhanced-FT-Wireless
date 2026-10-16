using QproFaceTracking.Hub;

internal static class TrackingHealthFixtures
{
    internal static void Run()
    {
        double now = 100;
        var health = new HubTrackingHealth(() => now);
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        void Observe(string tongue, string cheek, string pupil)
            => Check(health.Observe($"TRACKING_HEALTH camera_fps=24 inference_fps=24 frame_age_ms=1 result_age_ms=1 pipeline_ms=5 dropped_frames=0 tongue={tongue} cheek={cheek} pupil={pupil} pupil_age_ms=1"), "health fixture accepts current worker report");

        health.Start(tongue: true, cheek: false, pupil: false);
        Observe("invalid", "off", "invalid");
        Check(health.LowerFaceState == TrackingSignalState.Invalid && health.LowerFaceStatusText == "Invalid output",
            "invalid tongue remains invalid in lower-face status");
        Check(health.GuidanceText.Contains("Tongue output is invalid") && !health.GuidanceText.Contains("Pupils"),
            "tongue failure does not blame disabled pupils");

        health.Start(tongue: true, cheek: true, pupil: false);
        Observe("hidden", "invalid", "off");
        Check(!health.CheekSelected && health.FeatureState("cheek") == TrackingSignalState.Off &&
            health.LowerFaceState == TrackingSignalState.Live && !health.GuidanceText.Contains("cheek"),
            "retired camera cheek selection and telemetry cannot create health warnings");
        Check(health.Observe("TRACKING_HEALTH camera_fps=24 inference_fps=24 frame_age_ms=1 result_age_ms=1 pipeline_ms=5 dropped_frames=0 tongue=hidden pupil=off") &&
            health.LowerFaceState == TrackingSignalState.Live, "current report requires no retired camera cheek field");

        health.Start(tongue: false, cheek: false, pupil: true);
        Observe("off", "off", "invalid");
        Check(health.LowerFaceState == TrackingSignalState.Off && health.GuidanceText.StartsWith("Pupils are invalid or warming"),
            "pupil failure retains pupil-specific warm-up guidance");

        health.Start(tongue: true, cheek: false, pupil: true);
        Observe("invalid", "off", "invalid");
        Check(health.GuidanceText.Contains("Tongue output is invalid") && health.GuidanceText.Contains("Pupils are invalid"),
            "concurrent failures identify both selected features");
        now += 4;
        Check(health.CameraState == TrackingSignalState.Stale && health.GuidanceText.StartsWith("Camera input is stale"),
            "stale input supersedes earlier invalid predictions");
        health.CameraExited(failed: true);
        Check(health.GuidanceText.StartsWith("Camera worker failed"), "worker failure has recovery guidance");

        health.Start(tongue: true, cheek: false, pupil: false);
        Observe("hidden", "off", "off");
        Check(health.LowerFaceState == TrackingSignalState.Live && health.GuidanceText.StartsWith("Processing delay"),
            "valid hidden tongue is live rather than invalid");
    }
}
