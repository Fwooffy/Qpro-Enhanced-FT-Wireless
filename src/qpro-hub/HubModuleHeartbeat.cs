using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed record HubHeartbeatChannel(string Name, long AgeMs, long TimeoutMs, bool Applied);
internal sealed record HubModuleHeartbeatSnapshot(string Source, int ProcessId, long ProcessStartFileTime,
    string SessionId, long Sequence, long CapturedTickMs, string State, string NativeState, long NativeAgeMs,
    bool OwnsEyes, bool OwnsFace, HubHeartbeatChannel[] Channels);
internal sealed record HubModuleHeartbeatObservation(HubModuleHeartbeatSnapshot? Snapshot, string Failure)
{
    internal static HubModuleHeartbeatObservation Missing(string message) => new(null, message);
}
internal sealed record HubModuleHeartbeatView(string State, string Summary, string Detail);

internal sealed class HubModuleHeartbeatHistory
{
    private HubModuleHeartbeatSnapshot? _latest;
    internal void Reset() => _latest = null;
    internal HubModuleHeartbeatObservation Accept(HubModuleHeartbeatObservation observation)
    {
        if (observation.Snapshot is not { } next) return observation;
        if (_latest is { } prior && (next.CapturedTickMs < prior.CapturedTickMs ||
            next.SessionId == prior.SessionId && (next.Sequence < prior.Sequence ||
                next.Sequence == prior.Sequence && next.CapturedTickMs != prior.CapturedTickMs)))
            return HubModuleHeartbeatObservation.Missing("The module observation went backwards. Waiting for a fresh heartbeat.");
        _latest = next;
        return observation;
    }
}

// Reads a bounded local observation. Installation alone does not prove that
// this process loaded this DLL, received packets, or wrote VRCFT output slots.
internal static class HubModuleHeartbeat
{
    internal const long LeaseMs = 3500;
    private static readonly Dictionary<string, long> ChannelTimeouts = new(StringComparer.Ordinal)
    { ["gaze-left"] = 250, ["gaze-right"] = 250, ["tongue"] = 300,
      ["pupil-left"] = 350, ["pupil-right"] = 350 };

