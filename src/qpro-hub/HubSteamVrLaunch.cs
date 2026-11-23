using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed record SteamVrLaunchStatus(bool Enabled, bool ThisCopy, string Text, bool Verified = false);

internal readonly record struct SteamVrApplication(string BinaryPath, bool AutoLaunch);

internal interface ISteamVrApplications : IDisposable
{
    SteamVrApplication? Read(string appKey);
    void AddManifest(string path);
    void RemoveManifest(string path);
    void SetAutoLaunch(string appKey, bool enabled);
}

internal static class HubSteamVrLaunch
{
    internal const string Argument = "--steamvr-autolaunch";
    private const string MissingProfile = "The Windows user profile is unavailable for SteamVR startup registration.";

    private static SteamVrLaunchRegistration? Default
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.IsPathFullyQualified(local)
                ? new(Path.Combine(local, "QproFaceTracking", "steamvr-launch"), SteamVrOpenVrApplications.Open)
                : null;
        }
    }

    internal static SteamVrLaunchStatus GetStatus(string executablePath) =>
        Default?.GetStatus(executablePath) ?? new(false, false, MissingProfile);
    internal static SteamVrLaunchStatus SetEnabled(string executablePath, bool enabled) =>
        (Default ?? throw new InvalidOperationException(MissingProfile)).SetEnabled(executablePath, enabled);
    internal static bool IsAuthorizedLaunch(string executablePath) => Default?.IsAuthorizedLaunch(executablePath) == true;
}

internal sealed class SteamVrLaunchRegistration(string directory, Func<ISteamVrApplications> connect)
{
    internal const string AppKey = "org.qpro.companion.startup";
    internal const string ManifestName = "qpro-companion.vrmanifest";
    private const string ExecutableName = "QproFaceTracking.exe";
    private string ManifestPath => Path.Combine(directory, ManifestName);
    private static SteamVrLaunchStatus Off => new(false, false, "Launch with SteamVR is off.");

    internal SteamVrLaunchStatus GetStatus(string executablePath)
    {
        Manifest? manifest;
        try { manifest = ReadManifest(); }
        catch (Exception error) when (ExpectedFailure(error))
        { return new(false, false, error.Message); }
        if (manifest is null) return Off;

        bool thisCopy = SamePath(manifest.Path, executablePath);
        try
        {
            using var steam = connect();
            var current = steam.Read(AppKey);
            if (current is null)
                return new(false, thisCopy, "SteamVR does not have the Companion startup app. Enable it again to register this copy.", true);
            if (!SamePath(current.Value.BinaryPath, manifest.Path))
                return new(false, false, "SteamVR's Companion startup entry points elsewhere. Check Manage Startup Overlay Apps.", true);
            return Format(current.Value.AutoLaunch, thisCopy, verified: true);
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            return new(true, thisCopy,
                (thisCopy ? "This copy has a saved SteamVR startup registration. "
                    : "Another copy has a saved SteamVR startup registration. ") + error.Message);
        }
    }

    internal bool IsAuthorizedLaunch(string executablePath)
    {
        try
        {
            var owned = ReadManifest();
            if (owned is null || !SamePath(owned.Path, executablePath) || !File.Exists(executablePath))
                return false;
            using var steam = connect();
            return steam.Read(AppKey) is { AutoLaunch: true } current && SamePath(current.BinaryPath, owned.Path);
        }
        catch (Exception error) when (ExpectedFailure(error)) { return false; }
    }

