using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QproFaceTracking.Hub;

internal sealed partial class VrchatFaceOutput : IDisposable
{
    private const string AvatarPrefix = "/avatar/parameters/";
    private const string NativeAddress = "/tracking/face/v1";
    private const string EyePrefix = "/tracking/eye/";
    private const string BuiltinGaze = EyePrefix + "LeftRightPitchYaw";
    private const string BuiltinBlink = EyePrefix + "EyesClosedAmount";
    private const int NativeKeyCount = 102;
    private const float MinimumChange = 1f / 255;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(500);

    private sealed class Binding(string address, char oscType, Expression expression, string group, int bit, bool sign)
    {
        internal readonly string Address = address;
        internal readonly char OscType = oscType;
        internal readonly Expression Expression = expression;
        internal readonly string Group = group;
        internal readonly int Bit = bit;
        internal readonly bool Sign = sign;
        internal float? Last;
    }

    private readonly Socket _socket;
    private readonly IPEndPoint _endpoint;
    private readonly TimeProvider _time;
    private readonly Binding[] _bindings;
    private readonly Binding[] _toggles;
    private readonly Dictionary<string, int> _binaryMasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _signedGroups = new(StringComparer.Ordinal);
    private readonly bool _nativeFace;
    private readonly bool _nativeBlink;
    private readonly string? _nativeGaze;
    private readonly byte[] _lastNativeFrame = new byte[NativeKeyCount];
    private bool _nativeFaceActive;
    private bool _nativeGazeActive;
    private float _lastBlink;
    private bool _nativeBlinkActive;
    private readonly float[] _lastGaze = new float[6];
    private long? _lastHeartbeat;
    private bool _disposed;

    internal int SupportedInputCount => _nativeFace ? 1 : _bindings.Length + (_nativeBlink ? 1 : 0) + (_nativeGaze is not null ? 1 : 0);
    internal bool EyeOnly => !_nativeFace && _bindings.Length == 0 && (_nativeBlink || _nativeGaze is not null);
    internal bool HasActiveOutput { get; private set; }

    internal VrchatFaceOutput(IPEndPoint endpoint, IReadOnlyDictionary<string, string> inputs, bool nativeFace = false,
        TimeProvider? time = null, IReadOnlySet<string>? blockedInputs = null)
    {
        _endpoint = endpoint;
        _time = time ?? TimeProvider.System;
        _nativeFace = nativeFace;

        var bindings = new List<Binding>();
        var toggles = new List<Binding>();
        if (!nativeFace)
        {
            foreach ((string address, string type) in inputs)
            {
                if (ParseBinding(address, type) is not Binding binding)
                    continue;

                if (IsTrackingToggle(Leaf(binding.Group)))
                {
                    toggles.Add(binding);
                    continue;
                }

                bindings.Add(binding);
                if (binding.Bit != 0)
                    _binaryMasks[binding.Group] = _binaryMasks.GetValueOrDefault(binding.Group) | binding.Bit;
                if (binding.Sign)
                    _signedGroups.Add(binding.Group);
            }

            if (!bindings.Any(binding => IsGazeName(Leaf(binding.Group))))
            {
                (string Name, string Type)[] gazeInputs =
                [
                    ("LeftRightPitchYaw", "ffff"), ("LeftRightVec", "ffffff"),
                    ("CenterPitchYaw", "ff"), ("CenterVec", "fff"),
                ];
                foreach ((string name, string expectedType) in gazeInputs)
                {
                    string address = EyePrefix + name;
                    if (blockedInputs?.Contains(address) != true && inputs.TryGetValue(address, out string? actualType) && actualType == expectedType)
                    {
                        _nativeGaze = address;
                        break;
                    }
                }

                // These global inputs can work on avatars without any OSC avatar parameters.
                if (_nativeGaze is null && blockedInputs?.Contains(BuiltinGaze) != true && !inputs.ContainsKey(BuiltinGaze))
                    _nativeGaze = BuiltinGaze;
            }

            _nativeBlink = !bindings.Any(binding => IsBlinkName(Leaf(binding.Group))) &&
                blockedInputs?.Contains(BuiltinBlink) != true &&
                (!inputs.TryGetValue(BuiltinBlink, out string? blinkType) || GetTypeTag(blinkType) == 'f');
        }

        _bindings = bindings.ToArray();
        _toggles = toggles.ToArray();
        _socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { SendTimeout = 200 };
    }

