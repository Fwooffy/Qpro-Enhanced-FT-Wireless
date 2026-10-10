using System.Reflection;
using QproFaceTracking.Hub;

internal static class UpdaterLifetimeTests
{
    [STAThread]
    static void Main()
    {
        try { Run(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }

    static void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var closeNormally in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "qpro-update-lifetime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
            var source = (CancellationTokenSource)typeof(HubForm).GetField("_updateLifetime", flags)!.GetValue(form)!;
            var token = (CancellationToken)typeof(HubForm).GetField("_updateToken", flags)!.GetValue(form)!;
            Check(token.CanBeCanceled && !token.IsCancellationRequested, "Updater token did not initialize.");
            var callbacks = 0;
            using var registration = token.Register(() => callbacks++);
            var pending = Task.Delay(30_000, token);
            if (closeNormally) { form.Show(); Application.DoEvents(); form.Close(); }
            else form.Dispose();
            Check(token.IsCancellationRequested && callbacks == 1, "Closing or disposing did not cancel pending updates once.");
            Check(pending.IsCanceled, "A pending updater delay survived disposal.");
            Check((CancellationToken)typeof(HubForm).GetField("_updateToken", flags)!.GetValue(form)! == token,
                "The cached updater token changed after disposal.");
            try { _ = source.Token; throw new Exception("Updater cancellation source was not disposed."); }
            catch (ObjectDisposedException) { passed++; }
            try { Task.Delay(1, token).GetAwaiter().GetResult(); throw new Exception("A late updater continuation ignored cancellation."); }
            catch (OperationCanceledException) { passed++; }
            using var late = CancellationTokenSource.CreateLinkedTokenSource(token);
            Check(late.IsCancellationRequested, "A late HTTP timeout token was not safely canceled.");
            form.Dispose();
            Check(callbacks == 1, "Repeated disposal repeated cancellation callbacks.");
        }
        Console.WriteLine($"PASS: {passed} updater lifetime checks. Preview forms only; no network, installs, or headset actions.");
    }
}