    internal SteamVrLaunchStatus SetEnabled(string executablePath, bool enabled)
    {
        if (!enabled && ReadManifest() is null) return Off;
        if (!IsCompanionPath(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("Open the extracted QproFaceTracking.exe to change SteamVR startup.");

        // Open SteamVR before writing files. Opt-in and disable operations
        // share one stable manifest and one per-user change lock.
        using var steam = connect();
        Directory.CreateDirectory(directory);
        using var held = new FileStream(Path.Combine(directory, "change.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var previous = ReadManifest();
        if (!enabled && previous is null) return Off;
        var current = steam.Read(AppKey);
        if (current is { } registered) VerifyOwner(registered, previous);
        string target = Path.GetFullPath(executablePath);
        bool sameRegistration = current is { } known && SamePath(known.BinaryPath, target);
        if (enabled && sameRegistration && current is { AutoLaunch: true }) return Format(true, true, verified: true);

        try
        {
            if (enabled && !sameRegistration)
            {
                if (current is not null) steam.RemoveManifest(ManifestPath);
                WriteManifest(CreateManifest(target));
                steam.AddManifest(ManifestPath);
                VerifyRegistered(steam, target);
            }
            if (enabled || current is not null)
            {
                steam.SetAutoLaunch(AppKey, enabled);
                var actual = steam.Read(AppKey);
                if (actual is null || actual.Value.AutoLaunch != enabled)
                    throw new InvalidOperationException("SteamVR did not confirm the requested startup setting.");
            }
            if (enabled) return Format(true, true, verified: true);
            if (current is not null)
            {
                steam.RemoveManifest(ManifestPath);
                if (steam.Read(AppKey) is not null)
                    throw new InvalidOperationException("SteamVR still lists the Companion startup app.");
            }
            File.Delete(ManifestPath);
            return Off with { Verified = true };
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            try { Restore(steam, previous, current, sameRegistration); }
            catch (Exception restoreError) when (ExpectedFailure(restoreError))
            {
                throw new IOException("SteamVR startup could not be changed and restoration could not be verified. "
                    + "Check Startup / Shutdown > Manage Startup Overlay Apps in SteamVR.",
                    new AggregateException(error, restoreError));
            }
            throw;
        }
    }

    private void Restore(ISteamVrApplications steam, Manifest? previous, SteamVrApplication? registered, bool sameRegistration)
    {
        // Check the real state. An OpenVR error can arrive after a partial
        // change, and SteamVR may already be shutting down.
        bool restoredManifest = false;
        try
        {
            var now = steam.Read(AppKey);
            if (!sameRegistration && now is not null &&
                (registered is null || !SamePath(now.Value.BinaryPath, registered.Value.BinaryPath)))
            {
                steam.RemoveManifest(ManifestPath);
                now = steam.Read(AppKey);
            }
            RestoreManifest(previous);
            restoredManifest = true;
            if (registered is { } original)
            {
                if (now is null) steam.AddManifest(ManifestPath);
                VerifyRegistered(steam, original.BinaryPath);
                if (steam.Read(AppKey)?.AutoLaunch != original.AutoLaunch)
                    steam.SetAutoLaunch(AppKey, original.AutoLaunch);
                if (steam.Read(AppKey)?.AutoLaunch != original.AutoLaunch)
                    throw new IOException("SteamVR did not confirm the previous startup preference.");
            }
            else if (now is not null)
                throw new IOException("SteamVR still has a startup app after rollback.");
        }
        finally { if (!restoredManifest) RestoreManifest(previous); }
    }

    private void RestoreManifest(Manifest? previous)
    {
        if (previous is null) File.Delete(ManifestPath);
        else if (ReadManifest() is not { } current || !current.Bytes.AsSpan().SequenceEqual(previous.Bytes))
            WriteManifest(previous.Bytes);
    }

    private static SteamVrLaunchStatus Format(bool enabled, bool thisCopy, bool verified)
        => new(enabled, thisCopy, enabled
            ? (thisCopy ? "SteamVR will launch this copy of Qpro Companion." : "SteamVR will launch another copy. Enable this copy to replace it.")
            : "SteamVR startup is off.", verified);

    private static void VerifyOwner(SteamVrApplication current, Manifest? previous)
    {
        if (previous is null || !SamePath(current.BinaryPath, previous.Path))
            throw new InvalidOperationException("SteamVR's Companion startup entry does not match the Qpro manifest. "
                + "Check SteamVR's Manage Startup Overlay Apps before changing it.");
    }

    private static void VerifyRegistered(ISteamVrApplications steam, string path)
    {
        if (steam.Read(AppKey) is not { } registered || !SamePath(registered.BinaryPath, path))
            throw new InvalidOperationException("SteamVR did not register the selected Companion executable.");
    }

    private sealed record Manifest(string Path, byte[] Bytes);

    private Manifest? ReadManifest()
    {
        byte[] content;
        try
        {
            using var file = File.Open(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > 32768) throw new InvalidDataException("Invalid Qpro SteamVR manifest size.");
            content = new byte[checked((int)file.Length)];
            file.ReadExactly(content);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            using var json = JsonDocument.Parse(content);
            var entries = json.RootElement.GetProperty("applications");
            var entry = entries[0];
            var executable = entry.GetProperty("binary_path_windows").GetString();
            if (entries.GetArrayLength() != 1 || entry.GetProperty("app_key").GetString() != AppKey ||
                entry.GetProperty("launch_type").GetString() != "binary" ||
                !entry.GetProperty("is_dashboard_overlay").GetBoolean() ||
                entry.GetProperty("arguments").GetString() != HubSteamVrLaunch.Argument ||
                !IsCompanionPath(executable))
                throw new InvalidDataException();
            return new(executable!, content);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            IndexOutOfRangeException or InvalidDataException)
        { throw new InvalidDataException("The Qpro SteamVR startup manifest is invalid. Check the registered app in SteamVR.", error); }
    }

    private static byte[] CreateManifest(string executable) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        source = "builtin",
        applications = new[] { new {
            app_key = AppKey,
            launch_type = "binary",
            binary_path_windows = executable,
            arguments = HubSteamVrLaunch.Argument,
            is_dashboard_overlay = true,
            strings = new { en_us = new { name = "Qpro Companion", description = "Start Qpro Companion with SteamVR." } }
        } }
    });

    private void WriteManifest(byte[] bytes)
    {
        string temporary = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                target.Write(bytes);
                target.Flush(flushToDisk: true);
            }
            File.Move(temporary, ManifestPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static bool IsCompanionPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string left, string right) => Path.IsPathFullyQualified(left) &&
        Path.IsPathFullyQualified(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool ExpectedFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException or
        System.ComponentModel.Win32Exception;
}
