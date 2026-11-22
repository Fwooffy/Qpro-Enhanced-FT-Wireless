namespace QproFaceTracking.Hub;

internal static class HubTrackingSoundPreference
{
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QproFaceTracking", "config", "tracking-stop-sound.txt");
    internal static bool Load(string? path = null)
    {
        try { return File.ReadAllText(path ?? DefaultPath).Trim().Equals("on", StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    internal static void Save(bool enabled, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, enabled ? "on" : "off"); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
