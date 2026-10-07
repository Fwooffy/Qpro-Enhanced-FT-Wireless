using System.Diagnostics;
using System.Reflection;
using QproFaceTracking.Hub;

const string ModeKey = "QPRO_RUNTIME_TEST_MODE";
const string PidKey = "QPRO_RUNTIME_TEST_PID_FILE";
if (args.Length > 0 && args[0] == "--hold-pipe")
{
    await Task.Delay(TimeSpan.FromSeconds(60));
    return 0;
}
if (args.Length > 0 && args[0] == "-c")
{
    if (args.Length != 2 || !args[1].Contains("import cv2,numpy,torch")) return 9;
    var mode = Environment.GetEnvironmentVariable(ModeKey);
    if (mode == "inherited-pipe")
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--hold-pipe" } })!;
        File.WriteAllText(Environment.GetEnvironmentVariable(PidKey)!, child.Id.ToString());
    }
    Console.WriteLine("fixture output");
    Console.Error.WriteLine("fixture diagnostics");
    return mode == "failure" ? 7 : 0;
}

const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
var probe = typeof(HubEnvironment).GetMethod("ProbePythonRuntimeAsync", PrivateStatic)!;
Task<bool> Probe() => (Task<bool>)probe.Invoke(null, [Environment.ProcessPath!])!;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
}
var pidFile = Path.Combine(Path.GetTempPath(), "qpro-runtime-fixture-" + Guid.NewGuid() + ".pid");
try
{
    Environment.SetEnvironmentVariable(PidKey, pidFile);
    Environment.SetEnvironmentVariable(ModeKey, "inherited-pipe");
    var watch = Stopwatch.StartNew();
    var task = Probe();
    var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(8)));
    if (args.Contains("--reproduce-before-fix"))
        Check(completed != task, "original runtime probe remains stuck after parent exits and drain deadline passes");
    else
    {
        Check(completed == task, "runtime probe bounds inherited output pipe drain");
        Check(!await task, "incomplete output cannot verify runtime readiness");
        Check(watch.Elapsed < TimeSpan.FromSeconds(8), "runtime output failure returns within bounded deadline");
    }
    using var holder = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
    holder.Kill(entireProcessTree: true);
    await holder.WaitForExitAsync();
    await task.WaitAsync(TimeSpan.FromSeconds(3));
    if (args.Contains("--reproduce-before-fix")) return 0;
    Environment.SetEnvironmentVariable(ModeKey, "success");
    Check(await Probe(), "complete successful import verifies readiness");
    Environment.SetEnvironmentVariable(ModeKey, "failure");
    Check(!await Probe(), "nonzero import failure cannot verify readiness");
    // Exercise the actual cache without consulting or launching installed runtimes.
    const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    var environment = new HubEnvironment(Path.Combine(Path.GetTempPath(), "qpro-runtime-cache-unused"));
    var cache = typeof(HubEnvironment).GetField("_configuredRuntimeProbe", PrivateInstance)!.GetValue(environment)!;
    var cacheType = cache.GetType();
    var cachedTask = cacheType.GetField("Task", PrivateInstance)!;
    var started = cacheType.GetField("StartedUtc", PrivateInstance)!;
    var verify = typeof(HubEnvironment).GetMethod("VerifiedPythonRuntime", PrivateInstance)!;
    string? Verify(string marker = "fixture-v1") => (string?)verify.Invoke(environment, [Environment.ProcessPath!, marker, cache]);
    async Task AwaitCachedProbe() => await ((Task<bool>)cachedTask.GetValue(cache)!).WaitAsync(TimeSpan.FromSeconds(8));
    Environment.SetEnvironmentVariable(ModeKey, "success");
    Check(Verify() is null, "first cache probe does not assume readiness");
    await AwaitCachedProbe();
    Check(Verify() == Environment.ProcessPath, "completed successful probe is cached");
    Environment.SetEnvironmentVariable(ModeKey, "failure");
    started.SetValue(cache, DateTime.UtcNow.AddMinutes(-6));
    Check(Verify() == Environment.ProcessPath, "periodic recheck preserves last verified result while pending");
    await AwaitCachedProbe();
    Check(Verify() is null, "failed periodic recheck clears cached readiness");
    Environment.SetEnvironmentVariable(ModeKey, "success");
    started.SetValue(cache, DateTime.UtcNow.AddSeconds(-31));
    Check(Verify() is null, "failure retry remains unverified while pending");
    await AwaitCachedProbe();
    Check(Verify() == Environment.ProcessPath, "completed failure can retry and recover");
    Check(Verify("fixture-v2") is null, "changed readiness marker immediately invalidates cache");
    await AwaitCachedProbe();
    Check(Verify("fixture-v2") == Environment.ProcessPath, "changed marker becomes ready after fresh verification");
}
finally
{
    if (File.Exists(pidFile))
    {
        try { using var holder = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))); if (!holder.HasExited) holder.Kill(true); }
        catch (ArgumentException) { }
        File.Delete(pidFile);
    }
    Environment.SetEnvironmentVariable(ModeKey, null);
    Environment.SetEnvironmentVariable(PidKey, null);
}
return 0;
