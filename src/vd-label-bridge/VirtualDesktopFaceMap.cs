using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Qpro.Shared;

[SupportedOSPlatform("windows")]
internal static class VirtualDesktopFaceMap
{
    // Keep the handles only for this read. A long-lived consumer handle can
    // otherwise keep a stopped Streamer's named map and last pose alive.
    internal static bool ReadStable(string name, byte[] first, byte[] second)
    {
        using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        using var view = map.CreateViewAccessor(0, second.Length, MemoryMappedFileAccess.Read);
        view.ReadArray(0, first, 0, first.Length);
        Thread.MemoryBarrier();
        view.ReadArray(0, second, 0, second.Length);
        return first.AsSpan().SequenceEqual(second);
    }
}
