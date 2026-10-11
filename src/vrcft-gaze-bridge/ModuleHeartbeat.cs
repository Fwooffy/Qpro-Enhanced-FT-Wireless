using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

[assembly: InternalsVisibleTo("ModuleHeartbeatTests")]

namespace Qpro.GazeBridge;

internal sealed record HeartbeatChannel(string Name, long AgeMs, long TimeoutMs, bool Applied);
internal sealed record ModuleHeartbeatSnapshot(int Version, string Source, string ModuleId, string AssemblySha256,
    int ProcessId, long ProcessStartFileTime, string SessionId, long Sequence, long CapturedTickMs,
    string State, string NativeState, long NativeAgeMs, bool OwnsEyes, bool OwnsFace, HeartbeatChannel[] Channels);

// Updates only replace one pending observation. A slow/unavailable status disk
// never blocks the tracking loop or builds an unbounded queue of old snapshots.
internal sealed class ModuleHeartbeatPublisher
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _source;
    private readonly string _moduleId;
    private readonly string _hash;
    private readonly int _pid;
    private readonly long _processStart;
    private readonly string _session = Guid.NewGuid().ToString("D");
    private readonly Action<string, string> _write;
    private ModuleHeartbeatSnapshot? _pending;
    private Task _writer = Task.CompletedTask;
    private bool _sealed;
    private long _sequence;

    internal ModuleHeartbeatPublisher(string path, bool steamLink, string hash, int pid, long processStart,
        Action<string, string>? write = null)
    {
        _path = path; _source = steamLink ? "SteamLink" : "VirtualDesktop";
        _moduleId = steamLink ? "5d5cb4f9-63d7-4e8f-9802-24a5d78ea6ee" : "d6a8eeb2-3490-4d4f-bec1-9d5909da08ea";
        _hash = hash; _pid = pid; _processStart = processStart; _write = write ?? WriteAtomic;
    }

    internal static ModuleHeartbeatPublisher Create(bool steamLink)
    {
        using var process = Process.GetCurrentProcess();
        using var assembly = File.OpenRead(typeof(TrackingModule).Assembly.Location);
        var source = steamLink ? "SteamLink" : "VirtualDesktop";
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QproFaceTracking", "status", "module-" + source + ".json"), steamLink,
            Convert.ToHexString(SHA256.HashData(assembly)), process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc());
    }

    internal void Publish(long tick, string state, string nativeState, long nativeAge, bool eyes, bool face,
        HeartbeatChannel[] channels)
    {
        lock (_gate)
        {
            if (_sealed) return;
            _pending = new(1, _source, _moduleId, _hash, _pid, _processStart, _session, ++_sequence,
                tick, state, nativeState, nativeAge, eyes, face, channels);
            if (state is "closed" or "failed") _sealed = true;
            if (_writer.IsCompleted) _writer = Task.Run(WritePending);
        }
    }

    internal Task DrainAsync() { lock (_gate) return _writer; }

    private void WritePending()
    {
        while (true)
        {
            ModuleHeartbeatSnapshot? snapshot;
            lock (_gate)
            {
                snapshot = _pending;
                _pending = null;
                // Complete under the same lock used to start a writer, so an
                // observation cannot be stranded in the writer's exit race.
                if (snapshot is null) { _writer = Task.CompletedTask; return; }
            }
            try { _write(_path, JsonSerializer.Serialize(snapshot)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { /* The previous observation expires naturally in the Hub. */ }
        }
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
