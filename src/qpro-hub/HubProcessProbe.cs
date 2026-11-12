using System.ComponentModel;
using System.Diagnostics;

namespace QproFaceTracking.Hub;

internal static class HubProcessProbe
{
    internal static bool AnyRunning(string name)
        => Any(Process.GetProcessesByName(name), _ => true);

    internal static bool StartedBefore(string name, DateTime selectedAtUtc)
        => Any(Process.GetProcessesByName(name), process =>
        {
            try { return process.StartTime.ToUniversalTime() < selectedAtUtc; }
            catch (InvalidOperationException) { return false; } // Exited during the observation.
            catch (Win32Exception) { return true; } // Cannot confirm that it loaded the new selection.
        });

    // GetProcessesByName creates every Process object before the predicate
    // runs. Dispose the whole snapshot even when the first match ends the check.
    internal static bool Any<T>(IReadOnlyList<T> snapshot, Func<T, bool> predicate) where T : IDisposable
    {
        try
        {
            for (var index = 0; index < snapshot.Count; index++)
                if (predicate(snapshot[index])) return true;
            return false;
        }
        finally
        {
            for (var index = 0; index < snapshot.Count; index++) snapshot[index].Dispose();
        }
    }
}
