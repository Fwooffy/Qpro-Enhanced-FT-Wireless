using QproFaceTracking.Hub;

static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
var alert = new HubTrackingStopAlert();
alert.BeginSession();
for (int i = 0; i < 20; i++)
    Check(!alert.TryNotify(false, false, false), "Transient health or repeated diagnostic error sounded.");
Check(!alert.TryNotify(true, true, false), "Intentional Stop sounded.");
Check(!alert.TryNotify(true, false, true), "Startup failure sounded.");
Check(alert.TryNotify(true, false, false), "Confirmed unexpected worker exit did not sound.");
for (int i = 0; i < 20; i++)
    Check(!alert.TryNotify(true, false, false), "Duplicate event or second worker exit repeated the sound.");
alert.BeginSession();
Check(alert.TryNotify(true, false, false), "A new session did not restore its single alert.");
Console.WriteLine("PASS tracking stop alert: transient health, Stop, startup, duplicate exits and next session; no audio played.");
var folder = Path.Combine(Path.GetTempPath(), "qpro-sound-test-" + Guid.NewGuid().ToString("N"));
var path = Path.Combine(folder, "sound.txt");
try
{
    Check(!HubTrackingSoundPreference.Load(path), "New preferences should be silent.");
    HubTrackingSoundPreference.Save(true, path);
    Check(HubTrackingSoundPreference.Load(path), "Enabled alert was not persisted.");
    HubTrackingSoundPreference.Save(false, path);
    Check(!HubTrackingSoundPreference.Load(path), "Mute was not persisted.");
    File.WriteAllText(path, "invalid");
    Check(!HubTrackingSoundPreference.Load(path), "Invalid preference should be silent.");
}
finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder); }
Console.WriteLine("PASS tracking sound preference: silent default, enabled and muted persistence, invalid fallback.");
