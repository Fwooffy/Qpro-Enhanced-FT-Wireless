using System.Diagnostics;
using Qpro.Shared;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

[assembly: SupportedOSPlatform("windows")]

const string MapName = "VirtualDesktop.BodyState";
const int StateBytes = 360;
const int ExpressionOffset = 4;
const int ExpressionCount = 70;

string[] expressionNames =
[
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
];

int port = 27274;
int sampleRate = 60;
for (int index = 0; index < args.Length; ++index)
{
    if (args[index] == "--port" && index + 1 < args.Length)
        port = int.Parse(args[++index]);
    else if (args[index] == "--sample-rate" && index + 1 < args.Length)
        sampleRate = int.Parse(args[++index]);
    else
        throw new ArgumentException("Usage: Qpro.VirtualDesktopLabelBridge [--port 27274] [--sample-rate 10..120]");
}
if (port is < 1024 or > 65535 || sampleRate is < 10 or > 120)
    throw new ArgumentOutOfRangeException("Port or sample rate is outside its supported range");

using UdpClient udp = new(AddressFamily.InterNetwork);
udp.Connect(IPAddress.Loopback, port);
JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    cancellation.Cancel();
};

byte[] first = new byte[StateBytes];
byte[] second = new byte[StateBytes];
byte[] lastSourceState = new byte[StateBytes];
bool haveLastSourceState = false;
long sourceChangeSequence = 0;
long lastSourceChangeQpc = 0;
long sequence = 0;
long schemaDeadline = 0;
long interval = Math.Max(1, Stopwatch.Frequency / sampleRate);
long nextSample = Stopwatch.GetTimestamp();
bool waitingReported = false;

while (!cancellation.IsCancellationRequested)
{
    long now = Stopwatch.GetTimestamp();
    if (now < nextSample)
    {
        int delayMs = (int)Math.Min(
            10,
            Math.Max(1, (nextSample - now) * 1000 / Stopwatch.Frequency));
        cancellation.Token.WaitHandle.WaitOne(delayMs);
        continue;
    }

    try
    {
        if (!VirtualDesktopFaceMap.ReadStable(MapName, first, second))
        {
            AdvanceDeadline(ref nextSample, interval, now);
            continue;
        }
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
    {
        if (!waitingReported)
            Console.WriteLine("WAITING_FOR_VIRTUAL_DESKTOP_BODY_STATE");
        waitingReported = true;
        haveLastSourceState = false;
        sourceChangeSequence = 0;
        schemaDeadline = 0;
        cancellation.Token.WaitHandle.WaitOne(1000);
        nextSample = Stopwatch.GetTimestamp();
        continue;
    }
    if (!haveLastSourceState)
    {
        Console.WriteLine($"VD_LABEL_SOURCE_READY bytes={StateBytes} rate={sampleRate}");
        waitingReported = false;
    }

    if (!haveLastSourceState || !second.AsSpan().SequenceEqual(lastSourceState))
    {
        second.AsSpan().CopyTo(lastSourceState);
        haveLastSourceState = true;
        lastSourceChangeQpc = now;
        ++sourceChangeSequence;
    }
    double sourceUnchangedMs =
        (now - lastSourceChangeQpc) * 1000.0 / Stopwatch.Frequency;

    if (now >= schemaDeadline)
    {
        Send(udp, new { V = 1, Type = "schema", Names = expressionNames,
            TrackingSource = "VirtualDesktop" }, jsonOptions);
        schemaDeadline = now + Stopwatch.Frequency * 2;
    }

    float[] values = new float[ExpressionCount];
    for (int index = 0; index < ExpressionCount; ++index)
        values[index] = BitConverter.ToSingle(second, ExpressionOffset + index * 4);

    Send(
        udp,
        new
        {
            V = 1,
            Type = "sample",
            TrackingSource = "VirtualDesktop",
            Sequence = ++sequence,
            Qpc = now,
            QpcFrequency = Stopwatch.Frequency,
            UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceChangeSequence = sourceChangeSequence,
            SourceUnchangedMs = sourceUnchangedMs,
            Values = values,
            FaceFlags = second[0],
            IsEyeFollowingBlendshapesValid = second[1] != 0,
            LeftEyeIsValid = second[292] != 0,
            RightEyeIsValid = second[293] != 0,
            LeftEyeOrientation = ReadFloats(second, 296, 4),
            LeftEyePosition = ReadFloats(second, 312, 3),
            RightEyeOrientation = ReadFloats(second, 324, 4),
            RightEyePosition = ReadFloats(second, 340, 3),
            LeftEyeConfidence = BitConverter.ToSingle(second, 352),
            RightEyeConfidence = BitConverter.ToSingle(second, 356)
        },
        jsonOptions);

    AdvanceDeadline(ref nextSample, interval, now);
}

static void AdvanceDeadline(ref long deadline, long interval, long now)
{
    do
    {
        deadline += interval;
    } while (deadline <= now);
}

static float[] ReadFloats(byte[] data, int offset, int count)
{
    float[] result = new float[count];
    for (int index = 0; index < count; ++index)
        result[index] = BitConverter.ToSingle(data, offset + index * 4);
    return result;
}

static void Send<T>(UdpClient udp, T message, JsonSerializerOptions options)
{
    byte[] data = JsonSerializer.SerializeToUtf8Bytes(message, options);
    udp.Send(data, data.Length);
}
