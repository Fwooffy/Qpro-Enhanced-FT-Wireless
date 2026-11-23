using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Qpro.GazeBridge;

/// <summary>
/// Reads Steam Link's local OSC output without taking over VRCFT's other modules.
/// Call Poll and read the snapshot from the same thread. Steam Link must be set
/// to its custom OSC output port (9015).
/// </summary>
internal sealed class SteamOscSource : IDisposable
{
    internal const int DefaultPort = 9015;
    private const int MaxDatagramBytes = 16 * 1024;
    private const int MaxMessagesPerDatagram = 256;
    private const int MaxBundleDepth = 3;
    private const int MaxStringBytes = 128;
    private const int MaxDatagramsPerPoll = 32;
    private const long DefaultFreshMs = 500;
    private const string FaceWeightPrefix = "/sl/xrfb/facew/";

    // The Virtual Desktop body-state order is the same 70-weight XR_FB schema.
    // Steam Link spells a few suffixes differently; BuildExpressionIndices adds
    // those aliases without changing the shared indices.
    internal static IReadOnlyList<string> ExpressionNames { get; } = Array.AsReadOnly(new[]
    {
        "BrowLowererL", "BrowLowererR", "CheekPuffL", "CheekPuffR",
        "CheekRaiserL", "CheekRaiserR", "CheekSuckL", "CheekSuckR",
        "ChinRaiserB", "ChinRaiserT", "DimplerL", "DimplerR",
        "EyesClosedL", "EyesClosedR", "EyesLookDownL", "EyesLookDownR",
        "EyesLookLeftL", "EyesLookLeftR", "EyesLookRightL", "EyesLookRightR",
        "EyesLookUpL", "EyesLookUpR", "InnerBrowRaiserL", "InnerBrowRaiserR",
        "JawDrop", "JawSidewaysLeft", "JawSidewaysRight", "JawThrust",
        "LidTightenerL", "LidTightenerR", "LipCornerDepressorL",
        "LipCornerDepressorR", "LipCornerPullerL", "LipCornerPullerR",
        "LipFunnelerLb", "LipFunnelerLt", "LipFunnelerRb", "LipFunnelerRt",
        "LipPressorL", "LipPressorR", "LipPuckerL", "LipPuckerR",
        "LipStretcherL", "LipStretcherR", "LipSuckLb", "LipSuckLt",
        "LipSuckRb", "LipSuckRt", "LipTightenerL", "LipTightenerR",
        "LipsToward", "LowerLipDepressorL", "LowerLipDepressorR",
        "MouthLeft", "MouthRight", "NoseWrinklerL", "NoseWrinklerR",
        "OuterBrowRaiserL", "OuterBrowRaiserR", "UpperLidRaiserL",
        "UpperLidRaiserR", "UpperLipRaiserL", "UpperLipRaiserR",
        "TongueTipInterdental", "TongueTipAlveolar", "TongueFrontDorsalPalate",
        "TongueMidDorsalPalate", "TongueBackDorsalVelar", "TongueOut",
        "TongueRetreat"
    });

    private static readonly Dictionary<string, int> ExpressionIndices = BuildExpressionIndices();
    private readonly UdpClient _socket;
    private readonly float[] _expressions = new float[70];
    private readonly long[] _expressionTicks = new long[70];
    private readonly float[] _rawEyeCoordinates = new float[4];
    private readonly long[] _rawEyeTicks = new long[4];
    private long _lastCanonicalTongueTick;
    private long _lastCombinedGazeTick;
    private float _combinedGazeX;
    private float _combinedGazeY;
    private (float X, float Y, float Z) _combinedGazePoint;

