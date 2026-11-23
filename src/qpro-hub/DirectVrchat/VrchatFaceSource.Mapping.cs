using System.Buffers.Binary;
using Qpro.GazeBridge;

namespace QproFaceTracking.Hub;

internal sealed partial class VrchatFaceSource
{
    // These are the XR_FB-to-Unified Expressions mappings from TrackingModule.
    // Use the expression names directly so no VRCFaceTracking SDK is required.
    private static readonly (int Index, string[] Names)[] MouthShapes =
    [
        (8, ["MouthRaiserLower"]), (9, ["MouthRaiserUpper"]),
        (10, ["MouthDimpleLeft"]), (11, ["MouthDimpleRight"]),
        (24, ["JawOpen"]), (25, ["JawLeft"]), (26, ["JawRight"]), (27, ["JawForward"]),
        (30, ["MouthFrownLeft"]), (31, ["MouthFrownRight"]),
        (34, ["LipFunnelLowerLeft"]), (35, ["LipFunnelUpperLeft"]),
        (36, ["LipFunnelLowerRight"]), (37, ["LipFunnelUpperRight"]),
        (38, ["MouthPressLeft"]), (39, ["MouthPressRight"]),
        (40, ["LipPuckerUpperLeft", "LipPuckerLowerLeft"]),
        (41, ["LipPuckerUpperRight", "LipPuckerLowerRight"]),
        (42, ["MouthStretchLeft"]), (43, ["MouthStretchRight"]),
        (44, ["LipSuckLowerLeft"]), (46, ["LipSuckLowerRight"]),
        (48, ["MouthTightenerLeft"]), (49, ["MouthTightenerRight"]),
        (50, ["MouthClosed"]), (51, ["MouthLowerDownLeft"]), (52, ["MouthLowerDownRight"]),
        (53, ["MouthUpperLeft", "MouthLowerLeft"]), (54, ["MouthUpperRight", "MouthLowerRight"]),
        (55, ["NoseSneerLeft"]), (56, ["NoseSneerRight"])
    ];

    private static readonly string[] TongueShapes =
    [
        "TongueOut", "TongueUp", "TongueDown", "TongueLeft", "TongueRight", "TongueRoll",
        "TongueBendDown", "TongueCurlUp", "TongueSquish", "TongueFlat", "TongueTwistLeft", "TongueTwistRight"
    ];

    private readonly LiveOverlayState _leftGaze = new(250);
    private readonly LiveOverlayState _rightGaze = new(250);
    private readonly LiveOverlayState _tongue = new(300);
    private readonly LiveOverlayState _leftPupil = new(350);
    private readonly LiveOverlayState _rightPupil = new(350);
    private EyeAngles _leftCameraAngles;
    private EyeAngles _rightCameraAngles;
    private readonly float[] _cameraTongue = new float[NativeTongueMapping.SlotCount];
    private float _leftCameraPupil;
    private float _rightCameraPupil;

    private void WriteUpperFace(Dictionary<string, float> output, ReadOnlySpan<float> weights)
    {
        output["EyeSquintLeft"] = weights[28];
        output["EyeSquintRight"] = weights[29];
        output["EyeWideLeft"] = weights[59];
        output["EyeWideRight"] = weights[60];
        EyebrowWeights brows = EyebrowMapping.FromFaceWeights(weights, !RawNative && _adjustments.Brows && _eyebrows.Enabled, _eyebrows.Sensitivity);
        output["BrowLowererLeft"] = output["BrowPinchLeft"] = brows.LowerLeft;
        output["BrowLowererRight"] = output["BrowPinchRight"] = brows.LowerRight;
        output["BrowInnerUpLeft"] = brows.InnerLeft;
        output["BrowInnerUpRight"] = brows.InnerRight;
        output["BrowOuterUpLeft"] = brows.OuterLeft;
        output["BrowOuterUpRight"] = brows.OuterRight;
    }

    private void WriteLowerFace(Dictionary<string, float> output, ReadOnlySpan<float> weights, long now)
    {
        foreach ((int index, string[] names) in MouthShapes)
            foreach (string name in names)
                output[name] = weights[index];

        SmirkWeights smile = RawNative ? new(weights[32], weights[33]) : _smirk.Update(weights, now);
        output["MouthCornerPullLeft"] = output["MouthCornerSlantLeft"] = smile.Left;
        output["MouthCornerPullRight"] = output["MouthCornerSlantRight"] = smile.Right;
        CheekPuffWeights puff = RawNative || !_adjustments.CheekPuff ? CheekPuffMapping.RawFromFaceWeights(weights) :
            _cheekPuff.Update(weights, _cheekPuffMode, now, _cheekCalibration);
        output["CheekPuffLeft"] = puff.Left;
        output["CheekPuffRight"] = puff.Right;
        output["CheekSquintLeft"] = weights[4];
        output["CheekSquintRight"] = weights[5];
        CheekSuckWeights suck = RawNative || !_adjustments.CheekSuck ? CheekSuckMapping.RawFromFaceWeights(weights) :
            _cheekSuck.Update(weights, _cheekSuckMode, now);
        output["CheekSuckLeft"] = suck.Left;
        output["CheekSuckRight"] = suck.Right;

        NativeLipWeights lips = NativeLipMapping.FromFaceWeights(weights, _source);
        output["MouthUpperUpLeft"] = lips.UpperUpLeft;
        output["MouthUpperDeepenLeft"] = lips.UpperDeepenLeft;
        output["MouthUpperUpRight"] = lips.UpperUpRight;
        output["MouthUpperDeepenRight"] = lips.UpperDeepenRight;
        output["LipSuckUpperLeft"] = lips.SuckUpperLeft;
        output["LipSuckUpperRight"] = lips.SuckUpperRight;
    }

