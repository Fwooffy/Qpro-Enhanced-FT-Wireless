using System.Reflection;
using QproFaceTracking.Hub;

internal static class DirectStatusFixtures
{
    internal static void Run(HubForm form)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Call(string name, params object[] arguments) =>
            typeof(HubForm).GetMethod(name, flags)!.Invoke(form, arguments);
        FieldInfo Field(string name) => typeof(HubForm).GetField(name, flags)!;
        var output = (Label)Field("_directVrchatStatus").GetValue(form)!;
        var summary = (Label)Field("_runStatus").GetValue(form)!;
        var first = new TaskCompletionSource<VrchatOscTarget?>();
        var second = new TaskCompletionSource<VrchatOscTarget?>();
        Func<CancellationToken, Task<VrchatOscTarget?>> firstCheck = _ => first.Task;
        Func<CancellationToken, Task<VrchatOscTarget?>> secondCheck = _ => second.Task;
        output.AccessibleDescription = "Previous result";
        var oldCheck = (Task)Call("CheckVrchatConnectionAsync", firstCheck)!;
        if (output.AccessibleDescription == "Previous result") throw new Exception("Checking retained a stale accessible result.");
        Pump((Task)Call("StopDirectTrackingAsync")!);
        var newCheck = (Task)Call("CheckVrchatConnectionAsync", secondCheck)!;
        first.SetCanceled();
        Pump(oldCheck);
        if (!output.Text.Contains("Looking for VRChat") || Field("_vrchatCheck").GetValue(form) is null)
            throw new Exception("A cancelled check overwrote a newer check.");
        second.SetResult(null);
        Pump(newCheck);
        if (!output.Text.Contains("VRChat not found")) throw new Exception("Current discovery result was lost.");

        foreach (var field in new[] { "_gaze", "_tongue", "_pupil", "_hybridHands", "_controllerTouchpad" })
            ((CheckBox)Field(field).GetValue(form)!).Checked = false;
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var session = new VrchatFaceSession(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Fixture did not release the source.");
            return new Dictionary<string, float>();
        }, new MemoryStream(), _ => { },
            _ => Task.FromResult<VrchatOscTarget?>(null), (_, _) => Task.FromResult<VrchatOscTarget?>(null), TimeSpan.FromSeconds(1));
        Field("_directSession").SetValue(form, session);
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new Exception("Fixture source did not start.");
            Call("ShowDirectSessionStatus", session, new VrchatFaceStatus("Sending face tracking", true));
            if (!summary.Text.Contains("Sending face data")) throw new Exception("Direct summary lost active output.");
            Call("ShowDirectSessionStatus", session, new VrchatFaceStatus("Waiting for data", false));
            if (!summary.Text.Contains("Waiting for VRChat face output")) throw new Exception("Direct summary retained stale success.");
            var stop = (Task<bool>)Call("TryStopTrackingAsync")!;
            var concurrent = (Task<bool>)Call("TryStopTrackingAsync")!;
            if (!ReferenceEquals(stop, concurrent) || concurrent.IsCompleted)
                throw new Exception("The second Hub Stop returned before the receiver had closed.");
            release.Set();
            Pump(stop);
            if (!stop.Result) throw new Exception("Direct session cleanup was unconfirmed.");
            Call("ShowDirectSessionStatus", session, new VrchatFaceStatus("Late ready callback", true));
            if (!output.Text.Contains("VRChat output off") || summary.Text.Contains("Sending face data"))
                throw new Exception("An old session replaced the idle state.");
        }
        finally { release.Set(); Pump(session.StopAsync()); }
        Console.WriteLine("PASS overlapping Hub Stops, current direct output summaries and superseded discovery/session callbacks.");
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(3); }
        if (!task.IsCompleted) throw new TimeoutException("Hub callback did not finish.");
        task.GetAwaiter().GetResult();
    }
}
