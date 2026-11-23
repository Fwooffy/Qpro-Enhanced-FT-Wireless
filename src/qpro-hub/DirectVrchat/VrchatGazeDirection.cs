using System.Numerics;

namespace QproFaceTracking.Hub;

// The direct OSC routes use a unit vector in VRChat's HMD-local axes.
// Keep the producer's historical angle pair for legacy avatar parameters.
internal readonly record struct VrchatGazeDirection(float X, float Y, float Z)
{
    internal static VrchatGazeDirection Neutral => new(0, 0, 1);
    internal float Yaw => MathF.Atan2(X, Z);
    internal float Pitch => MathF.Atan2(Y, MathF.Sqrt(X * X + Z * Z));

    internal static VrchatGazeDirection FromSteamPoint(float x, float y, float z) =>
        Normalize(x, y, -z);

    internal static VrchatGazeDirection FromVirtualDesktop(Quaternion orientation)
    {
        // The OpenXR eye looks along its local -Z. Reflect the HMD-space ray
        // into VRChat's +Z-forward coordinates, as in the calibration reader.
        Vector3 ray = Vector3.Transform(-Vector3.UnitZ, orientation);
        return new(ray.X, ray.Y, -ray.Z);
    }

    internal static VrchatGazeDirection FromCamera(float yaw, float pitch)
    {
        const float maxAngle = MathF.PI / 2;
        yaw = Math.Clamp(yaw, -maxAngle, maxAngle);
        pitch = Math.Clamp(pitch, -maxAngle, maxAngle);
        float cosPitch = MathF.Cos(pitch);
        return new(MathF.Sin(yaw) * cosPitch, MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch);
    }

    internal static VrchatGazeDirection Average(VrchatGazeDirection left, VrchatGazeDirection right) =>
        Normalize(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    internal void Write(IDictionary<string, float> values, string eye)
    {
        var (directionX, directionY, directionZ, _, _) = Keys(eye);
        values[directionX] = X;
        values[directionY] = Y;
        values[directionZ] = Z;
    }

    internal static VrchatGazeDirection? Read(IReadOnlyDictionary<string, float> values, string eye)
    {
        var (directionX, directionY, directionZ, gazeX, gazeY) = Keys(eye);
        if (TryGetFinite(values, directionX, out float x) && TryGetFinite(values, directionY, out float y)
            && TryGetFinite(values, directionZ, out float z))
            return new(x, y, z);

        // An anatomic direction for either eye means this snapshot distinguishes
        // directions from legacy angles. A missing direction is a missing eye.
        if (values.ContainsKey("EyeLeftDirectionX") || values.ContainsKey("EyeRightDirectionX"))
            return null;
        return TryGetFinite(values, gazeX, out x) && TryGetFinite(values, gazeY, out y)
            ? FromCamera(x, y)
            : null;
    }

    private static (string DirectionX, string DirectionY, string DirectionZ, string GazeX, string GazeY) Keys(string eye) =>
        eye switch
        {
            "EyeLeft" => ("EyeLeftDirectionX", "EyeLeftDirectionY", "EyeLeftDirectionZ", "EyeLeftX", "EyeLeftY"),
            "EyeRight" => ("EyeRightDirectionX", "EyeRightDirectionY", "EyeRightDirectionZ", "EyeRightX", "EyeRightY"),
            _ => throw new ArgumentOutOfRangeException(nameof(eye)),
        };

    private static bool TryGetFinite(IReadOnlyDictionary<string, float> values, string key, out float value) =>
        values.TryGetValue(key, out value) && float.IsFinite(value);

    private static VrchatGazeDirection Normalize(float x, float y, float z)
    {
        // Scale before normalizing so very small or large input vectors remain usable.
        float largest = MathF.Max(MathF.Abs(x), MathF.Max(MathF.Abs(y), MathF.Abs(z)));
        if (largest <= 0 || !float.IsFinite(largest))
            return Neutral;
        var direction = Vector3.Normalize(new Vector3(x / largest, y / largest, z / largest));
        return new(direction.X, direction.Y, direction.Z);
    }
}
