using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using Qpro.GazeBridge;

namespace QproFaceTracking.Hub;

internal sealed partial class VrchatFaceSource
{
    private static readonly JsonSerializerOptions LabelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly float[] ZeroPosition = [0, 0, 0];
    private readonly IPEndPoint? _steamLabelTarget;
    private long _nextSteamSchema;
    private long _nextSteamSample;
    private long _steamLabelSequence;
    private long _steamSourceChangeSequence;
    private long _lastSteamChange;
    private float[]? _previousSteamWeights;
    private (bool Lower, bool Upper, bool Tongue, EyeAngles? Gaze) _previousSteamState;
    private int _captureRequest;
    private int _appliedCaptureRequest;
    private long _nextCaptureOpen;

    // Called by the Hub. Only Read() opens, sends or closes the socket. The
    // counter preserves a detach/reattach even when both happen between reads.
    internal void SetSteamCaptureLabels(bool enabled)
    {
        if (!_unadjustedNative || _steamLabelTarget is null) return;
        int request;
        do
        {
            request = Volatile.Read(ref _captureRequest);
            if (((request & 1) != 0) == enabled) return;
        } while (Interlocked.CompareExchange(ref _captureRequest, unchecked(request + 1), request) != request);
    }

    private void ApplySteamCaptureRequest(long now)
    {
        int request = Volatile.Read(ref _captureRequest);
        if (request == _appliedCaptureRequest || now < _nextCaptureOpen) return;
        _steamLabels?.Dispose();
        _steamLabels = null;
        if ((request & 1) != 0)
        {
            try { _steamLabels = new UdpClient(AddressFamily.InterNetwork); }
            catch (SocketException)
            {
                _nextCaptureOpen = now + 1000;
                return;
            }
        }
        _appliedCaptureRequest = request;
        _nextSteamSchema = _nextSteamSample = 0;
        _steamSourceChangeSequence = 0;
        _previousSteamWeights = null;
    }

    // Preserve the native reference consumed by the camera label recorder.
    // Publish before brow, cheek, tongue, pupil and independent gaze adjustments.
    private void PublishSteamLabels(long now, ReadOnlySpan<float> weights,
        bool lower, bool upper, long? tongueAge, EyeAngles? gaze)
    {
        if (_steamLabels is null || _steamLabelTarget is null)
            return;
        long qpc = Stopwatch.GetTimestamp();
        try
        {
            if (qpc >= _nextSteamSchema)
            {
                _nextSteamSchema = qpc + Stopwatch.Frequency * 2;
                SendLabel(new { V = 1, Type = "schema", TrackingSource = "SteamLink", Names = SteamOscSource.ExpressionNames });
            }
            if (qpc < _nextSteamSample)
                return;
            _nextSteamSample = qpc + Stopwatch.Frequency / 60;
            var state = (lower, upper, tongueAge.HasValue, gaze);
            float[] values = weights.ToArray();
            if (_previousSteamWeights is null || !weights.SequenceEqual(_previousSteamWeights) ||
                state != _previousSteamState)
            {
                _lastSteamChange = now;
                ++_steamSourceChangeSequence;
            }
            _previousSteamState = state;
            _previousSteamWeights = values;

            // The label consumer rotates -Z through this XYZW quaternion.
            Quaternion orientation = gaze is EyeAngles native
                ? Quaternion.CreateFromYawPitchRoll(-native.Direction.Yaw, native.Direction.Pitch, 0)
                : Quaternion.Identity;
            float[] xyzw = [orientation.X, orientation.Y, orientation.Z, orientation.W];
            SendLabel(new
            {
                V = 1,
                Type = "sample",
                TrackingSource = "SteamLink",
                Sequence = ++_steamLabelSequence,
                Qpc = qpc,
                QpcFrequency = Stopwatch.Frequency,
                UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SourceChangeSequence = _steamSourceChangeSequence,
                SourceUnchangedMs = (double)(now - _lastSteamChange),
                NativeTongueValid = tongueAge.HasValue,
                NativeTongueAgeMs = tongueAge,
                Values = values,
                FaceFlags = lower ? 1 : 0,
                IsEyeFollowingBlendshapesValid = upper,
                LeftEyeIsValid = gaze.HasValue,
                RightEyeIsValid = gaze.HasValue,
                LeftEyeOrientation = xyzw,
                RightEyeOrientation = xyzw,
                LeftEyePosition = ZeroPosition,
                RightEyePosition = ZeroPosition,
                LeftEyeConfidence = gaze.HasValue ? 1.0f : 0.0f,
                RightEyeConfidence = gaze.HasValue ? 1.0f : 0.0f,
            });
        }
        catch (SocketException)
        {
            // Live tracking does not depend on the local label recorder.
        }
    }

    private void SendLabel<T>(T message)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, LabelJson);
        _steamLabels!.Send(payload, payload.Length, _steamLabelTarget);
    }
}
