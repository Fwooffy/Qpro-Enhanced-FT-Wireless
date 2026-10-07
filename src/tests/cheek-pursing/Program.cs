using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qpro.GazeBridge;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;

int checks = 0;
try
{
    // These are synthetic channel-isolation checks, not a recording of a
    // real pursing false positive. Read only an anonymous test map: never
    // initialize the module, open VirtualDesktop.BodyState, or bind its ports.
    foreach (string style in new[] { "Off", "Balanced", "Strong", "Calibrated" })
    {
        using var fixture = new Fixture(style);
        foreach (int mouthIndex in new[] { 34, 35, 36, 37, 38, 39, 40, 41, 48, 49, 50 })
        {
            float[] weights = new float[70];
            weights[mouthIndex] = .9f;
            fixture.Frame(weights, 100);
            fixture.Frame(weights, 100);
            Near(Shape(UnifiedExpressions.CheekPuffLeft), 0,
                $"{style}: mouth source {mouthIndex} cannot write left cheek");
            Near(Shape(UnifiedExpressions.CheekPuffRight), 0,
                $"{style}: mouth source {mouthIndex} cannot write right cheek");
        }

        // Keep asymmetric, bilateral, partial and low cheek signals unchanged
        // when a pucker is added. This protects puff+pucker combinations from
        // an unconditional pucker veto or a mouth-to-cheek mapping mistake.
        foreach ((float left, float right) in new[]
        {
            (.38f, .27f), (.05f, .10f), (.6f, .6f), (.24f, .24f), (.04f, .04f)
        })
        {
            (float Left, float Right) plain = RunPose(style, left, right, pucker: false);
            (float Left, float Right) puckered = RunPose(style, left, right, pucker: true);
            Near(puckered.Left, plain.Left, $"{style}: pucker preserves left {left}/{right}");
            Near(puckered.Right, plain.Right, $"{style}: pucker preserves right {left}/{right}");
            if (style == "Off")
            {
                Near(puckered.Left, left, "native passthrough preserves analog left");
                Near(puckered.Right, right, "native passthrough preserves analog right");
            }
        }
    }

    (float Left, float Right) strong = RunPose("Strong", .09f, .09f, pucker: true);
    (float Left, float Right) native = RunPose("Off", .09f, .09f, pucker: true);
    Near(strong.Left, 1, "Strong expands a sustained 0.09 cheek input to full left");
    Near(strong.Right, 1, "Strong expands a sustained 0.09 cheek input to full right");
    Near(native.Left, .09f, "native mode retains that small left signal");
    Near(native.Right, .09f, "native mode retains that small right signal");
    Console.WriteLine("Synthetic 0.09/0.09 cheek input: Strong=1/1; native passthrough=0.09/0.09.");
    if (args.Contains("--inspect-saved-preference"))
    {
        using var fixture = new Fixture("Off");
        Console.WriteLine(fixture.InspectSavedPreference());
    }
    Console.WriteLine($"PASS: {checks} cheek/pucker isolation checks against {typeof(TrackingModule).Assembly.Location}");
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL after {checks} checks: {error}");
    Environment.ExitCode = 1;
}

(float Left, float Right) RunPose(string style, float left, float right, bool pucker)
{
    using var fixture = new Fixture(style);
    float[] weights = new float[70];
    weights[2] = left;
    weights[3] = right;
    weights[40] = pucker ? .8f : 0;
    weights[41] = pucker ? .7f : 0;
    fixture.Frame(weights, 0);
    fixture.Frame(weights, 100);
    Near(Shape(UnifiedExpressions.LipPuckerUpperLeft), weights[40], "left pucker maps to its own channel");
    Near(Shape(UnifiedExpressions.LipPuckerLowerLeft), weights[40], "lower left pucker maps to its own channel");
    Near(Shape(UnifiedExpressions.LipPuckerUpperRight), weights[41], "right pucker maps to its own channel");
    Near(Shape(UnifiedExpressions.LipPuckerLowerRight), weights[41], "lower right pucker maps to its own channel");
    return (Shape(UnifiedExpressions.CheekPuffLeft), Shape(UnifiedExpressions.CheekPuffRight));
}

float Shape(UnifiedExpressions expression) => UnifiedTracking.Data.Shapes[(int)expression].Weight;
void Near(float actual, float expected, string label)
{
    if (!float.IsFinite(actual) || MathF.Abs(actual - expected) > .00001f)
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    checks++;
}

sealed class Fixture : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly TrackingModule _module;
    private readonly MemoryMappedViewAccessor _view;
    private long _time = 1000;

    internal Fixture(string style)
    {
        _module = (TrackingModule)Activator.CreateInstance(typeof(TrackingModule), Private,
            binder: null, args: [new Func<long>(() => _time), "QproCheekTest." + Guid.NewGuid().ToString("N")], culture: null)!;
        for (Type? type = _module.GetType(); type is not null; type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (typeof(ILogger).IsAssignableFrom(field.FieldType)) field.SetValue(_module, NullLogger.Instance);
            foreach (PropertyInfo property in type.GetProperties(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (property.CanWrite && typeof(ILogger).IsAssignableFrom(property.PropertyType))
                    property.SetValue(_module, NullLogger.Instance);
        }
        MemoryMappedFile map = MemoryMappedFile.CreateNew(null, 360);
        _view = map.CreateViewAccessor();
        Set("_map", map);
        Set("_view", _view);
        Set("_wasActive", true);
        Set("_needsExpression", true);
        Assembly assembly = typeof(TrackingModule).Assembly;
        Set("_cheekPuffMode", Enum.Parse(assembly.GetType("Qpro.GazeBridge.CheekPuffMode")!, style));
        Set("_cheekSuckMode", Enum.Parse(assembly.GetType("Qpro.GazeBridge.CheekSuckMode")!, "Off"));
        Set("_cheekPuffCalibration", Activator.CreateInstance(assembly.GetType("Qpro.Shared.CheekPuffCalibration")!,
            [.03f, .4f, .02f, .5f]));
        Set("_nextCheekPuffModeCheckTick", long.MaxValue);
        Set("_nextCheekSuckModeCheckTick", long.MaxValue);
        _module.Status = ModuleState.Active;
    }

    internal void Frame(float[] weights, long elapsedMs)
    {
        _time += elapsedMs;
        byte[] bytes = new byte[360];
        bytes[0] = 1;
        for (int index = 0; index < weights.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4 + index * 4, 4), weights[index]);
        _view.WriteArray(0, bytes, 0, bytes.Length);
        _module.Update();
    }

    internal string InspectSavedPreference()
    {
        // Explicit diagnostic option: use the module's own read-only preference
        // refresh, without calling Initialize or opening its live input/output.
        Set("_nextCheekPuffModeCheckTick", 0L);
        typeof(TrackingModule).GetMethod("RefreshCheekPuffMode", Private)!.Invoke(_module, null);
        object? mode = typeof(TrackingModule).GetField("_cheekPuffMode", Private)!.GetValue(_module);
        string path = (string)typeof(TrackingModule).GetField("CheekPuffModePath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        string origin = File.Exists(path) ? "file" : "missing file; module default";
        return $"This process's preference refresh: {mode} ({origin}); resolved path: {path}. A sandbox path is not the user's live settings path.";
    }

    private void Set(string name, object? value) => typeof(TrackingModule).GetField(name, Private)!.SetValue(_module, value);
    public void Dispose() => _module.Teardown();
}