    // Calls are serialized by the direct tracking loop.
    internal void Send(IReadOnlyDictionary<string, float> values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long now = _time.GetTimestamp();
        bool refresh = !_lastHeartbeat.HasValue || _time.GetElapsedTime(_lastHeartbeat.Value, now) >= Heartbeat;

        if (_nativeFace)
        {
            SendNativeFace(values, refresh);
            HasActiveOutput = _nativeFaceActive;
        }
        else
        {
            bool eye = false;
            bool expression = false;
            foreach (Binding binding in _bindings)
            {
                if (binding.Expression.Read(values) is not float sample)
                {
                    Release(binding);
                    continue;
                }

                Write(binding, Encode(binding, sample), refresh);
                eye |= binding.Expression.Eye;
                expression |= !binding.Expression.Eye;
            }

            foreach (Binding toggle in _toggles)
            {
                bool active = Leaf(toggle.Group) == "EyeTrackingActive" ? eye : expression;
                if (active)
                    Write(toggle, 1, refresh);
                else
                    Release(toggle);
            }

            SendNativeEyes(values, refresh);
            HasActiveOutput = eye || expression || _nativeBlinkActive || _nativeGazeActive;
        }

        if (refresh)
            _lastHeartbeat = now;
    }

    internal void Reset()
    {
        if (_disposed)
            return;

        if (_nativeFaceActive)
            ReleaseNativeFace();
        foreach (Binding binding in _bindings)
            Release(binding);
        foreach (Binding toggle in _toggles)
            Release(toggle);

        ReleaseNativeEyes();
        HasActiveOutput = false;
        _lastHeartbeat = null;
    }

    private static Binding? ParseBinding(string address, string type)
    {
        char tag = GetTypeTag(type);
        if (tag == '\0' || !address.StartsWith(AvatarPrefix, StringComparison.Ordinal) || !IsLiteralOscAddress(address))
            return null;

        string parameter = address[AvatarPrefix.Length..];
        int lastSlash = parameter.LastIndexOf('/');
        string name = parameter[(lastSlash + 1)..];
        string prefix = parameter[..(lastSlash + 1)];

        if (IsTrackingToggle(name))
        {
            return tag == 'T' && (prefix.Length == 0 || prefix == "v2/" || prefix.EndsWith("/v2/", StringComparison.Ordinal))
                ? new Binding(address, tag, new Expression(_ => null), parameter, 0, false)
                : null;
        }

        if (prefix.Length != 0 && prefix != "v2/" && !prefix.EndsWith("/v2/", StringComparison.Ordinal))
            return null;

        if (Expressions.TryGetValue(name, out Expression? expression))
            return new Binding(address, tag, expression, parameter, 0, false);

        if (tag != 'T')
            return null;

        const string negative = "Negative";
        if (name.EndsWith(negative, StringComparison.Ordinal) && Expressions.TryGetValue(name[..^negative.Length], out expression))
            return new Binding(address, tag, expression, parameter[..^negative.Length], 0, true);

        int digit = name.Length;
        while (digit > 0 && char.IsAsciiDigit(name[digit - 1]))
            digit--;

        if (digit == name.Length || name[digit] == '0' ||
            !int.TryParse(name.AsSpan(digit), NumberStyles.None, CultureInfo.InvariantCulture, out int bit) ||
            bit <= 0 || (bit & (bit - 1)) != 0 || !Expressions.TryGetValue(name[..digit], out expression))
            return null;

        return new Binding(address, tag, expression, prefix + name[..digit], bit, false);
    }

