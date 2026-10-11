using System.Buffers.Binary;

namespace Qpro.GazeBridge;

// Camera cheeks have their own short lease. Stopping the camera process must
// immediately return to the user's native cheek style, even without a face feed.
internal sealed class CheekCameraReceiver
{
    internal const int Port = 27279;
    internal const int PacketBytes = 16;
    private readonly LiveOverlayState _overlay = new(500);
    private CheekPuffWeights _values;

    internal long PacketAge(long now) => _overlay.PacketAge(now);
    internal bool CustomApplied(long now) => _overlay.CustomApplied(now);

    internal bool Receive(ReadOnlySpan<byte> packet, long now)
    {
        if (packet.Length != PacketBytes || packet[0] != (byte)'Q' ||
            packet[1] != (byte)'P' || packet[2] != (byte)'C' ||
            packet[3] != (byte)'O' || packet[4] != 1 ||
            packet[5] > 1 || packet[6] != 0 || packet[7] != 0)
            return false;
        float left = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(8, 4)));
        float right = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(12, 4)));
        if (!float.IsFinite(left) || !float.IsFinite(right) ||
            left is < 0 or > 1 || right is < 0 or > 1)
            return false;
        _values = new(left, right);
        _overlay.Receive(now, packet[5] == 1);
        return true;
    }

    internal CheekPuffWeights? Resolve(long now, bool nativeAvailable,
        CheekPuffWeights native) => _overlay.Resolve(now, nativeAvailable) switch
    {
        OverlayOutput.Custom => _values,
        OverlayOutput.Native => native,
        OverlayOutput.Neutral => new CheekPuffWeights(0, 0),
        _ => null,
    };

    internal void DiscardPackets() => _overlay.DiscardPackets();
    internal void Reset() => _overlay.Reset();
}