    internal SteamOscSource(int port = DefaultPort)
    {
        _socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            _socket.Client.ExclusiveAddressUse = true;
            _socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
            _socket.Client.Blocking = false;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    internal ReadOnlySpan<float> Expressions => _expressions;
    internal long LastFaceTick { get; private set; }
    internal long LastEyeTick { get; private set; }
    internal long LastGazeTick { get; private set; }
    internal long LastLowerFaceCapabilityTick { get; private set; }
    internal long LastUpperFaceCapabilityTick { get; private set; }
    internal long ReceivedDatagrams { get; private set; }
    internal long ParsedDatagrams { get; private set; }
    internal long RejectedDatagrams { get; private set; }
    internal bool? LowerFaceAvailable { get; private set; }
    internal bool? UpperFaceAvailable { get; private set; }
    internal float LeftOpenness => CalculateOpenness(_expressions[12], _expressions[28]);
    internal float RightOpenness => CalculateOpenness(_expressions[13], _expressions[29]);

    internal bool HasFreshFace(long now, long maxAgeMs = DefaultFreshMs) => IsFresh(LastFaceTick, now, maxAgeMs);
    internal long? ExpressionAge(int index, long now) =>
        IsFresh(_expressionTicks[index], now, DefaultFreshMs) ? now - _expressionTicks[index] : null;
    internal bool HasFreshEye(long now, long maxAgeMs = DefaultFreshMs) => IsFresh(LastEyeTick, now, maxAgeMs);
    internal bool HasFreshLowerFaceCapability(long now, long maxAgeMs = DefaultFreshMs) =>
        IsFresh(LastLowerFaceCapabilityTick, now, maxAgeMs);
    internal bool HasFreshUpperFaceCapability(long now, long maxAgeMs = DefaultFreshMs) =>
        IsFresh(LastUpperFaceCapabilityTick, now, maxAgeMs);
    internal bool HasAvailableLowerFace(long now, long maxAgeMs = DefaultFreshMs) =>
        LowerFaceAvailable == true && HasFreshLowerFaceCapability(now, maxAgeMs);
    internal bool HasAvailableUpperFace(long now, long maxAgeMs = DefaultFreshMs) =>
        UpperFaceAvailable == true && HasFreshUpperFaceCapability(now, maxAgeMs);
    internal bool HasFreshGaze(long now, long maxAgeMs = DefaultFreshMs) =>
        TryGetGaze(now, out _, out _, out _, out _, maxAgeMs);

    internal int FreshExpressionCount(long now, long maxAgeMs = DefaultFreshMs)
    {
        int count = 0;
        foreach (long tick in _expressionTicks)
            if (IsFresh(tick, now, maxAgeMs)) count++;
        return count;
    }

    internal void Poll() => Poll(Environment.TickCount64);

    internal void Poll(long now)
    {
        for (int i = 0; i < MaxDatagramsPerPoll && _socket.Client.Poll(0, SelectMode.SelectRead); i++)
        {
            IPEndPoint sender = new(IPAddress.Loopback, 0);
            byte[] datagram;
            try
            {
                datagram = _socket.Receive(ref sender);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
            {
                break;
            }

            // The socket itself is bound to 127.0.0.1, and this check also
            // protects against a future change to the bind address.
            if (!IPAddress.IsLoopback(sender.Address) || datagram.Length > MaxDatagramBytes)
                continue;
            ReceivedDatagrams++;
            if (ParseDatagram(datagram, now))
                ParsedDatagrams++;
            else
                RejectedDatagrams++;
        }
        ExpireWeights(now);
    }

    internal bool DiscardPending()
    {
        bool drained = LocalDatagrams.DiscardPending(_socket);
        Array.Clear(_expressions);
        Array.Clear(_expressionTicks);
        Array.Clear(_rawEyeCoordinates);
        Array.Clear(_rawEyeTicks);
        _lastCanonicalTongueTick = _lastCombinedGazeTick = 0;
        LastFaceTick = LastEyeTick = LastGazeTick = 0;
        LastLowerFaceCapabilityTick = LastUpperFaceCapabilityTick = 0;
        LowerFaceAvailable = UpperFaceAvailable = null;
        return drained;
    }

    internal bool TryGetCombinedGaze(long now, out float x, out float y, long maxAgeMs = DefaultFreshMs) =>
        TryGetCombinedGaze(now, out x, out y, out _, maxAgeMs);

    internal bool TryGetCombinedGaze(long now, out float x, out float y,
        out (float X, float Y, float Z) point, long maxAgeMs = DefaultFreshMs)
    {
        x = _combinedGazeX;
        y = _combinedGazeY;
        point = _combinedGazePoint;
        return IsFresh(_lastCombinedGazeTick, now, maxAgeMs);
    }

    /// <summary>
    /// Steam Link's point gives one shared gaze angle. The separate avatar
    /// parameters use a different sign/scale and are diagnostic only until
    /// their conversion into VRCFT's gaze coordinates is verified.
    /// </summary>
    internal bool TryGetGaze(long now, out float leftX, out float leftY,
        out float rightX, out float rightY, long maxAgeMs = DefaultFreshMs)
    {
        if (TryGetCombinedGaze(now, out float x, out float y, maxAgeMs))
        {
            leftX = rightX = x;
            leftY = rightY = y;
            return true;
        }
        leftX = leftY = rightX = rightY = 0;
        return false;
    }

    internal bool TryGetRawPerEyeGaze(long now, out float leftX, out float leftY,
        out float rightX, out float rightY, long maxAgeMs = DefaultFreshMs)
    {
        bool fresh = true;
        foreach (long tick in _rawEyeTicks)
            fresh &= IsFresh(tick, now, maxAgeMs);
        leftX = _rawEyeCoordinates[0];
        leftY = _rawEyeCoordinates[1];
        rightX = _rawEyeCoordinates[2];
        rightY = _rawEyeCoordinates[3];
        return fresh;
    }

    internal bool ParseDatagram(ReadOnlySpan<byte> datagram, long tick)
    {
        if (datagram.Length is < 4 or > MaxDatagramBytes)
            return false;
        // Validate the whole bundle first. A truncated later element must not
        // leave a partially updated face from an otherwise invalid datagram.
        int messagesRemaining = MaxMessagesPerDatagram;
        if (!ParseElement(datagram, tick, 0, ref messagesRemaining, apply: false))
            return false;
        messagesRemaining = MaxMessagesPerDatagram;
        return ParseElement(datagram, tick, 0, ref messagesRemaining, apply: true);
    }

    private bool ParseElement(ReadOnlySpan<byte> element, long tick, int depth,
        ref int messagesRemaining, bool apply)
    {
        if (element.StartsWith("#bundle\0"u8))
        {
            if (depth >= MaxBundleDepth || element.Length < 16)
                return false;
            int cursor = 16; // OSC bundle header and 64-bit timetag.
            while (cursor < element.Length)
            {
                if (element.Length - cursor < 4)
                    return false;
                int size = BinaryPrimitives.ReadInt32BigEndian(element.Slice(cursor, 4));
                cursor += 4;
                if (size < 4 || size > element.Length - cursor)
                    return false;
                if (!ParseElement(element.Slice(cursor, size), tick, depth + 1, ref messagesRemaining, apply))
                    return false;
                cursor += size;
            }
            return cursor == element.Length;
        }

        if (--messagesRemaining < 0 ||
            !TryReadOscString(ref element, out string address) ||
            !TryReadOscString(ref element, out string tags) ||
            tags.Length < 2 || tags[0] != ',' || tags.Length > 5)
            return false;

        Span<float> values = stackalloc float[4];
        int valueCount = 0;
        foreach (char tag in tags.AsSpan(1))
        {
            float value;
            switch (tag)
            {
                case 'f':
                    if (element.Length < 4) return false;
                    value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(element[..4]));
                    element = element[4..];
                    break;
                case 'i':
                    if (element.Length < 4) return false;
                    value = BinaryPrimitives.ReadInt32BigEndian(element[..4]);
                    element = element[4..];
                    break;
                case 'T': value = 1; break;
                case 'F': value = 0; break;
                default: return false;
            }
            if (!float.IsFinite(value)) return false;
            values[valueCount++] = value;
        }
        if (!element.IsEmpty || valueCount == 0)
            return false;
        if (apply)
            ApplyMessage(address, values[..valueCount], tick);
        return true;
    }

    private void ApplyMessage(string address, ReadOnlySpan<float> values, long tick)
    {
        if (address.StartsWith(FaceWeightPrefix, StringComparison.OrdinalIgnoreCase) &&
            ExpressionIndices.TryGetValue(address[FaceWeightPrefix.Length..], out int index) &&
            values.Length == 1)
        {
            if (index == 68)
            {
                if (address.EndsWith("/ToungeOut", StringComparison.OrdinalIgnoreCase) &&
                    IsFresh(_lastCanonicalTongueTick, tick, DefaultFreshMs))
                    return;
                if (address.EndsWith("/TongueOut", StringComparison.OrdinalIgnoreCase))
                    _lastCanonicalTongueTick = tick;
            }
            _expressions[index] = Math.Clamp(values[0], 0, 1);
            _expressionTicks[index] = tick;
            LastFaceTick = tick;
            if (index is 12 or 13 or 28 or 29)
                LastEyeTick = tick;
            return;
        }
        if (address.Equals("/sl/eyeTrackedGazePoint", StringComparison.OrdinalIgnoreCase) && values.Length == 3)
        {
            // Both reference modules derive one combined gaze angle from this
            // three-dimensional point. It cannot provide independent gaze.
            if (values[0] == 0 && values[1] == 0 && values[2] == 0)
                return;
            _combinedGazeX = MathF.Atan2(values[0], -values[2]);
            _combinedGazeY = MathF.Atan2(values[1], -values[2]);
            _combinedGazePoint = (values[0], values[1], values[2]);
            _lastCombinedGazeTick = LastGazeTick = tick;
            return;
        }
        if (values.Length != 1) return;
        int coordinate = address switch
        {
            "/avatar/parameters/LeftEyeX" => 0,
            "/avatar/parameters/LeftEyeY" => 1,
            "/avatar/parameters/RightEyeX" => 2,
            "/avatar/parameters/RightEyeY" => 3,
            _ => -1
        };
        if (coordinate >= 0)
        {
            _rawEyeCoordinates[coordinate] = values[0];
            _rawEyeTicks[coordinate] = tick;
        }
        else if (address.Equals("/sl/xrfb/facec/LowerFace", StringComparison.OrdinalIgnoreCase))
        {
            LowerFaceAvailable = values[0] != 0;
            LastLowerFaceCapabilityTick = tick;
        }
        else if (address.Equals("/sl/xrfb/facec/UpperFace", StringComparison.OrdinalIgnoreCase))
        {
            UpperFaceAvailable = values[0] != 0;
            LastUpperFaceCapabilityTick = tick;
        }
    }

    private static bool TryReadOscString(ref ReadOnlySpan<byte> packet, out string value)
    {
        value = string.Empty;
        int length = packet.IndexOf((byte)0);
        if (length is < 1 or > MaxStringBytes)
            return false;
        int paddedLength = (length + 4) & ~3;
        if (paddedLength > packet.Length)
            return false;
        for (int i = 0; i < length; i++)
            if (packet[i] is < 32 or > 126)
                return false;
        for (int i = length; i < paddedLength; i++)
            if (packet[i] != 0)
                return false;
        value = Encoding.ASCII.GetString(packet[..length]);
        packet = packet[paddedLength..];
        return true;
    }

    private void ExpireWeights(long now)
    {
        for (int i = 0; i < _expressions.Length; i++)
        {
            if (_expressionTicks[i] != 0 && now - _expressionTicks[i] > DefaultFreshMs)
            {
                _expressions[i] = 0;
                _expressionTicks[i] = 0;
            }
        }
    }

    private static float CalculateOpenness(float blink, float lidTightener) =>
        1.0f - Math.Clamp(blink + blink * lidTightener, 0.0f, 1.0f);

    private static bool IsFresh(long tick, long now, long maxAgeMs) =>
        tick != 0 && maxAgeMs >= 0 && now >= tick && now - tick <= maxAgeMs;

    private static Dictionary<string, int> BuildExpressionIndices()
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ExpressionNames.Count; i++)
            map.Add(ExpressionNames[i], i);
        map.Add("FrontDorsalPalate", 65);
        map.Add("MidDorsalPalate", 66);
        map.Add("BackDorsalVelar", 67);
        // Some Steam Link builds send this misspelling alongside TongueOut.
        map.Add("ToungeOut", 68);
        return map;
    }

    public void Dispose() => _socket.Dispose();
}