    private void WriteTongue(Dictionary<string, float> output, ReadOnlySpan<float> weights, byte faceFlags, bool camera)
    {
        if (camera)
        {
            WriteTongueSlots(output, _cameraTongue);
            return;
        }
        Span<float> nativeSlots = stackalloc float[NativeTongueMapping.SlotCount];
        NativeTongueWeights tongue = NativeTongueMapping.Resolve(
            weights, _source, faceFlags, customFresh: false, customEnabled: false)!.Value;
        NativeTongueMapping.WriteSlots(tongue, nativeSlots);
        WriteTongueSlots(output, nativeSlots);
    }

    private static void WriteTongueSlots(Dictionary<string, float> output, ReadOnlySpan<float> slots)
    {
        for (int index = 0; index < TongueShapes.Length; index++)
            output[TongueShapes[index]] = slots[index];
    }

    private static void WriteEyeGaze(Dictionary<string, float> output, EyeAngles? anatomical, EyeAngles? legacy, bool left)
    {
        if (legacy is EyeAngles gaze)
        {
            output[left ? "EyeLeftX" : "EyeRightX"] = gaze.X;
            output[left ? "EyeLeftY" : "EyeRightY"] = gaze.Y;
        }
        anatomical?.Direction.Write(output, left ? "EyeLeft" : "EyeRight");
    }

    private static float? ResolvePupil(LiveOverlayState overlay, long now, float camera, bool native) =>
        overlay.Resolve(now, native) switch
        {
            OverlayOutput.Custom => Math.Clamp((camera - 2) / 6, 0.0f, 1.0f),
            OverlayOutput.Native => 0.5f,
            _ => null,
        };

    private void ReadCameraPackets(long now)
    {
        foreach (byte[] packet in LocalDatagrams.ReadPending(_gazeSocket))
        {
            if (!IsCameraPacket(packet, 24, "QPGE"u8)) continue;
            EyeAngles? left = CameraAngles(packet, 8, (packet[5] & 1) != 0);
            EyeAngles? right = CameraAngles(packet, 16, (packet[5] & 2) != 0);
            _leftCameraAngles = left.GetValueOrDefault();
            _rightCameraAngles = right.GetValueOrDefault();
            _leftGaze.Receive(now, left.HasValue);
            _rightGaze.Receive(now, right.HasValue);
        }
        foreach (byte[] packet in LocalDatagrams.ReadPending(_tongueSocket))
        {
            if (!IsCameraPacket(packet, 56, "QPTO"u8)) continue;
            bool valid = true;
            for (int index = 0; index < _cameraTongue.Length; index++)
            {
                float value = ReadFloat(packet, 8 + index * sizeof(float));
                valid &= float.IsFinite(value);
                _cameraTongue[index] = Weight(value);
            }
            _tongue.Receive(now, (packet[5] & 1) != 0 && valid);
        }
        foreach (byte[] packet in LocalDatagrams.ReadPending(_pupilSocket))
        {
            if (!IsCameraPacket(packet, 16, "QPDI"u8)) continue;
            _leftCameraPupil = ReadFloat(packet, 8);
            _rightCameraPupil = ReadFloat(packet, 12);
            _leftPupil.Receive(now, (packet[5] & 1) != 0 && _leftCameraPupil is >= 2 and <= 9);
            _rightPupil.Receive(now, (packet[5] & 2) != 0 && _rightCameraPupil is >= 2 and <= 9);
        }
    }

    private static bool IsCameraPacket(ReadOnlySpan<byte> packet, int length, ReadOnlySpan<byte> magic) =>
        packet.Length == length && packet.StartsWith(magic) && packet[4] == 1;

    private static float ReadFloat(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, sizeof(float)));

    private static float Weight(float value) => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;

    private static float Openness(float blink, float tightener) =>
        1.0f - Math.Clamp(blink + blink * tightener, 0.0f, 1.0f);

    private static EyeAngles? CameraAngles(ReadOnlySpan<byte> packet, int offset, bool enabled)
    {
        if (!enabled)
            return null;
        float x = ReadFloat(packet, offset);
        float y = ReadFloat(packet, offset + 4);
        return float.IsFinite(x) && float.IsFinite(y) ? new(x, y, VrchatGazeDirection.FromCamera(x, y)) : null;
    }

    private static EyeAngles? QuaternionToAngles(ReadOnlySpan<byte> data, int offset)
    {
        float x = ReadFloat(data, offset);
        float y = ReadFloat(data, offset + 4);
        float z = ReadFloat(data, offset + 8);
        float w = ReadFloat(data, offset + 12);
        float length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (!float.IsFinite(length) || length <= 1e-6f)
            return null;
        x /= length;
        y /= length;
        z /= length;
        w /= length;
        float gazeX = MathF.Asin(Math.Clamp(2.0f * (x * z - w * y), -1.0f, 1.0f));
        float gazeY = MathF.Atan2(2.0f * (y * z + w * x), w * w - x * x - y * y + z * z);
        return new(gazeX, gazeY, VrchatGazeDirection.FromVirtualDesktop(new(x, y, z, w)));
    }
}
