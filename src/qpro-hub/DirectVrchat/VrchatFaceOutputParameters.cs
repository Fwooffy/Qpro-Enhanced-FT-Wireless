namespace QproFaceTracking.Hub;

internal sealed partial class VrchatFaceOutput
{
    private delegate float? Sample(IReadOnlyDictionary<string, float> values);

    private sealed record Expression(Sample Read, float Neutral = 0, bool Eye = false);

    // Wire order from https://creators.vrchat.com/avatars/face-tracking/osc/.
    // The first fourteen slots are derived from gaze, openness and pupil samples.
    private static readonly string[] NativeShapes =
    [
        "EyeSquintRight", "EyeSquintLeft", "EyeWideRight", "EyeWideLeft",
        "BrowPinchRight", "BrowPinchLeft", "BrowLowererRight", "BrowLowererLeft",
        "BrowInnerUpRight", "BrowInnerUpLeft", "BrowOuterUpRight", "BrowOuterUpLeft",
        "NasalDilationRight", "NasalDilationLeft", "NasalConstrictRight", "NasalConstrictLeft",
        "CheekSquintRight", "CheekSquintLeft", "CheekPuffRight", "CheekPuffLeft", "CheekSuckRight", "CheekSuckLeft",
        "JawOpen", "JawRight", "JawLeft", "JawForward", "JawBackward", "JawClench", "JawMandibleRaise", "MouthClosed",
        "LipSuckUpperRight", "LipSuckUpperLeft", "LipSuckLowerRight", "LipSuckLowerLeft", "LipSuckCornerRight", "LipSuckCornerLeft",
        "LipFunnelUpperRight", "LipFunnelUpperLeft", "LipFunnelLowerRight", "LipFunnelLowerLeft",
        "LipPuckerUpperRight", "LipPuckerUpperLeft", "LipPuckerLowerRight", "LipPuckerLowerLeft",
        "MouthUpperUpRight", "MouthUpperUpLeft", "MouthUpperDeepenRight", "MouthUpperDeepenLeft", "NoseSneerRight", "NoseSneerLeft",
        "MouthLowerDownRight", "MouthLowerDownLeft", "MouthUpperRight", "MouthUpperLeft", "MouthLowerRight", "MouthLowerLeft",
        "MouthCornerPullRight", "MouthCornerPullLeft", "MouthCornerSlantRight", "MouthCornerSlantLeft",
        "MouthFrownRight", "MouthFrownLeft", "MouthStretchRight", "MouthStretchLeft", "MouthDimpleRight", "MouthDimpleLeft",
        "MouthRaiserUpper", "MouthRaiserLower", "MouthPressRight", "MouthPressLeft", "MouthTightenerRight", "MouthTightenerLeft",
        "TongueOut", "TongueUp", "TongueDown", "TongueRight", "TongueLeft", "TongueRoll", "TongueBendDown", "TongueCurlUp",
        "TongueSquish", "TongueFlat", "TongueTwistRight", "TongueTwistLeft", "SoftPalateClose", "ThroatSwallow", "NeckFlexRight", "NeckFlexLeft",
    ];

    private static readonly IReadOnlyDictionary<string, Expression> Expressions = CreateExpressions();