    internal static string PathFor(bool steamLink) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "status",
        "module-" + (steamLink ? "SteamLink" : "VirtualDesktop") + ".json");

    internal static HubModuleHeartbeatObservation Read(string path, bool steamLink, string installedDll,
        Func<int, long, bool>? processMatches = null)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return HubModuleHeartbeatObservation.Missing("Heartbeat unavailable. Restart VRCFaceTracking after installing the current module.");
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length is <= 0 or > 16384)
                return HubModuleHeartbeatObservation.Missing("Heartbeat file is invalid.");
            using var installed = File.OpenRead(installedDll);
            var hash = Convert.ToHexString(SHA256.HashData(installed));
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[16385];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = input.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > 16384) return HubModuleHeartbeatObservation.Missing("Heartbeat exceeds its size limit.");
            return Parse(Encoding.UTF8.GetString(bytes, 0, length), steamLink, hash, processMatches ?? ProcessMatches);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or System.Security.SecurityException)
        { return HubModuleHeartbeatObservation.Missing("Heartbeat could not be verified: " + error.Message); }
    }

    internal static HubModuleHeartbeatObservation Parse(string json, bool steamLink, string expectedHash,
        Func<int, long, bool> processMatches)
    {
        try
        {
            if (json.Length > 16384) throw new FormatException("Heartbeat exceeds its size limit.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            Unique(root);
            string Text(string key) => root.GetProperty(key).GetString() ?? throw new FormatException("Missing " + key);
            long Number(string key) => root.GetProperty(key).GetInt64();
            var source = steamLink ? "SteamLink" : "VirtualDesktop";
            var id = steamLink ? HubModuleInstallation.SteamLinkId : HubModuleInstallation.VirtualDesktopId;
            if (Number("Version") != 1 || Text("Source") != source || Text("ModuleId") != id ||
                !Text("AssemblySha256").Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                return HubModuleHeartbeatObservation.Missing("Heartbeat does not match the selected source and installed module.");
            var pid = root.GetProperty("ProcessId").GetInt32();
            var start = Number("ProcessStartFileTime");
            var session = Text("SessionId");
            var sequence = Number("Sequence");
            var tick = Number("CapturedTickMs");
            var state = Text("State");
            var native = Text("NativeState");
            var nativeAge = Number("NativeAgeMs");
            if (pid <= 0 || start <= 0 || !Guid.TryParseExact(session, "D", out _) || sequence <= 0 || tick < 0 ||
                state is not ("initializing" or "active" or "inactive" or "failed" or "closed") ||
                native is not ("fresh" or "stale" or "untimed" or "unavailable") || nativeAge < -1 ||
                (steamLink && native == "untimed") || (!steamLink && native is "fresh" or "stale") ||
                (native is "fresh" or "stale" && nativeAge < 0)) throw new FormatException("Invalid heartbeat fields.");
            var channels = root.GetProperty("Channels");
            if (channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() < ChannelTimeouts.Count ||
                channels.GetArrayLength() > ChannelTimeouts.Count + 1)
                throw new FormatException("Invalid heartbeat channels.");
            var values = new List<HubHeartbeatChannel>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channel in channels.EnumerateArray())
            {
                Unique(channel);
                var name = channel.GetProperty("Name").GetString() ?? "";
                if (!names.Add(name)) throw new FormatException("Duplicate heartbeat channel.");
                // A previous module may still publish its retired sixth channel.
                // It cannot establish current receiving, stale output or guidance.
                if (name == "camera-cheeks") continue;
                var age = channel.GetProperty("AgeMs").GetInt64();
                var timeout = channel.GetProperty("TimeoutMs").GetInt64();
                var applied = channel.GetProperty("Applied").GetBoolean();
                if (!ChannelTimeouts.TryGetValue(name, out var expectedTimeout) ||
                    timeout != expectedTimeout || age < -1 || (applied && (age < 0 || age > timeout)))
                    throw new FormatException("Invalid heartbeat channel freshness.");
                values.Add(new(name, age, timeout, applied));
            }
            if (values.Count != ChannelTimeouts.Count) throw new FormatException("Missing heartbeat channel.");
            if (!processMatches(pid, start)) return HubModuleHeartbeatObservation.Missing("Module process is no longer running, or its identity changed.");
            return new(new(source, pid, start, session, sequence, tick, state, native, nativeAge,
                root.GetProperty("OwnsEyes").GetBoolean(), root.GetProperty("OwnsFace").GetBoolean(), values.ToArray()), "");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return HubModuleHeartbeatObservation.Missing("Heartbeat is invalid: " + error.Message); }
    }

    private static void Unique(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Expected heartbeat object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!names.Add(property.Name) || names.Count > 24) throw new FormatException("Duplicate or excessive heartbeat fields.");
    }

    internal static bool ProcessMatches(int pid, long start)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == start;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        { return false; }
    }

    internal static HubModuleHeartbeatView Describe(HubModuleHeartbeatObservation observation, long now,
        bool hostRunning, bool moduleCurrent)
    {
        if (!hostRunning) return new("closed", "Module output: VRCFaceTracking closed", "Open VRCFaceTracking to load the selected module.");
        if (!moduleCurrent) return new("unverified", "Module output: installation needs attention", "Install the current module for the selected source before verifying output.");
        if (observation.Snapshot is not { } data) return new("unverified", "Module output: unverified", observation.Failure);
        var age = now - data.CapturedTickMs;
        if (age < 0 || age > LeaseMs) return new("stale", "Module output: heartbeat stale", "The module observation expired. Check VRCFaceTracking; a running process does not confirm live output.");
        if (data.State is "closed" or "failed") return new(data.State, "Module output: " + data.State, "The selected module stopped or failed to initialize. Check the VRCFaceTracking log.");
        if (data.State != "active") return new("loaded", "Module loaded: " + (data.State == "inactive" ? "inactive" : "initializing"), "The module has not confirmed ownership of active output slots.");
        var applied = data.Channels.Where(channel => ChannelTimeouts.ContainsKey(channel.Name) && channel.Applied && channel.AgeMs >= 0 &&
            channel.AgeMs <= channel.TimeoutMs && age <= channel.TimeoutMs - channel.AgeMs &&
            (channel.Name == "tongue" ? data.OwnsFace : data.OwnsEyes)).Select(channel => channel.Name).ToArray();
        var nativeFresh = data.NativeState == "fresh" && data.NativeAgeMs >= 0 && data.NativeAgeMs <= 500 && age <= 500 - data.NativeAgeMs && (data.OwnsEyes || data.OwnsFace);
        var recent = data.Channels.Where(channel => ChannelTimeouts.ContainsKey(channel.Name) && channel.Applied &&
            (channel.Name == "tongue" ? data.OwnsFace : data.OwnsEyes)).Select(channel => channel.Name).ToArray();
        var detail = $"{data.Source}; observation {age / 1000.0:0.0} s ago; module active; eye slots {(data.OwnsEyes ? "owned" : "not owned")}, face slots {(data.OwnsFace ? "owned" : "not owned")}. ";
        detail += recent.Length > 0 ? "Applied at last observation: " + string.Join(", ", recent) + ". " : "No custom output applied at last observation. ";
        detail += "This verifies local VRCFT slots only; check the avatar separately.";
        var featureNames = applied.Select(name => name.StartsWith("gaze-", StringComparison.Ordinal) ? "gaze"
            : name.StartsWith("pupil-", StringComparison.Ordinal) ? "pupils" : name).Distinct();
        if (applied.Length > 0 || nativeFresh) return new("receiving", "Module receiving: " + (nativeFresh ? "Steam Link" : string.Join(", ", featureNames)), detail);
        // The producer reports once per second. Its last good observation
        // cannot establish current packet age between those reports, and does
        // not establish a stall until a new observation or heartbeat expiry.
        if (recent.Length > 0 || data.NativeState == "fresh")
            return new("observed", $"Module active: output last observed {age / 1000.0:0.0} s ago",
                detail + " Current packet freshness is unavailable between module observations.");
        var expiredChannels = data.Channels.Any(channel => ChannelTimeouts.ContainsKey(channel.Name) && channel.AgeMs > channel.TimeoutMs &&
            (channel.Name == "tongue" ? data.OwnsFace : data.OwnsEyes));
        if (expiredChannels || data.NativeState == "stale")
            return new("stale", "Module active: received output is stale", detail +
                (data.NativeState == "untimed" ? " Native Virtual Desktop source age remains unknown." : " Check the selected input source."));
        if (data.NativeState == "untimed" && (data.OwnsEyes || data.OwnsFace))
            return new("untimed", "Module loaded: native source available · age unknown", detail + " Virtual Desktop shared memory provides no producer timestamp; native freshness is unavailable.");
        return new("waiting", "Module loaded: waiting for fresh output", detail);
    }
}
