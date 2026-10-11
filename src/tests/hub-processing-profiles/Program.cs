using System.Reflection;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static class ProcessingProfileTests
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var form = new HubForm(Path.Combine(Path.GetTempPath(), "qpro-profile-fixture"),
                rememberLaunch: false, previewOnly: true);
            var field = (ComboBox)typeof(HubForm).GetField("_processingProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var original = form.CaptureLiveOptions();
            Check(original.ProcessingProfile == "responsive", "new setup preserves existing cadence");
            Check(!field.Enabled, "profile is disabled without pupil processing");
            foreach (var profile in HubProcessingProfile.Choices)
            {
                form.ApplyLiveOptions(original with { Pupil = true, ProcessingProfile = profile.Id });
                Check(field.Enabled, "profile becomes available with pupils enabled");
                Check(form.CaptureLiveOptions().ProcessingProfile == profile.Id, "stable profile ID round-trips through UI state");
                var serialized = JsonSerializer.Serialize(form.CaptureLiveOptions());
                form.ApplyLiveOptions(original);
                form.ApplyLiveOptions(JsonSerializer.Deserialize<HubLiveOptions>(serialized)!);
                Check(form.CaptureLiveOptions().ProcessingProfile == profile.Id, "saved choice restores after reopen/update");
                Check(form.CaptureLiveOptions().Fps == original.Fps && form.CaptureLiveOptions().Smoothing == original.Smoothing,
                    "processing profile cannot change camera FPS or tongue smoothing");
            }
            var legacyJson = JsonSerializer.Serialize(original).Replace(
                ",\"ProcessingProfile\":\"responsive\"", "", StringComparison.Ordinal);
            form.ApplyLiveOptions(JsonSerializer.Deserialize<HubLiveOptions>(legacyJson)!);
            Check(form.CaptureLiveOptions().ProcessingProfile == "responsive", "legacy saved choices use Responsive");
            form.ApplyLiveOptions(original with { ProcessingProfile = "future-mode" });
            Check(form.CaptureLiveOptions().ProcessingProfile == "responsive", "unknown saved choices preserve default cadence");
            form.ApplyLiveOptions(original with { Pupil = false, ProcessingProfile = "balanced" });
            Check(!field.Enabled && form.CaptureLiveOptions().ProcessingProfile == "balanced", "disabled pupil setting retains next-session choice");
            Console.WriteLine("PASS processing profiles: defaults, persistence, legacy choices and unchanged tongue settings. No window or tracking started.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
