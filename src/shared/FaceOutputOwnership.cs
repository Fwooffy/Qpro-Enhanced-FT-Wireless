using System.Text;

namespace Qpro.Shared;

internal enum FaceOutputRoute { Unconfigured, Direct, Vrcft, Unavailable }

// The Hub and the VRCFT module run in separate processes. Keep the same file
// in place after releasing it so every owner locks the same directory entry.
internal static class FaceOutputOwnership
{
    private const string PreferenceFile = "face-output-route.txt";
    private const string OwnerFile = "face-output-owner.lock";
    private const string ExtrasFile = "face-overlay-owner.lock";

    private static string DirectoryFor(string? configDirectory)
    {
        if (configDirectory is not null) return configDirectory;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) throw new IOException("The per-user configuration directory is unavailable.");
        return Path.Combine(local, "QproFaceTracking", "config");
    }

    internal static FaceOutputRoute ReadRoute(string? configDirectory = null)
    {
        try
        {
            using var file = new FileStream(Path.Combine(DirectoryFor(configDirectory), PreferenceFile),
                FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 128) return FaceOutputRoute.Unavailable;
            using var reader = new StreamReader(file, Encoding.UTF8);
            return reader.ReadToEnd().Trim() switch
            {
                "direct" => FaceOutputRoute.Direct,
                "vrcft" => FaceOutputRoute.Vrcft,
                _ => FaceOutputRoute.Unavailable
            };
        }
        catch (FileNotFoundException) { return FaceOutputRoute.Unconfigured; }
        catch (DirectoryNotFoundException) { return FaceOutputRoute.Unconfigured; }
        catch (Exception ex) when (IsAccessFailure(ex)) { return FaceOutputRoute.Unavailable; }
    }

    // The caller holds the owner handle throughout a transition.
    internal static void SaveRoute(FaceOutputRoute route, string? configDirectory = null)
    {
        var value = route switch
        {
            FaceOutputRoute.Direct => "direct",
            FaceOutputRoute.Vrcft => "vrcft",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        var directory = Directory.CreateDirectory(DirectoryFor(configDirectory));
        string target = Path.Combine(directory.FullName, PreferenceFile);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static IDisposable? TryAcquireDirect(string? configDirectory = null)
        => TryAcquireRoute(FaceOutputRoute.Direct, configDirectory, out _);

    internal static IDisposable? TryAcquireModule(string? configDirectory = null)
        => TryAcquireModule(out _, configDirectory);

    internal static IDisposable? TryAcquireModule(out FaceOutputRoute current, string? configDirectory = null)
        => TryAcquireRoute(FaceOutputRoute.Vrcft, configDirectory, out current);

    private static IDisposable? TryAcquireRoute(FaceOutputRoute requested, string? configDirectory, out FaceOutputRoute current)
    {
        current = FaceOutputRoute.Unavailable;
        var lease = TryAcquireTransition(configDirectory);
        if (lease is null) return null;
        // Check after locking. A previous owner could have changed the route
        // between another process reading the preference and opening this file.
        current = ReadRoute(configDirectory);
        if (current == requested || current == FaceOutputRoute.Unconfigured) return lease;
        lease.Dispose();
        return null;
    }

    internal static IDisposable? TryAcquireTransition(string? configDirectory = null)
        => TryAcquireFile(OwnerFile, configDirectory);

    // Camera and gaze processes from separate Companion windows feed the same
    // local inputs, even when the final writer is a separate VRCFT process.
    internal static IDisposable? TryAcquireExtras(string? configDirectory = null)
        => TryAcquireFile(ExtrasFile, configDirectory);

    private static IDisposable? TryAcquireFile(string name, string? configDirectory)
    {
        try
        {
            var directory = Directory.CreateDirectory(DirectoryFor(configDirectory));
            return new FileStream(Path.Combine(directory.FullName, name), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (IsAccessFailure(ex)) { return null; }
    }

    private static bool IsAccessFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
