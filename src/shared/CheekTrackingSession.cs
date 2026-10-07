using System.Runtime.Versioning;
using System.Security.Principal;

namespace Qpro.Shared;

// Saved cheek preferences describe the next session, not permission to keep
// overriding the streaming app after Stop. Windows releases this handle even
// if the Companion crashes, so no persistent "running" flag can be left behind.
[SupportedOSPlatform("windows")]
internal sealed class CheekTrackingSession : IDisposable
{
    private static readonly string SessionName = CreateSessionName();
    private readonly EventWaitHandle _presence;

    private static string CreateSessionName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return @"Local\QproFaceTracking.CheekTracking.v1." + identity.User!.Value;
    }

    internal CheekTrackingSession(string? name = null)
    {
        _presence = new EventWaitHandle(true, EventResetMode.ManualReset, name ?? SessionName);
    }

    internal static bool IsActive(string? name = null)
    {
        try
        {
            // Never retain the reader's handle: that would keep the session
            // object alive after the last Companion process has exited.
            using var presence = EventWaitHandle.OpenExisting(name ?? SessionName);
            return presence.WaitOne(0);
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    // Do not Reset a shared event: another running Companion session may own
    // its own handle. The object disappears when the final owner releases it.
    public void Dispose() => _presence.Dispose();
}