    private static Dictionary<string, Expression> CreateExpressions()
    {
        var expressions = new Dictionary<string, Expression>(StringComparer.Ordinal);
        foreach (string shape in NativeShapes)
            expressions.Add(shape, new Expression(values => Strength(values, shape), Eye: shape.StartsWith("Eye", StringComparison.Ordinal)));

        void Add(string name, Sample read, float neutral = 0, bool eye = false) =>
            expressions.Add(name, new Expression(read, neutral, eye));

        void AveragePair(string name) =>
            Add(name, values => Average(Strength(values, name + "Left"), Strength(values, name + "Right")));

        void Axis(string name, string positive, string negative) =>
            Add(name, values => Difference(Strength(values, positive), Strength(values, negative)));

        float? Read(IReadOnlyDictionary<string, float> values, string name) => expressions[name].Read(values);

        foreach (string side in new[] { "Left", "Right" })
        {
            int outward = side == "Right" ? 1 : -1;
            Add("Eye" + side + "X", values => Angle(values, "Eye" + side + "X"), eye: true);
            Add("Eye" + side + "Y", values => Angle(values, "Eye" + side + "Y"), eye: true);
            Add("EyeLookOut" + side, values => Positive(outward * Angle(values, "Eye" + side + "X")), eye: true);
            Add("EyeLookIn" + side, values => Positive(-outward * Angle(values, "Eye" + side + "X")), eye: true);
            Add("EyeLookUp" + side, values => Positive(Angle(values, "Eye" + side + "Y")), eye: true);
            Add("EyeLookDown" + side, values => Positive(-Angle(values, "Eye" + side + "Y")), eye: true);
            Add("EyeOpen" + side, values => Strength(values, "Eye" + side + "Openness"), 1, true);
            Add("EyeClosed" + side, values => 1 - Strength(values, "Eye" + side + "Openness"), eye: true);
            Add("EyeLid" + side, values =>
            {
                float? openness = Read(values, "EyeOpen" + side);
                float? wide = Read(values, "EyeWide" + side);
                return openness.HasValue || wide.HasValue ? .75f * (openness ?? 1) + .25f * (wide ?? 0) : null;
            }, .75f, true);
            // Camera pupil samples are scaled by the producer to the bridge's 0..10 mm range.
            Add("PupilDiameter" + side, values => Strength(values, "Eye" + side + "Pupil"), .5f, true);
            Add("EyeDilation" + side, values => Positive(Strength(values, "Eye" + side + "Pupil") * 2 - 1), eye: true);
            Add("EyeConstrict" + side, values => Positive(1 - Strength(values, "Eye" + side + "Pupil") * 2), eye: true);
            Axis("CheekPuffSuck" + side, "CheekPuff" + side, "CheekSuck" + side);
            Add("BrowUp" + side, values => Combine(Strength(values, "BrowOuterUp" + side), .60f, Strength(values, "BrowInnerUp" + side), .40f));
            Add("BrowDown" + side, values => Combine(Strength(values, "BrowLowerer" + side), .75f, Strength(values, "BrowPinch" + side), .25f));
            Add("BrowExpression" + side, values => Difference(Average(Strength(values, "BrowInnerUp" + side), Strength(values, "BrowOuterUp" + side)), Read(values, "BrowDown" + side)));
            Add("MouthSmile" + side, values => Combine(Strength(values, "MouthCornerPull" + side), .8f, Strength(values, "MouthCornerSlant" + side), .2f));
            Add("MouthSad" + side, values => Maximum(Strength(values, "MouthFrown" + side), Strength(values, "MouthStretch" + side)));
            Add("SmileFrown" + side, values => Difference(Read(values, "MouthSmile" + side), Strength(values, "MouthFrown" + side)));
            Add("SmileSad" + side, values => Difference(Read(values, "MouthSmile" + side), Read(values, "MouthSad" + side)));
            Axis("MouthCornerY" + side, "MouthCornerSlant" + side, "MouthFrown" + side);
            Axis("MouthTightenerStretch" + side, "MouthTightener" + side, "MouthStretch" + side);
            foreach (string lip in new[] { "Upper", "Lower" })
                Axis("LipSuckFunnel" + lip + side, "LipSuck" + lip + side, "LipFunnel" + lip + side);
        }

        Add("EyeX", values => AvailableAverage(Read(values, "EyeLeftX"), Read(values, "EyeRightX")), eye: true);
        Add("EyeY", values => AvailableAverage(Read(values, "EyeLeftY"), Read(values, "EyeRightY")), eye: true);
        Add("EyeOpen", values => AvailableAverage(Read(values, "EyeOpenLeft"), Read(values, "EyeOpenRight")), 1, true);
        Add("EyeClosed", values => 1 - Read(values, "EyeOpen"), eye: true);
        Add("EyeLid", values => AvailableAverage(Read(values, "EyeLidLeft"), Read(values, "EyeLidRight")), .75f, true);
        Add("EyeWide", values => Maximum(Read(values, "EyeWideLeft"), Read(values, "EyeWideRight")), eye: true);
        Add("EyeSquint", values => Maximum(Read(values, "EyeSquintLeft"), Read(values, "EyeSquintRight")), eye: true);
        Add("EyesSquint", values => Read(values, "EyeSquint"), eye: true);
        Add("PupilDiameter", values => AvailableAverage(Read(values, "PupilDiameterLeft"), Read(values, "PupilDiameterRight")), .5f, true);
        Add("PupilDilation", values => Read(values, "PupilDiameter"), .5f, true);

        Axis("JawX", "JawRight", "JawLeft");
        Axis("JawZ", "JawForward", "JawBackward");
        Axis("MouthUpperX", "MouthUpperRight", "MouthUpperLeft");
        Axis("MouthLowerX", "MouthLowerRight", "MouthLowerLeft");
        Axis("TongueX", "TongueRight", "TongueLeft");
        Axis("TongueY", "TongueUp", "TongueDown");
        // Match the VRCFT implementation where the reference table lists these two signs in reverse.
        Axis("TongueArchY", "TongueCurlUp", "TongueBendDown");
        Axis("TongueShape", "TongueFlat", "TongueSquish");

        foreach (string name in new[] { "BrowInnerUp", "BrowOuterUp", "CheekSquint", "CheekPuff", "CheekSuck", "NoseSneer", "MouthUpperUp", "MouthLowerDown", "MouthStretch", "MouthTightener", "MouthPress", "MouthDimple" })
            AveragePair(name);

        foreach (string name in new[] { "BrowUp", "BrowDown", "BrowExpression", "CheekPuffSuck", "MouthSmile", "MouthSad", "MouthCornerY", "MouthTightenerStretch", "SmileFrown", "SmileSad" })
            Add(name, values => Average(Read(values, name + "Left"), Read(values, name + "Right")));

        Add("MouthX", values => Average(Read(values, "MouthUpperX"), Read(values, "MouthLowerX")));
        Add("MouthOpen", values => Average(Read(values, "MouthUpperUp"), Read(values, "MouthLowerDown")));
        foreach (string lip in new[] { "LipSuck", "LipFunnel", "LipPucker" })
        {
            AveragePair(lip + "Upper");
            AveragePair(lip + "Lower");
            Add(lip, values => Average(Read(values, lip + "Upper"), Read(values, lip + "Lower")));
        }

        Add("LipPuckerRight", values => Average(Strength(values, "LipPuckerUpperRight"), Strength(values, "LipPuckerLowerRight")));
        Add("LipPuckerLeft", values => Average(Strength(values, "LipPuckerUpperLeft"), Strength(values, "LipPuckerLowerLeft")));
        foreach (string lip in new[] { "Upper", "Lower" })
            Add("LipSuckFunnel" + lip, values => Difference(Read(values, "LipSuck" + lip), Read(values, "LipFunnel" + lip)));

        return expressions;
    }

