using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal enum HubComponentKind { Module, Runtime, Rocm, LegacyRocm }
internal sealed record HubComponentUpdate(HubComponentKind Kind, string Name, string Detail,
    string? Recipe, string? EnvironmentRoot = null, bool SteamLink = false);
internal sealed record HubComponentPlan(IReadOnlyList<HubComponentUpdate> Updates, IReadOnlyList<string> Notes);

// Component recipes come from the already verified app package. Startup never
// queries pip or installs a library; it compares receipts written after checks.
internal static class HubComponentUpdates
{
    internal static HubComponentPlan Inspect(string runtimeRoot, string localAppData, string customLibs,
        bool selectedSteamLink, bool customPython, bool customRocm)
    {
        var updates = new List<HubComponentUpdate>();
        var notes = new List<string>();
        var recipes = ReadRecipes(runtimeRoot);
        string supplied = Path.Combine(runtimeRoot, "vrcft-gaze-bridge", "bin", "Release", "net10.0", "Qpro.GazeBridge.dll");
        if (HubModuleInstallation.HasInstalled(customLibs))
        {
            if (!HubModuleInstallation.TryGetInstalledSource(customLibs, selectedSteamLink, out bool steamLink))
                notes.Add("The installed Qpro module source is ambiguous. Use First-time setup to choose and repair it.");
            else if (File.Exists(supplied) && !HubModuleInstallation.IsCurrent(customLibs, supplied, steamLink))
                updates.Add(new(HubComponentKind.Module, "Qpro " + (steamLink ? "Steam Link" : "Virtual Desktop") + " module",
                    "Replace the installed module with this app's version. Close VRCFaceTracking and its ModuleProcess helper first.", null, SteamLink: steamLink));
        }
        if (recipes is null)
        {
            notes.Add("This package has no component recipe information. Runtime maintenance is available in First-time setup.");
            return new(updates, notes);
        }
        string qpro = Path.Combine(localAppData, "QproFaceTracking");
        string shared = Path.Combine(qpro, "runtime");
        if (File.Exists(Path.Combine(shared, ".venv", "Scripts", "python.exe")))
        {
            if (customPython) notes.Add("QPRO_PYTHON selects a custom runtime. Automatic component maintenance leaves it unchanged; clear the override to update Qpro's private runtime.");
            else if (!ReceiptMatches(Path.Combine(shared, "runtime-ready.json"), recipes["runtimeRecipe"],
                Path.Combine(shared, ".venv", "Scripts", "python.exe"), rocm: false))
                updates.Add(new(HubComponentKind.Runtime, "PC runtime and PyTorch",
                    "Update Qpro's private Python libraries to this release's requirements, then verify imports. Other Python installations are untouched.", recipes["runtimeRecipe"], shared));
        }
        if (customRocm)
            notes.Add("QPRO_ROCM_HOME selects custom storage. Use First-time setup to maintain it, or clear the override before component maintenance.");
        else
        {
            string indexPath = Path.Combine(qpro, "r", "rocm-runtimes.json");
            using var index = ReadObject(indexPath);
            if (index is not null && Integer(index.RootElement, "schema") == 1)
            {
                AddRocm("latestEnvironment", "rocmRecipe", HubComponentKind.Rocm, "AMD ROCm and PyTorch");
                AddRocm("legacyEnvironment", "legacyRocmRecipe", HubComponentKind.LegacyRocm, "Legacy AMD ROCm and PyTorch");
                void AddRocm(string field, string recipeField, HubComponentKind kind, string name)
                {
                    string? environment = Text(index.RootElement, field);
                    if (environment is null)
                    {
                        if (index.RootElement.TryGetProperty(field, out _))
                            notes.Add(name + ": the saved environment path is invalid. Use First-time setup to repair its registration.");
                        return;
                    }
                    if (!IsOwnedRocmEnvironment(environment))
                    {
                        notes.Add(name + ": the registered environment could not be confirmed as Qpro-owned. Use First-time setup to repair it.");
                        return;
                    }
                    string python = Path.Combine(environment, "Scripts", "python.exe");
                    if (!ReceiptMatches(Path.Combine(environment, "qpro-rocm-ready.json"), recipes[recipeField], python, rocm: true))
                        updates.Add(new(kind, name,
                            "Prepare the release's compatible GPU packages in a separate environment. Keep the current environment until GPU checks pass.", recipes[recipeField], environment));
                }
            }
            else if (File.Exists(indexPath) || (Directory.Exists(Path.Combine(qpro, "r")) &&
                     Directory.EnumerateDirectories(Path.Combine(qpro, "r")).Take(65).Any(path => Path.GetFileName(path).Contains("gfx", StringComparison.Ordinal))) ||
                     Directory.Exists(Path.Combine(runtimeRoot, ".venv-rocm")) || Directory.Exists(Path.Combine(runtimeRoot, ".venv-rocm-experimental")))
                notes.Add("An older AMD environment has no valid registration. Use First-time setup to verify and register it before component updates.");
        }
        return new(updates, notes);
    }

