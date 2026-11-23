using System.Diagnostics;
using System.Reflection;
using Qpro.Shared;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Expressions;

internal static class FaceOutputOwnershipFixtures
{
    internal static bool RunChild(string[] args)
    {
        if (args is not ["--hold-module-owner", var directory]) return false;
        var owner = FaceOutputOwnership.TryAcquireModule(directory);
        var extras = FaceOutputOwnership.TryAcquireExtras(directory);
        Console.WriteLine(owner is null || extras is null ? "unavailable" : "ready");
        Console.ReadLine();
        GC.KeepAlive(extras);
        GC.KeepAlive(owner); // Exit with the handle still open, as after a crashed host.
        Environment.Exit(0);
        return true;
    }

    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("qpro-output-owner-");
        try
        {
            string path = directory.FullName;
            string preference = Path.Combine(path, "face-output-route.txt");
            check(FaceOutputOwnership.ReadRoute(path) == FaceOutputRoute.Unconfigured, "absent route retains standalone support");
            await CheckOtherProcess(path, check);
            CheckExpressions(path, check, backup: false);
            using var fixture = new ModuleFixture(false);
            var module = fixture.Module;
            int opens = 0;
            (bool, bool) Open(bool eye, bool face) { opens++; return (eye, face); }

            FaceOutputOwnership.SaveRoute(FaceOutputRoute.Direct, path);
            check(module.InitializeOutput(true, true, path, Open) == (false, false) && opens == 0,
                "explicit direct refuses both VRCFT slots before opening inputs");
            UnifiedTracking.Data.Eye.Left.Gaze.x = .125f;
            fixture.Native();
            module.Update();
            check(UnifiedTracking.Data.Eye.Left.Gaze.x == .125f, "a refused module cannot write native slots on a late Update");
            using (var direct = FaceOutputOwnership.TryAcquireDirect(path))
            {
                check(direct is not null && Denied(FaceOutputOwnership.TryAcquireTransition(path)), "direct blocks a competing transition");
                check(module.InitializeOutput(true, true, path, Open) == (false, false) && opens == 0,
                    "loaded direct owner prevents module startup");
            }
            File.WriteAllText(preference, "unknown");
            check(FaceOutputOwnership.ReadRoute(path) == FaceOutputRoute.Unavailable &&
                Denied(FaceOutputOwnership.TryAcquireDirect(path)) && Denied(FaceOutputOwnership.TryAcquireModule(path)),
                "an invalid route cannot silently enable either sender");
            using (var busyPreference = File.Open(preference, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(FaceOutputOwnership.ReadRoute(path) == FaceOutputRoute.Unavailable && Denied(FaceOutputOwnership.TryAcquireModule(path)),
                    "an unreadable choice does not enable standalone mode");
            FaceOutputOwnership.SaveRoute(FaceOutputRoute.Vrcft, path);
            CheckExpressions(path, check, backup: true);
            using (var transition = FaceOutputOwnership.TryAcquireTransition(path))
            using (var reader = File.Open(preference, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                check(transition is not null, "selection starts with an exclusive transition owner");
                bool failed = false;
                try { FaceOutputOwnership.SaveRoute(FaceOutputRoute.Direct, path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
                check(failed && FaceOutputOwnership.ReadRoute(path) == FaceOutputRoute.Vrcft &&
                    !Directory.EnumerateFiles(path, "*.tmp").Any(), "failed replacement preserves the prior route and removes its temporary file");
            }
            check(module.InitializeOutput(false, false, path, Open) == (false, false) && opens == 0,
                "slotless startup does not bind inputs or retain ownership");
            check(module.InitializeOutput(false, true, path, Open) == (false, true) && opens == 1, "backup accepts the requested slot");
            using (var extras = FaceOutputOwnership.TryAcquireExtras(path))
                check(extras is not null, "a separate Companion can supply camera data while VRCFT owns final output");
            check(module.InitializeOutput(true, true, path, Open) == (false, true) && opens == 1, "repeat initialization keeps actual ownership");
            check(Denied(FaceOutputOwnership.TryAcquireTransition(path)), "module retains exclusive ownership between updates");
            module.Teardown();
            module.Teardown();
            using (var change = FaceOutputOwnership.TryAcquireTransition(path))
                check(change is not null, "repeated teardown releases the module without leaving an owner");
            check(module.InitializeOutput(true, true, path, (_, _) => (false, false)) == (false, false),
                "input-open failure returns no slots");
            using (var change = FaceOutputOwnership.TryAcquireTransition(path))
                check(change is not null, "rejected input startup releases the lease before returning");
            try { module.InitializeOutput(true, true, path, (_, _) => throw new IOException("unavailable input")); }
            catch (IOException) { }
            using (var change = FaceOutputOwnership.TryAcquireTransition(path))
                check(change is not null, "exceptional input startup also releases the lease");
            check(File.Exists(Path.Combine(path, "face-output-owner.lock")) && !Directory.EnumerateFiles(path, "*.tmp").Any(),
                "atomic selection and release retain one stable lock file");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task CheckOtherProcess(string directory, Action<bool, string> check)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--hold-module-owner");
        start.ArgumentList.Add(directory);
        using var child = Process.Start(start)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            check(ready == "ready" && Denied(FaceOutputOwnership.TryAcquireDirect(directory)) &&
                Denied(FaceOutputOwnership.TryAcquireModule(directory)), "standalone module excludes Hub and module contenders across processes");
            check(Denied(FaceOutputOwnership.TryAcquireExtras(directory)), "another process cannot launch a second set of eye and face producers");
        }
        finally
        {
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        var deadline = Stopwatch.StartNew();
        IDisposable? acquired;
        while ((acquired = FaceOutputOwnership.TryAcquireDirect(directory)) is null && deadline.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(20);
        using (acquired)
            check(acquired is not null, "an unexpected owner exit frees direct output without deleting the lock file");
        using var extras = FaceOutputOwnership.TryAcquireExtras(directory);
        check(extras is not null, "an exited producer also releases its camera ownership");
    }

    private static void CheckExpressions(string directory, Action<bool, string> check, bool backup)
    {
        using var fixture = new ModuleFixture(false);
        var module = fixture.Module;
        check(module.InitializeOutput(true, true, directory, (eye, face) => (eye, face)) == (true, true),
            "standalone and explicit backup can each open their configured output");
        fixture.Set("_eyebrowSettings", new EyebrowSettings(true, 2f));
        fixture.Native(browLeft: .25f, smileLeft: .12f, smileRight: .03f);
        static float Shape(UnifiedExpressions expression) => UnifiedTracking.Data.Shapes[(int)expression].Weight;
        foreach (bool active in new[] { false, true, false })
        {
            fixture.Set("_cheekSessionActive", active);
            fixture.Time += 250;
            module.Update();
            bool adjusted = !backup || active;
            check(Shape(UnifiedExpressions.BrowLowererLeft) == (adjusted ? .5f : .25f),
                "backup applies saved brow sensitivity only while optional tracking is active; standalone retains its setting");
            check(adjusted ? Shape(UnifiedExpressions.MouthCornerPullLeft) > .12f && Shape(UnifiedExpressions.MouthCornerPullRight) == 0f
                : Shape(UnifiedExpressions.MouthCornerPullLeft) == .12f && Shape(UnifiedExpressions.MouthCornerPullRight) == .03f &&
                  Shape(UnifiedExpressions.MouthCornerSlantLeft) == .12f && Shape(UnifiedExpressions.MouthCornerSlantRight) == .03f,
                "backup preserves both native smile corners while idle and retains enhanced smirk during optional tracking");
        }
    }

    private static bool Denied(IDisposable? lease)
    {
        using (lease) return lease is null;
    }
}
