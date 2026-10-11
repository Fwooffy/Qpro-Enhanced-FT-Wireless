using System.Net;
using System.Net.Sockets;

namespace Qpro.GazeBridge;

internal enum OverlayOutput { Unchanged, Custom, Native, Neutral }

// Expiration is independent of native source availability. Only an overlay
// that actually wrote a slot may neutralize it when neither source is valid.
internal sealed class LiveOverlayState(long timeoutMs)
{
    private long _receivedAt;
    private bool _hasPacket;
    private bool _enabled;
    private bool _applied;

    internal long PacketAge(long now) => _hasPacket && _enabled && now >= _receivedAt ? now - _receivedAt : -1;
    internal bool CustomApplied(long now) => _applied && PacketAge(now) is var age && age >= 0 && age <= timeoutMs;

    internal void Receive(long now, bool enabled)
    {
        _receivedAt = now;
        _hasPacket = true;
        _enabled = enabled;
    }

    internal OverlayOutput Resolve(long now, bool nativeAvailable)
    {
        if (_hasPacket && _enabled && now >= _receivedAt && now - _receivedAt <= timeoutMs)
        {
            _applied = true;
            return OverlayOutput.Custom;
        }
        if (nativeAvailable)
        {
            _applied = false;
            return OverlayOutput.Native;
        }
        if (!_applied) return OverlayOutput.Unchanged;
        _applied = false;
        return OverlayOutput.Neutral;
    }

    // An inactive interval ends packet freshness, but keep ownership until
    // the active module can restore native output or clear its last overlay.
    internal void DiscardPackets() => _hasPacket = _enabled = false;

    internal void Reset()
    {
        DiscardPackets();
        _applied = false;
    }
}

internal static class LocalDatagrams
{
    // Poll detects even a zero-byte datagram. Checking Available against the
    // expected packet size can leave a short packet blocking the whole queue.
    internal static IEnumerable<byte[]> ReadPending(UdpClient? socket, int limit = 256)
    {
        if (socket is null) yield break;
        for (int index = 0; index < limit && socket.Client.Poll(0, SelectMode.SelectRead); index++)
        {
            IPEndPoint sender = new(IPAddress.Loopback, 0);
            byte[] packet;
            try { packet = socket.Receive(ref sender); }
            catch (SocketException error) when (
                error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
            { yield break; }
            if (IPAddress.IsLoopback(sender.Address)) yield return packet;
        }
    }

    internal static bool DiscardPending(UdpClient? socket)
    {
        foreach (byte[] _ in ReadPending(socket)) { }
        return socket is null || !socket.Client.Poll(0, SelectMode.SelectRead);
    }
}
