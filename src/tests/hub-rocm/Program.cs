using System.Text.Json;
using QproFaceTracking.Hub;

// Isolated paths exercise upgrades and failed installs without starting Python
// or reading the user's runtime, graphics driver, or Windows registry.
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var root = Path.Combine(Path.GetTempPath(), "qpro-rocm-routing-" + Guid.NewGuid().ToString("N"));
var release = Path.Combine(root, "extracted release");
var local = Path.Combine(root, "local");
var storage = Path.Combine(local, "QproFaceTracking", "r");
var latest = Path.Combine(storage, "10-gfx1100");
var legacy = Path.Combine(release, ".venv-rocm");
var custom = Path.Combine(root, "custom");
var index = Path.Combine(storage, "rocm-runtimes.json");
Check(!HubRocmOsPolicy.CanInstall(19045, false), "Windows 10 was admitted without explicit opt-in.");
Check(HubRocmOsPolicy.CanInstall(19045, true), "Windows 10 22H2 opt-in was refused.");
Check(!HubRocmOsPolicy.CanInstall(19044, true), "Older Windows 10 was admitted by opt-in.");
Check(!HubRocmOsPolicy.CanInstall(19045, true, legacy: true), "Windows 10 opt-in admitted legacy ROCm.");
Check(!HubRocmOsPolicy.CanInstall(-1, true), "An invalid Windows build was admitted.");
foreach (var build in new[] { 22000, 22621, 26100, 26200 })
{
    Check(HubRocmOsPolicy.CanInstall(build, false), "Windows 11 installation was changed.");
    Check(HubRocmOsPolicy.CanInstall(build, true, legacy: true), "Windows 11 legacy installation was changed.");
    Check(HubRocmOsPolicy.InstallArguments(build, true).Length == 0, "Windows 11 received the Windows 10 opt-in flag.");
}
Check(HubRocmOsPolicy.IsWindows10OptInEligible(19045) && !HubRocmOsPolicy.IsWindows10OptInEligible(22000), "Opt-in checkbox OS range is wrong.");
Check(HubRocmOsPolicy.InstallArguments(19045, true).SequenceEqual(new[] { "-AllowExperimentalWindows10" }), "Hub did not forward the CLI opt-in.");
Check(HubRocmOsPolicy.InstallArguments(19045, false).Length == 0, "Hub forwarded an unchecked opt-in.");
Check(HubRocmOsPolicy.BlockedReason(19045, false)?.Contains("Enable Try ROCm") == true, "Blocked Windows 10 did not have a next step.");
Check(HubRocmOsPolicy.BlockedReason(19045, true) is null, "Opted-in Windows 10 was still blocked.");
void WriteReady(string environment, bool old, string target = "gfx1100", string version = "10.0.0")
{
    Directory.CreateDirectory(Path.Combine(environment, "Scripts"));
    File.WriteAllText(Path.Combine(environment, "Scripts", "python.exe"), "fixture");
    File.WriteAllText(Path.Combine(environment, "qpro-rocm-ready.json"), JsonSerializer.Serialize(new
    {
        schema = 1, gfxTarget = target,
        supportTier = old ? "amd-windows-7.2.1" : "experimental-rocm-10",
        rocmVersion = old ? "7.2.1" : version
    }));
}
try
{
    WriteReady(latest, false);
    WriteReady(legacy, true);
    WriteReady(custom, false);
    Directory.CreateDirectory(Path.Combine(storage, "unrelated"));
    File.WriteAllText(index, JsonSerializer.Serialize(new { schema = 1, latestEnvironment = custom }));
    var candidates = HubRocmRuntime.Candidates(release, false, local);
    Check(candidates[0] == custom && candidates.Contains(latest), "A verified custom short install was not preferred.");
    Check(candidates[^1] == Path.Combine(release, ".venv-rocm-experimental"), "An older extracted environment was lost.");
    Check(!candidates.Any(path => path.EndsWith("unrelated")), "Unrelated folders entered runtime discovery.");
    Check(HubRocmRuntime.IsReady(custom, false, "gfx1100"), "Verified latest runtime was not ready.");
    Check(HubRocmRuntime.ReadyVersion(custom, false, "gfx1100") == "10.0.0", "Existing ROCm 10.0 was reported as the update.");
    WriteReady(custom, false, version: "10.1.0");
    Check(HubRocmRuntime.ReadyVersion(custom, false, "gfx1100") == "10.1.0", "ROCm 10.1 readiness was not recognized.");
    WriteReady(custom, false, version: "10.2.0");
    Check(!HubRocmRuntime.IsReady(custom, false, "gfx1100"), "An unreviewed future ROCm version was accepted.");
    WriteReady(custom, false, version: "10.1.0");
    var previousCustom = Path.Combine(root, "prior custom runtime");
    WriteReady(previousCustom, false);
    File.WriteAllText(index, JsonSerializer.Serialize(new { schema = 1, latestEnvironment = custom, latestFallbackEnvironments = new[] { previousCustom } }));
    Check(HubRocmRuntime.Candidates(release, false, local).Take(2).SequenceEqual(new[] { custom, previousCustom }), "A previous custom runtime was not retained as a fallback.");
    Check(!HubRocmRuntime.IsReady(custom, false, "gfx1031"), "A runtime for another GPU was accepted.");
    Check(!HubRocmRuntime.IsReady(custom, true, "gfx1100"), "Latest runtime was mislabeled as a legacy fallback.");
    Check(HubRocmRuntime.Candidates(release, true, local).Contains(legacy) &&
        HubRocmRuntime.IsReady(legacy, true, "gfx1100"), "Old release-local compatibility fallback was lost.");
    Check(!HubRocmRuntime.IsReady(legacy, true, "gfx1201"), "Legacy readiness ignored the detected GPU target.");
    File.WriteAllText(Path.Combine(legacy, "qpro-rocm-ready.json"), JsonSerializer.Serialize(new
    {
        schema = 1, supportTier = "amd-windows-7.2.1", rocmVersion = "7.2.1", gfxTarget = (string?)null
    }));
    Check(HubRocmRuntime.IsReady(legacy, true, "gfx1100"), "Old verified legacy marker without a GPU target was lost.");
    File.WriteAllText(Path.Combine(custom, "qpro-rocm-ready.json"), JsonSerializer.Serialize(new
    {
        schema = 1, supportTier = "experimental-rocm-10", rocmVersion = "10.0.0", gfxTarget = (string?)null
    }));
    Check(!HubRocmRuntime.IsReady(custom, false, "gfx1100"), "Latest runtime accepted a missing GPU target.");

    File.WriteAllText(index, "incomplete index");
    Check(HubRocmRuntime.Candidates(release, false, local).Contains(latest), "Damaged index hid a usable compact runtime.");
    File.WriteAllText(index, JsonSerializer.Serialize(new { schema = 1, latestEnvironment = "relative" }));
    Check(!HubRocmRuntime.Candidates(release, false, local).Contains("relative"), "Relative index path was accepted.");
    File.Delete(Path.Combine(latest, "qpro-rocm-ready.json"));
    Check(!HubRocmRuntime.IsReady(latest, false, "gfx1100"), "Partial install was marked ready.");
    File.WriteAllText(Path.Combine(latest, "qpro-rocm-ready.json"), "incomplete marker");
    Check(!HubRocmRuntime.IsReady(latest, false, "gfx1100"), "Invalid marker was marked ready.");
    WriteReady(latest, false);
    File.Delete(Path.Combine(latest, "Scripts", "python.exe"));
    Check(!HubRocmRuntime.IsReady(latest, false, "gfx1100"), "Missing interpreter was marked ready.");
    Console.WriteLine("PASS: Hub ROCm OS policy requires Windows 10 22H2 opt-in and preserves Windows 11; routing rejects wrong GPU, incomplete installs and damaged metadata.");
}
finally
{
    // Only this test's freshly created absolute directory is removed.
    Directory.Delete(root, recursive: true);
}