    private static float? Finite(IReadOnlyDictionary<string, float> values, string name) =>
        values.TryGetValue(name, out float value) && float.IsFinite(value) ? value : null;

    private static float? Strength(IReadOnlyDictionary<string, float> values, string name) =>
        Finite(values, name) is float value ? Math.Clamp(value, 0, 1) : null;

    private static float? Angle(IReadOnlyDictionary<string, float> values, string name) =>
        Finite(values, name) is float radians ? Math.Clamp(radians, -1, 1) : null;

    private static float? Positive(float? value) => value.HasValue ? Math.Max(0, value.Value) : null;

    private static float? Combine(float? first, float firstWeight, float? second, float secondWeight) =>
        first.HasValue || second.HasValue ? first.GetValueOrDefault() * firstWeight + second.GetValueOrDefault() * secondWeight : null;

    private static float? Difference(float? positive, float? negative) => Combine(positive, 1, negative, -1);

    private static float? Average(float? first, float? second) => Combine(first, .5f, second, .5f);

    private static float? AvailableAverage(float? first, float? second) => first.HasValue && second.HasValue ? Average(first, second) : first ?? second;

    private static float? Maximum(float? first, float? second) => first.HasValue && second.HasValue ? Math.Max(first.Value, second.Value) : first ?? second;
}
