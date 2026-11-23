using System.Text.Json;
using QproFaceTracking.Hub;

var root = Directory.CreateTempSubdirectory("qpro-steamvr-launch-");
try
{
    var first = Executable("original copy");
    var moved = Executable("new copy & Δ");
    var settings = Path.Combine(root.FullName, "SteamVR launch");
    var manifest = Path.Combine(settings, SteamVrLaunchRegistration.ManifestName);
    using var fake = new SteamVrFixture();
    var registration = new SteamVrLaunchRegistration(settings, fake.Connect);

    Check(!registration.GetStatus(first).Enabled && !registration.IsAuthorizedLaunch(first) &&
        !registration.SetEnabled(first, false).Enabled && fake.Connections == 0 && !Directory.Exists(settings),
        "An unset preference opened SteamVR or wrote registration files.");
    fake.Running = false;
    Fails(() => registration.SetEnabled(first, true), "Open SteamVR");
    Check(!Directory.Exists(settings), "Enabling with SteamVR stopped wrote registration files.");
    fake.Running = true;
    Fails(() => registration.SetEnabled(Path.Combine(root.FullName, "dotnet.exe"), true), "extracted QproFaceTracking.exe");

    var enabled = registration.SetEnabled(first, true);
    Check(enabled is { Enabled: true, ThisCopy: true, Verified: true } &&
        fake.Current == new SteamVrApplication(first, true) && registration.IsAuthorizedLaunch(first),
        "An enabled copy was not registered, verified and authorized.");
    using (var document = JsonDocument.Parse(File.ReadAllBytes(manifest)))
    {
        var app = document.RootElement.GetProperty("applications")[0];
        Check(app.GetProperty("app_key").GetString() == SteamVrLaunchRegistration.AppKey &&
            app.GetProperty("arguments").GetString() == "--steamvr-autolaunch" &&
            app.GetProperty("launch_type").GetString() == "binary" &&
            app.GetProperty("is_dashboard_overlay").GetBoolean(), "The manifest is not an OpenVR startup overlay app.");
    }

    int calls = fake.Changes.Count;
    File.WriteAllText(first, "a new version of the same executable");
    Check(registration.SetEnabled(first, true).ThisCopy && fake.Changes.Count == calls, "A same-folder update registered a duplicate.");
    fake.Current = new(first, false);
    Check(registration.GetStatus(first) is { Enabled: false, ThisCopy: true, Verified: true } &&
        !registration.IsAuthorizedLaunch(first), "User opt-out in SteamVR did not take effect.");
    registration.SetEnabled(first, true);
    Check(fake.Changes.Count == calls + 1, "Re-enabling the same disabled entry unnecessarily registered another manifest.");

    fake.Running = false;
    Check(registration.GetStatus(first) is { Verified: false, ThisCopy: true } &&
        !registration.IsAuthorizedLaunch(first), "A stopped SteamVR runtime was reported as verified or authorized.");
    Fails(() => registration.SetEnabled(first, false), "Open SteamVR");
    Check(File.Exists(manifest), "A failed disabled operation lost the existing opt-in.");
    fake.Running = true;

    Check(registration.GetStatus(moved) is { Enabled: true, ThisCopy: false, Verified: true } &&
        !registration.IsAuthorizedLaunch(moved), "A moved copy inherited authorization without explicit enable.");
    var previous = File.ReadAllBytes(manifest);
    fake.FailNextAdd = true;
    Fails(() => registration.SetEnabled(moved, true), "registration failure");
    Check(fake.Current == new SteamVrApplication(first, true) && previous.SequenceEqual(File.ReadAllBytes(manifest)),
        "A failed move did not restore the previous manifest and auto-launch state.");
    fake.DisconnectOnNextAdd = true;
    Fails(() => registration.SetEnabled(moved, true), "restoration could not be verified");
    Check(previous.SequenceEqual(File.ReadAllBytes(manifest)) && !registration.IsAuthorizedLaunch(moved),
        "Losing SteamVR during a move left a new local preference in place.");
    fake.Running = true;
    registration.SetEnabled(first, true);
    Check(registration.SetEnabled(moved, true) is { Enabled: true, ThisCopy: true } &&
        fake.Current == new SteamVrApplication(moved, true) && !registration.IsAuthorizedLaunch(first) &&
        registration.IsAuthorizedLaunch(moved), "Rebind did not replace the old executable under the same app key.");
    Check(fake.Changes.Where(change => change.Action == "add").All(change => change.Argument == manifest),
        "The registered manifest path changed between copies.");

    fake.FailNextRemove = true;
    Fails(() => registration.SetEnabled(moved, false), "unregistration failure");
    Check(registration.IsAuthorizedLaunch(moved), "A failed disable left the prior registration unchecked.");
    using (File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Fails(() => registration.SetEnabled(moved, false));
        Check(fake.Current == new SteamVrApplication(moved, true), "A locked manifest made rollback lose the registered preference.");
    }
    Check(registration.SetEnabled(moved, false) is { Enabled: false, Verified: true } &&
        fake.Current is null && !File.Exists(manifest) && !registration.IsAuthorizedLaunch(moved),
        "Disabling did not remove the authorized startup entry.");

    fake.DisconnectOnNextAdd = true;
    Fails(() => registration.SetEnabled(first, true), "restoration could not be verified");
    Check(!File.Exists(manifest), "Losing SteamVR during an initial registration left an opt-in manifest.");
    fake.Running = true;
    fake.Current = new(first, true);
    Fails(() => registration.SetEnabled(moved, true), "does not match");
    Check(!File.Exists(manifest) && fake.Current == new SteamVrApplication(first, true),
        "A conflicting unowned registration was modified.");
    fake.Current = null;
    registration.SetEnabled(first, true);
    fake.Current = new(moved, true);
    Check(registration.GetStatus(first) is { Enabled: false, ThisCopy: false, Verified: true } &&
        !registration.IsAuthorizedLaunch(first), "A mismatched registration was shown as enabled.");
    File.WriteAllText(manifest, """{"applications":[]}""");
    Check(!registration.IsAuthorizedLaunch(first) && !registration.GetStatus(first).Enabled,
        "A malformed manifest was authorized or raised an unhandled error.");
    Fails(() => registration.SetEnabled(first, true), "manifest is invalid");

    var runtime = Path.Combine(root.FullName, "Steam library", "SteamVR");
    var bin = Directory.CreateDirectory(Path.Combine(runtime, "bin", "win64"));
    foreach (var file in new[] { Path.Combine(bin.FullName, "vrserver.exe"), Path.Combine(bin.FullName, "openvr_api.dll"),
        Path.Combine(runtime, "bin", "vrclient_x64.dll") }) File.WriteAllText(file, "fixture only");
    Check(Resolve(runtime) == runtime && Resolve("relative", runtime) is null && Resolve(@"\\server\SteamVR", runtime) is null &&
        Resolve(Path.Combine(root.FullName, "absent"), runtime) is null && Resolve() is null,
        "OpenVR runtime resolution used a network, relative or obsolete history path.");
    using (var unsigned = File.OpenRead(Path.Combine(bin.FullName, "openvr_api.dll")))
        Check(!SteamVrValveTrust.IsSignedByValve(unsigned), "An unsigned local file passed the Valve DLL verifier.");

    if (args is ["--verify-installed-valve-library", var installedLoader])
    {
        // Optional read-only installed signature check. No native OpenVR
        // call, SteamVR process, registry change or manifest side effect.
        using var library = File.OpenRead(installedLoader);
        Check(SteamVrValveTrust.IsSignedByValve(library), "The installed Valve-signed OpenVR loader did not pass trust verification.");
        Console.WriteLine("PASS read-only installed Valve DLL signature");
    }
    Console.WriteLine("PASS SteamVR launch preference, manifest registration, rollback, startup authorization and safe runtime path");
}
finally { root.Delete(recursive: true); }