    internal static Dictionary<string, string>? ReadRecipes(string runtimeRoot)
    {
        using var document = ReadObject(Path.Combine(runtimeRoot, "release-manifest.json"), packageSource: true);
        if (document is null || !document.RootElement.TryGetProperty("componentUpdates", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object || Integer(value, "schema") != 1) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!keys.Add(field.Name)) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in new[] { "runtimeRecipe", "rocmRecipe", "legacyRocmRecipe" })
        {
            string? hash = Text(value, key);
            if (hash is null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)) return null;
            result.Add(key, hash.ToLowerInvariant());
        }
        return result;
    }

    internal static bool ReceiptMatches(string marker, string recipe, string expectedPython, bool rocm)
    {
        using var document = ReadObject(marker);
        if (document is null) return false;
        var root = document.RootElement;
        string? python = Text(root, "python");
        return (rocm ? Integer(root, "schema") == 1 : Text(root, "format") == "qpro-runtime-ready-v1") &&
            string.Equals(Text(root, "recipeSha256"), recipe, StringComparison.OrdinalIgnoreCase) &&
            python is not null && SamePath(python, expectedPython) && RegularFile(expectedPython);
    }

    internal static bool RuntimeUpdateVerified(HubComponentUpdate item, string localAppData)
    {
        if (item.Recipe is null) return false;
        string qpro = Path.Combine(localAppData, "QproFaceTracking");
        if (item.Kind == HubComponentKind.Runtime)
            return ReceiptMatches(Path.Combine(qpro, "runtime", "runtime-ready.json"), item.Recipe,
                Path.Combine(qpro, "runtime", ".venv", "Scripts", "python.exe"), rocm: false);
        if (item.Kind is not (HubComponentKind.Rocm or HubComponentKind.LegacyRocm)) return false;
        using var index = ReadObject(Path.Combine(qpro, "r", "rocm-runtimes.json"));
        if (index is null || Integer(index.RootElement, "schema") != 1) return false;
        string? environment = Text(index.RootElement, item.Kind == HubComponentKind.LegacyRocm ? "legacyEnvironment" : "latestEnvironment");
        return environment is not null && IsOwnedRocmEnvironment(environment) &&
            ReceiptMatches(Path.Combine(environment, "qpro-rocm-ready.json"), item.Recipe,
                Path.Combine(environment, "Scripts", "python.exe"), rocm: true);
    }

    internal static bool IsOwnedRocmEnvironment(string environment)
    {
        if (!LocalPath(environment) || !Directory.Exists(environment) || HasReparse(environment)) return false;
        using var owner = ReadObject(Path.Combine(environment, "qpro-rocm-environment.json"));
        return owner is not null && Text(owner.RootElement, "format") == "qpro-rocm-environment-v1" &&
            Text(owner.RootElement, "environment") is { } recorded && SamePath(recorded, environment) &&
            RegularFile(Path.Combine(environment, "Scripts", "python.exe"));
    }

    internal static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }
    private static bool LocalPath(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal);
    private static bool RegularFile(string path) => LocalPath(path) && File.Exists(path) && !HasReparse(path);
    private static bool HasReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }
    private static JsonDocument? ReadObject(string path, bool packageSource = false)
    {
        try
        {
            bool readable = packageSource ? HubModuleInstallation.IsReadablePackageSource(path) : RegularFile(path);
            if (!readable || new FileInfo(path).Length > 65_536) return null;
            var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (document.RootElement.EnumerateObject().All(field => names.Add(field.Name))) return document;
            }
            document.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { }
        return null;
    }
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
}