    private static bool IsLiteralOscAddress(string address)
    {
        if (address.Length > 512 || address.Contains("//", StringComparison.Ordinal))
            return false;

        foreach (char ch in address)
        {
            if (ch <= 32 || ch > 126 || ch is '#' or '*' or ',' or '?' or '[' or ']' or '{' or '}')
                return false;
        }
        return true;
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsTrackingToggle(string name) => name is "EyeTrackingActive" or "LipTrackingActive" or "ExpressionTrackingActive";

    private static bool IsGazeName(string name) => name is "EyeX" or "EyeY" or "EyeLeftX" or "EyeLeftY" or "EyeRightX" or "EyeRightY" ||
        name.StartsWith("EyeLook", StringComparison.Ordinal);

    private static bool IsBlinkName(string name) => name.StartsWith("EyeOpen", StringComparison.Ordinal) ||
        name.StartsWith("EyeClosed", StringComparison.Ordinal) || name.StartsWith("EyeLid", StringComparison.Ordinal);

    private static char GetTypeTag(string type) => type switch
    {
        "f" or "Float" => 'f',
        "i" or "Int" => 'i',
        "T" or "F" or "Bool" => 'T',
        _ => '\0',
    };

    private float Encode(Binding binding, float value)
    {
        if (binding.Sign)
            return value < 0 ? 1 : 0;

        if (binding.Bit != 0)
        {
            if (value < 0 && !_signedGroups.Contains(binding.Group))
                return 0;

            // VRCFT uses 2^N steps for N bits and saturates at full strength.
            int mask = _binaryMasks[binding.Group];
            long quantized = Math.Abs(value) >= .99999f ? mask : (long)(Math.Abs(value) * (mask + 1d));
            return (quantized & binding.Bit) != 0 ? 1 : 0;
        }

        return binding.OscType switch
        {
            'T' => value >= .5f ? 1 : 0,
            'i' => MathF.Round(value, MidpointRounding.AwayFromZero),
            _ => value,
        };
    }

    private void Write(Binding binding, float value, bool force)
    {
        if (!force && binding.Last is float last && Math.Abs(value - last) < MinimumChange)
            return;

        SendValue(binding.Address, binding.OscType, value);
        binding.Last = value;
    }

    private void Release(Binding binding)
    {
        if (binding.Last.HasValue)
        {
            Write(binding, Encode(binding, binding.Expression.Neutral), force: true);
            binding.Last = null;
        }
    }

    private void SendValue(string address, char type, float value)
    {
        Span<byte> packet = stackalloc byte[PaddedLength(address.Length + 1) + 8];
        char tag = type == 'T' && value == 0 ? 'F' : type;
        int header = WriteHeader(packet, address, stackalloc[] { tag });
        if (type != 'T')
        {
            int encoded = type == 'i' ? (int)value : BitConverter.SingleToInt32Bits(value);
            BinaryPrimitives.WriteInt32BigEndian(packet[header..], encoded);
            header += sizeof(int);
        }
        _socket.SendTo(packet[..header], SocketFlags.None, _endpoint);
    }

    private void SendFloats(string address, ReadOnlySpan<float> values)
    {
        Span<char> tags = stackalloc char[values.Length];
        tags.Fill('f');
        Span<byte> packet = stackalloc byte[PaddedLength(address.Length + 1) + PaddedLength(tags.Length + 2) + sizeof(float) * values.Length];
        int offset = WriteHeader(packet, address, tags);
        foreach (float value in values)
        {
            BinaryPrimitives.WriteSingleBigEndian(packet[offset..], value);
            offset += sizeof(float);
        }
        _socket.SendTo(packet, SocketFlags.None, _endpoint);
    }

    private static int PaddedLength(int length) => (length + 3) & ~3;

    private static int WriteHeader(Span<byte> packet, string address, ReadOnlySpan<char> tags)
    {
        int addressBytes = PaddedLength(address.Length + 1);
        int header = addressBytes + PaddedLength(tags.Length + 2);
        packet[..header].Clear();
        Encoding.ASCII.GetBytes(address, packet);
        packet[addressBytes] = (byte)',';
        Encoding.ASCII.GetBytes(tags, packet[(addressBytes + 1)..]);
        return header;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        HasActiveOutput = false;
        _socket.Dispose();
    }
}