static void Check(bool result, string detail)
{
    if (!result) throw new InvalidOperationException(detail);
}

static void Fails(Action attempt, string? text = null)
{
    try { attempt(); }
    catch (Exception expected) when (expected is IOException or InvalidDataException or InvalidOperationException)
    {
        Check(text is null || expected.Message.Contains(text, StringComparison.OrdinalIgnoreCase), expected.ToString());
        return;
    }
    throw new InvalidOperationException("Expected an actionable registration failure.");
}

string Executable(string folder)
{
    var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root.FullName, folder)).FullName, "QproFaceTracking.exe");
    File.WriteAllText(path, "fixture only");
    return path;
}

static string? Resolve(params string[] roots)
{
    using var config = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { runtime = roots }));
    return SteamVrOpenVrApplications.FindRegisteredRuntime(config);
}

internal sealed class SteamVrFixture : ISteamVrApplications
{
    internal bool Running = true;
    internal bool FailNextAdd, FailNextRemove, DisconnectOnNextAdd;
    internal SteamVrApplication? Current;
    internal int Connections;
    internal readonly List<(string Action, string Argument)> Changes = [];

    internal ISteamVrApplications Connect()
    {
        Connections++;
        if (!Running) throw new InvalidOperationException("Open SteamVR to check or change the setting.");
        return this;
    }

    public SteamVrApplication? Read(string appKey)
    {
        if (!Running) throw new InvalidOperationException("The SteamVR fixture stopped.");
        AssertKey(appKey);
        return Current;
    }

    public void AddManifest(string path)
    {
        Changes.Add(("add", path));
        if (DisconnectOnNextAdd)
        {
            DisconnectOnNextAdd = false;
            Running = false;
            throw new InvalidOperationException("The SteamVR fixture exited during registration.");
        }
        if (FailNextAdd)
        {
            FailNextAdd = false;
            throw new InvalidOperationException("injected registration failure");
        }
        if (Current is not null) throw new InvalidOperationException("The fixture already has an active entry.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        var app = manifest.RootElement.GetProperty("applications")[0];
        AssertKey(app.GetProperty("app_key").GetString()!);
        Current = new(app.GetProperty("binary_path_windows").GetString()!, false);
    }

    public void RemoveManifest(string path)
    {
        Changes.Add(("remove", path));
        if (FailNextRemove)
        {
            FailNextRemove = false;
            throw new InvalidOperationException("injected unregistration failure");
        }
        Current = null;
    }

    public void SetAutoLaunch(string appKey, bool enabled)
    {
        AssertKey(appKey);
        Changes.Add(("auto", enabled.ToString()));
        Current = (Current ?? throw new InvalidOperationException("The fixture has no active entry.")) with { AutoLaunch = enabled };
    }

    public void Dispose() { }

    private static void AssertKey(string key)
    {
        if (key != SteamVrLaunchRegistration.AppKey)
            throw new InvalidOperationException("The operation addressed a foreign app key.");
    }
}
