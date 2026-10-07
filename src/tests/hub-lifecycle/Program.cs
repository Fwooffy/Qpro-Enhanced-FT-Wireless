using System.Reflection;
using QproFaceTracking.Hub;

internal static class LifecycleTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // Preview construction avoids ADB, runtime detection, timers and preference writes.
        using var form = new HubForm(Path.Combine(Path.GetTempPath(), "qpro-lifecycle-unused"), rememberLaunch: false, previewOnly: true);
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
            foreach (var name in new[] { "_setupRuntimeButton", "_setupGazeButton", "_connectWirelessButton", "_pairWirelessButton" })
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
    }
}
