using System.Reflection;
using System.Text.Json;
using QproFaceTracking.Hub;

try
{
int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
if (args.Length != 1 || !File.Exists(args[0]) || AssemblyName.GetAssemblyName(args[0]).Name != "Qpro.GazeBridge")
    throw new Exception("Pass a packaged Qpro module DLL; the fixture only copies and inspects it.");
string fixture = Path.Combine(Path.GetTempPath(), "qpro-component-tests-" + Guid.NewGuid().ToString("N"));
string app = Path.Combine(fixture, "app");
string local = Path.Combine(fixture, "local");
string modules = Path.Combine(fixture, "modules");
string shared = Path.Combine(local, "QproFaceTracking", "runtime");
string registration = Path.Combine(local, "QproFaceTracking", "r", "rocm-runtimes.json");
string manifest = Path.Combine(app, "release-manifest.json");
string a = new('a', 64), b = new('b', 64), c = new('c', 64);
void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
void Json(string path, object value) => Write(path, JsonSerializer.Serialize(value));
void Recipes() => Json(manifest, new { componentUpdates = new { schema = 1, runtimeRecipe = a, rocmRecipe = b, legacyRocmRecipe = c } });
HubComponentPlan Plan(bool python = false, bool rocm = false, bool steam = false) => HubComponentUpdates.Inspect(app, local, modules, steam, python, rocm);
Recipes();
Check(Plan().Updates.Count == 0, "Fresh install must not add optional components.");
string sharedPython = Path.Combine(shared, ".venv", "Scripts", "python.exe");
Write(sharedPython, "fixture; never execute");
Check(Plan().Updates.Single().Kind == HubComponentKind.Runtime, "Existing runtime without receipt needs verification.");
var runtimeUpdate = Plan().Updates.Single();
Check(!HubComponentUpdates.RuntimeUpdateVerified(runtimeUpdate, local), "Missing readiness receipt was accepted.");
Json(Path.Combine(shared, "runtime-ready.json"), new { format = "qpro-runtime-ready-v1", python = sharedPython, recipeSha256 = a });
Check(Plan().Updates.Count == 0 && HubComponentUpdates.RuntimeUpdateVerified(runtimeUpdate, local), "Matching runtime receipt was not recognized.");
foreach (string invalid in new[] {
    "{}", "{broken", "{\"schema\":\"1\"}",
    JsonSerializer.Serialize(new { format = "qpro-runtime-ready-v1", python = sharedPython, recipeSha256 = b }),
    JsonSerializer.Serialize(new { format = "qpro-runtime-ready-v1", python = Path.Combine(fixture, "global", "python.exe"), recipeSha256 = a }),
    "{\"format\":\"qpro-runtime-ready-v1\",\"format\":\"foreign\"}", new string(' ', 65537)
})
{
    Write(Path.Combine(shared, "runtime-ready.json"), invalid);
    Check(!HubComponentUpdates.RuntimeUpdateVerified(runtimeUpdate, local), "Invalid runtime receipt was accepted.");
}
Check(Plan(python: true).Updates.Count == 0 && Plan(python: true).Notes.Count > 0, "Explicit custom Python must not be modified.");
Json(Path.Combine(shared, "runtime-ready.json"), new { format = "qpro-runtime-ready-v1", python = sharedPython, recipeSha256 = a });

string Rocm(string folder, string recipe)
{
    string path = Path.Combine(fixture, "custom-AMD", folder);
    string python = Path.Combine(path, "Scripts", "python.exe");
    Write(python, "fixture; never execute");
    Json(Path.Combine(path, "qpro-rocm-environment.json"), new { format = "qpro-rocm-environment-v1", environment = path });
    Json(Path.Combine(path, "qpro-rocm-ready.json"), new { schema = 1, python, recipeSha256 = recipe });
    return path;
}
string latest = Rocm("10-gfx1100", a), legacy = Rocm("721-gfx1100", a);
Json(registration, new { schema = 1, latestEnvironment = latest, legacyEnvironment = legacy });
var gpu = Plan().Updates;
Check(gpu.Count == 2 && gpu[0].Kind == HubComponentKind.Rocm && gpu[1].Kind == HubComponentKind.LegacyRocm, "Both installed GPU tracks should preserve their separate update recipes.");
Check(Plan(rocm: true).Updates.Count == 0, "Custom ROCm override must not redirect component maintenance.");
Check(!HubComponentUpdates.RuntimeUpdateVerified(gpu[0], local), "Old GPU receipt accepted before verification.");
string replacement = Rocm("10-gfx1100-12345678", b);
Json(registration, new { schema = 1, latestEnvironment = replacement, legacyEnvironment = legacy });
Check(HubComponentUpdates.RuntimeUpdateVerified(gpu[0], local), "Verified registered replacement was not accepted.");
Check(Directory.Exists(latest), "Existing GPU environment must be preserved by inspection.");
Check(Plan().Updates.Single().Kind == HubComponentKind.LegacyRocm, "Updating current ROCm must not mark legacy ROCm current.");
File.Delete(registration);
Check(!HubComponentUpdates.RuntimeUpdateVerified(gpu[0], local), "Lost registration must not report success.");
Json(registration, new { schema = 1, latestEnvironment = replacement });
Write(Path.Combine(replacement, "qpro-rocm-environment.json"), "{}");
Check(!HubComponentUpdates.RuntimeUpdateVerified(gpu[0], local), "Foreign environment must not report success.");
Check(Plan().Updates.Count == 0 && Plan().Notes.Count > 0, "Foreign environment should be blocked, not repaired automatically.");
Write(registration, "{\"schema\":\"1\"}");
Check(Plan().Updates.Count == 0 && Plan().Notes.Count > 0, "Invalid registration must report inspection guidance.");
Json(registration, new { schema = 1, latestEnvironment = 10 });
Check(Plan().Updates.Count == 0 && Plan().Notes.Count > 0, "Invalid registered path must not appear fully up to date.");

foreach (var badManifest in new[] { "{}", "{broken", "{\"componentUpdates\":{\"schema\":\"1\"}}", "{\"componentUpdates\":{\"schema\":1,\"runtimeRecipe\":\"x\"}}", new string(' ', 65537) })
{
    Write(manifest, badManifest);
    Check(HubComponentUpdates.ReadRecipes(app) is null, "Invalid component recipe must not launch an update.");
}
Recipes();
string supplied = Path.Combine(app, "vrcft-gaze-bridge", "bin", "Release", "net10.0", "Qpro.GazeBridge.dll");
Directory.CreateDirectory(Path.GetDirectoryName(supplied)!); File.Copy(args[0], supplied);
Directory.CreateDirectory(modules);
string vd = Path.Combine(modules, "000-Qpro.VirtualDesktop.dll"); File.Copy(supplied, vd);
Check(HubModuleInstallation.TryGetInstalledSource(modules, true, out bool steamLink) && !steamLink, "Installed VD source must be preserved even if the combo currently selects Steam Link.");
Check(Plan(steam: true).Updates.Single().Kind == HubComponentKind.Module && !Plan(steam: true).Updates.Single().SteamLink, "Module update chose a different streaming app.");
string sl = Path.Combine(modules, "000-Qpro.SteamLink.dll"); File.Copy(supplied, sl);
Check(!HubModuleInstallation.TryGetInstalledSource(modules, false, out _), "Duplicate source modules must not be guessed.");
Check(Plan().Updates.Count == 0 && Plan().Notes.Count > 0, "Ambiguous module ownership must go to manual repair.");
File.Delete(vd);
Check(HubModuleInstallation.TryGetInstalledSource(modules, false, out steamLink) && steamLink, "Installed Steam Link source was not preserved.");
Console.WriteLine($"PASS: {assertions} component planning and receipt checks; no installations or package downloads. Fixture: {fixture}");
return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
