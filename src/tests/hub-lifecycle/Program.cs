using System.Reflection;
using Qpro.Shared;
using QproFaceTracking.Hub;

internal static class LifecycleTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    static void Main()
    {
        try { Run(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Environment.ExitCode = 1;
        }
    }

    static void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // Preview construction avoids ADB, runtime detection, timers and preference writes.
        string fixtureRoot = Path.Combine(Path.GetTempPath(), "qpro-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        using var form = new HubForm(fixtureRoot, rememberLaunch: false, previewOnly: true);
        object? Call(string name, params object[] args) => typeof(HubForm).GetMethod(name, Private)!.Invoke(form, args);
        void Set(string name, bool value) => typeof(HubForm).GetField(name, Private)!.SetValue(form, value);
        bool Pending() => (bool)typeof(HubForm).GetProperty("TrackingShutdownPending", Private)!.GetValue(form)!;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        Check(!Pending(), "idle permits shutdown");
        foreach (var state in new[] { "_starting", "_stopping" })
        {
            Set(state, true);
            Check(Pending(), state + " prevents shutdown");
            Check((bool)Call("UtilityActionIsBusy", false)!, state + " rejects setup/capture entry");
            Check(!(bool)Call("TryBeginSetupProgress", "Offline regression")!, state + " cannot acquire setup");
            var utility = (Task<bool>)Call("RunUtilityAsync", "Offline regression", "nonexistent-must-not-launch.ps1", Array.Empty<string>())!;
            Check(utility.IsCompletedSuccessfully && !utility.Result, state + " rejects direct utility before launching script");
            Call("UpdateControlState");
            foreach (var name in new[] { "_setupRuntimeButton", "_setupGazeButton", "_connectWirelessButton", "_pairWirelessButton", "_reconnectUsbButton", "_forgetUsbButton" })
                Check(!((Control)typeof(HubForm).GetField(name, Private)!.GetValue(form)!).Enabled, state + " disables " + name);
            Set(state, false);
        }
        Set("_trackingCleanupPending", true);
        Check(Pending(), "exited children with undrained output still prevent shutdown");
        Call("UpdateControlState");
        Check(((Control)typeof(HubForm).GetField("_stop", Private)!.GetValue(form)!).Enabled, "pending output cleanup remains retryable");
        Set("_trackingCleanupPending", false);
        Check(!Pending(), "confirmed cleanup permits shutdown");
        foreach (var state in new[] { "_trackingCleanupPending", "_gazeRecoveryRunning", "_closingInProgress" })
        {
            Set(state, true);
            Call("UpdateControlState");
            foreach (var name in new[] { "_installHandsButton", "_removeHandsButton", "_checkHandsButton" })
                Check(!((Control)typeof(HubForm).GetField(name, Private)!.GetValue(form)!).Enabled, state + " disables " + name);
            Set(state, false);
        }

        string sessionName = @"Local\QproHubLifecycleRegression." + Guid.NewGuid().ToString("N");
        Check(!CheekTrackingSession.IsActive(sessionName), "isolated cheek session starts inactive");
        string configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "config");
        string[] preferenceNames = ["cheek-puff-mode.txt", "cheek-puff-last-style.txt", "cheek-suck-mode.txt", "cheek-suck-last-style.txt"];
        var beforePreferences = preferenceNames.ToDictionary(name => name, name => ReadPreference(Path.Combine(configRoot, name)));
        var puff = (CheckBox)typeof(HubForm).GetField("_individualCheekPuff", Private)!.GetValue(form)!;
        var suck = (CheckBox)typeof(HubForm).GetField("_individualCheekSuck", Private)!.GetValue(form)!;
        bool savedPuff = puff.Checked, savedSuck = suck.Checked;

        using var session = new CheekTrackingSession(sessionName);
        typeof(HubForm).GetField("_cheekTrackingSession", Private)!.SetValue(form, session);
        Set("_nativeCheekOnlySession", true);
        Check(Pending(), "cheek-only session counts as live tracking without child processes");
        Call("UpdateControlState");
        Check(((Control)typeof(HubForm).GetField("_stop", Private)!.GetValue(form)!).Enabled, "cheek-only session enables Stop");
        foreach (string field in new[] { "_start", "_connectionMode", "_trackingSourceSetup", "_trackingSourceLive", "_setupRuntimeButton", "_recordCameraCheeks", "_trainCameraCheeks", "_reconnectUsbButton", "_forgetUsbButton" })
            Check(!((Control)typeof(HubForm).GetField(field, Private)!.GetValue(form)!).Enabled, "cheek-only session disables " + field);

        var stopped = (Task)Call("StopTrackingAsync")!;
        Check(!CheekTrackingSession.IsActive(sessionName), "Stop releases cheek permission before asynchronous cleanup");
        Check(stopped.IsCompletedSuccessfully, "cheek-only Stop completes without a worker or headset action");
        Check(!Pending(), "cheek-only Stop clears live tracking state");
        Check(((Control)typeof(HubForm).GetField("_start", Private)!.GetValue(form)!).Enabled, "Start is enabled after cheek-only Stop");
        Check(!((Control)typeof(HubForm).GetField("_stop", Private)!.GetValue(form)!).Enabled, "Stop is disabled after cheek-only Stop");
        Check(puff.Checked == savedPuff && suck.Checked == savedSuck, "Stop retains selected cheek options");
        foreach (string name in preferenceNames)
            Check(ReadPreference(Path.Combine(configRoot, name)) == beforePreferences[name], "Stop does not rewrite " + name);

        string closingName = @"Local\QproHubLifecycleRegression." + Guid.NewGuid().ToString("N");
        using var closingSession = new CheekTrackingSession(closingName);
        typeof(HubForm).GetField("_cheekTrackingSession", Private)!.SetValue(form, closingSession);
        form.Dispose();
        Check(!CheekTrackingSession.IsActive(closingName), "disposing the Hub releases its cheek session");
        Console.WriteLine("PASS: Hub lifecycle and cheek-only session checks completed without live processes.");
    }

    static string? ReadPreference(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
}
