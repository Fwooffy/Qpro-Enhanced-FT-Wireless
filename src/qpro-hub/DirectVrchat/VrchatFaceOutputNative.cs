using System.Buffers.Binary;

namespace QproFaceTracking.Hub;

internal sealed partial class VrchatFaceOutput
{
    private void SendNativeFace(IReadOnlyDictionary<string, float> values, bool refresh)
    {
        Span<byte> frame = stackalloc byte[NativeKeyCount];
        frame.Clear();

        var rightGaze = VrchatGazeDirection.Read(values, "EyeRight");
        var leftGaze = VrchatGazeDirection.Read(values, "EyeLeft");
        bool hasSample = false;
        hasSample |= WriteGazeWeights(frame[0..4], rightGaze?.X, rightGaze?.Y);
        hasSample |= WriteGazeWeights(frame[4..8], -leftGaze?.X, leftGaze?.Y);
        hasSample |= WriteWeight(frame, 8, 1 - Strength(values, "EyeRightOpenness"));
        hasSample |= WriteWeight(frame, 9, 1 - Strength(values, "EyeLeftOpenness"));

        float? rightPupil = Strength(values, "EyeRightPupil") * 2 - 1;
        float? leftPupil = Strength(values, "EyeLeftPupil") * 2 - 1;
        hasSample |= WriteWeight(frame, 10, rightPupil);
        hasSample |= WriteWeight(frame, 11, leftPupil);
        hasSample |= WriteWeight(frame, 12, -rightPupil);
        hasSample |= WriteWeight(frame, 13, -leftPupil);

        for (int index = 0; index < NativeShapes.Length; index++)
            hasSample |= WriteWeight(frame, index + 14, Strength(values, NativeShapes[index]));

        if (!hasSample)
        {
            if (_nativeFaceActive)
                ReleaseNativeFace();
            return;
        }

        if (!_nativeFaceActive || refresh || !frame.SequenceEqual(_lastNativeFrame))
        {
            SendFaceBlob(frame);
            frame.CopyTo(_lastNativeFrame);
            _nativeFaceActive = true;
        }
    }

    private static bool WriteGazeWeights(Span<byte> frame, float? outward, float? up)
    {
        bool horizontal = WriteWeight(frame, 0, outward) | WriteWeight(frame, 1, -outward);
        bool vertical = WriteWeight(frame, 2, up) | WriteWeight(frame, 3, -up);
        return horizontal || vertical;
    }

    private static bool WriteWeight(Span<byte> frame, int slot, float? value)
    {
        if (!value.HasValue)
            return false;

        frame[slot] = (byte)(Math.Clamp(value.Value, 0, 1) * byte.MaxValue);
        return true;
    }

    private void ReleaseNativeFace()
    {
        Span<byte> neutral = stackalloc byte[NativeKeyCount];
        neutral.Clear();
        SendFaceBlob(neutral);
        _nativeFaceActive = false;
    }

    private void SendFaceBlob(ReadOnlySpan<byte> frame)
    {
        Span<byte> packet = stackalloc byte[PaddedLength(NativeAddress.Length + 1) + 4 + 4 + PaddedLength(NativeKeyCount)];
        packet.Clear();
        int offset = WriteHeader(packet, NativeAddress, "b");
        BinaryPrimitives.WriteInt32BigEndian(packet[offset..], NativeKeyCount);
        frame.CopyTo(packet[(offset + sizeof(int))..]);
        _socket.SendTo(packet, System.Net.Sockets.SocketFlags.None, _endpoint);
    }

    private void SendNativeEyes(IReadOnlyDictionary<string, float> values, bool refresh)
    {
        if (_nativeBlink)
        {
            float? blink = 1 - AvailableAverage(Strength(values, "EyeLeftOpenness"), Strength(values, "EyeRightOpenness"));
            if (blink is float closedAmount)
            {
                if (refresh || !_nativeBlinkActive || Math.Abs(closedAmount - _lastBlink) >= MinimumChange)
                {
                    SendValue(EyePrefix + "EyesClosedAmount", 'f', closedAmount);
                    _lastBlink = closedAmount;
                    _nativeBlinkActive = true;
                }
            }
            else
            {
                ReleaseNativeBlink();
            }
        }

        if (_nativeGaze is null)
            return;

        var left = VrchatGazeDirection.Read(values, "EyeLeft");
        var right = VrchatGazeDirection.Read(values, "EyeRight");
        if (left is null && right is null)
        {
            ReleaseNativeGaze();
            return;
        }

        Span<float> frame = stackalloc float[6];
        int length = FillNativeGaze(frame, left ?? right!.Value, right ?? left!.Value);
        ReadOnlySpan<float> gaze = frame[..length];

        if (refresh || !_nativeGazeActive || !gaze.SequenceEqual(_lastGaze.AsSpan(0, gaze.Length)))
        {
            SendFloats(_nativeGaze, gaze);
            gaze.CopyTo(_lastGaze);
            _nativeGazeActive = true;
        }
    }

    private int FillNativeGaze(Span<float> frame, VrchatGazeDirection left, VrchatGazeDirection right)
    {
        const float degreesPerRadian = 180 / MathF.PI;
        var center = VrchatGazeDirection.Average(left, right);

        // VRChat's OSC pitch is positive down, while Qpro gaze Y is positive up.
        switch (_nativeGaze)
        {
            case EyePrefix + "LeftRightPitchYaw":
                frame[0] = -left.Pitch * degreesPerRadian;
                frame[1] = left.Yaw * degreesPerRadian;
                frame[2] = -right.Pitch * degreesPerRadian;
                frame[3] = right.Yaw * degreesPerRadian;
                return 4;
            case EyePrefix + "CenterPitchYaw":
                frame[0] = -center.Pitch * degreesPerRadian;
                frame[1] = center.Yaw * degreesPerRadian;
                return 2;
            case EyePrefix + "LeftRightVec":
                WriteGazeVector(frame[..3], left);
                WriteGazeVector(frame[3..], right);
                return 6;
            default:
                WriteGazeVector(frame, center);
                return 3;
        }
    }

    private static void WriteGazeVector(Span<float> xyz, VrchatGazeDirection direction)
    {
        xyz[0] = direction.X;
        xyz[1] = direction.Y;
        xyz[2] = direction.Z;
    }

    private void ReleaseNativeEyes()
    {
        ReleaseNativeBlink();
        ReleaseNativeGaze();
    }

    private void ReleaseNativeBlink()
    {
        if (_nativeBlinkActive)
        {
            SendValue(EyePrefix + "EyesClosedAmount", 'f', 0);
            _nativeBlinkActive = false;
        }
    }

    private void ReleaseNativeGaze()
    {
        if (_nativeGazeActive && _nativeGaze is string address)
        {
            Span<float> neutral = stackalloc float[6];
            int length = FillNativeGaze(neutral, VrchatGazeDirection.Neutral, VrchatGazeDirection.Neutral);
            SendFloats(address, neutral[..length]);
            _nativeGazeActive = false;
        }
    }
}
