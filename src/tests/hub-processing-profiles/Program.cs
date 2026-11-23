using System.Diagnostics;
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
                ",\"ProcessingProfile\":\"responsive\"", "", StringComparison.Ordinal).Replace(
                "\"CameraCheeks\":false", "\"CameraCheeks\":true,\"NativeFace\":false", StringComparison.Ordinal);
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            T Field<T>(string name) => (T)typeof(HubForm).GetField(name, privateInstance)!.GetValue(form)!;
            VrchatFaceSelection FaceSelection() => (VrchatFaceSelection)typeof(HubForm)
                .GetMethod("FaceSelection", privateInstance)!.Invoke(form, null)!;
            form.ApplyLiveOptions(JsonSerializer.Deserialize<HubLiveOptions>(legacyJson)!);
            var restoredLegacy = form.CaptureLiveOptions();
            Check(restoredLegacy.ProcessingProfile == "responsive", "legacy saved choices use Responsive");
            Check(typeof(HubForm).GetField("_nativeFace", privateInstance) is null &&
                !JsonSerializer.Serialize(restoredLegacy).Contains("NativeFace") && !restoredLegacy.CameraCheeks,
                "legacy native opt-out is discarded and retired camera-cheek output stays off");
            form.ApplyLiveOptions(original with { ProcessingProfile = "future-mode" });
            Check(form.CaptureLiveOptions().ProcessingProfile == "responsive", "unknown saved choices preserve default cadence");
            form.ApplyLiveOptions(original with { Pupil = false, ProcessingProfile = "balanced" });
            Check(!field.Enabled && form.CaptureLiveOptions().ProcessingProfile == "balanced", "disabled pupil setting retains next-session choice");

            form.ApplyLiveOptions(original);
            var adjustments = new[] { "_eyebrowBoost", "_individualCheekPuff", "_individualCheekSuck" }
                .Select(Field<CheckBox>).ToArray();
            foreach (var adjustment in adjustments) adjustment.Checked = false;
            Check(!FaceSelection().HasAdjustments && !Field<Button>("_start").Enabled,
                "native face and eyes need no optional tracking action");
            form.ApplyLiveOptions(original with { Hands = true });
            Check(!FaceSelection().HasAdjustments && Field<Button>("_start").Enabled,
                "optional controllers enable Start tracking without face adjustments");
            form.ApplyLiveOptions(original with { Pupil = true });
            Check(FaceSelection() is { HasAdjustments: true, Pupils: true } && Field<Button>("_start").Enabled,
                "optional camera output augments native output and enables Start tracking");
            var starting = typeof(HubForm).GetField("_starting", privateInstance)!;
            var updateControls = typeof(HubForm).GetMethod("UpdateControlState", privateInstance)!;
            starting.SetValue(form, true);
            updateControls.Invoke(form, null);
            Check(adjustments.All(adjustment => !adjustment.Enabled), "face adjustments stay fixed during startup handoff");
            starting.SetValue(form, false);
            updateControls.Invoke(form, null);
            Check(adjustments.All(adjustment => adjustment.Enabled), "face adjustments return to editable after handoff");
            using var fixtureProcess = Process.GetCurrentProcess();
            var workers = Field<List<Process>>("_trackingProcesses");
            workers.Add(fixtureProcess);
            try
            {
                updateControls.Invoke(form, null);
                Check(adjustments.All(adjustment => !adjustment.Enabled) && !Field<ComboBox>("_eyebrowSensitivity").Enabled,
                    "a controller-only session locks face adjustments until the next Start");
            }
            finally { workers.Remove(fixtureProcess); }
            updateControls.Invoke(form, null);
            Check(adjustments.All(adjustment => adjustment.Enabled), "face adjustments unlock after controllers stop");
            Console.WriteLine("PASS processing profiles and face selection: defaults, persistence, legacy settings and unchanged tongue settings. No window or tracking started.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
