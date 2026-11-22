using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using QproFaceTracking.Hub;

internal static class DatasetScanTests
{
    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "qpro-dataset-scan-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "captures"));
        try
        {
            var types = new[] { "lower-face-refinement-v1", "tongue-stereo-arc-v3", "lower-face-stills-v1", "unknown" };
            for (var i = 0; i < 64; i++)
            {
                var stem = Path.Combine(root, "captures", "session-" + i);
                File.WriteAllText(stem + ".qpcap", "fixture");
                File.WriteAllText(stem + ".qpsession.json", JsonSerializer.Serialize(new {
                    sessionType = types[i % types.Length], completed = i % 3 != 0,
                    displayName = "Session " + i, samples = Enumerable.Range(0, 1500).Select(n => new { frame = n, label = "fixture" }).ToArray()
                }));
            }
            File.WriteAllText(Path.Combine(root, "captures", "broken.qpsession.json"), "invalid");
            // No constructor, window, preferences, ADB, or installed runtime needed.
            var form = (HubForm)RuntimeHelpers.GetUninitializedObject(typeof(HubForm));
            typeof(HubForm).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, root);
            var find = typeof(HubForm).GetMethod("FindDatasets", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var read = typeof(HubForm).GetMethod("ReadDatasets", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object[] Find(TongueDatasetKind kind) => ((IEnumerable)find.Invoke(form, [kind, false, null])!).Cast<object>().ToArray();
            object[] Read() => ((IEnumerable)read.Invoke(form, [null, false, null])!).Cast<object>().ToArray();
            var snapshot = Read();
            foreach (var kind in Enum.GetValues<TongueDatasetKind>())
            {
                var old = Find(kind);
                var current = snapshot.Cast<ITuple>().Where(entry => Equals(entry[0], kind)).Select(entry => entry[1]).ToArray();
                if (!old.SequenceEqual(current) || old.Length != 16) throw new Exception("Snapshot changed dataset contents or ordering: " + kind);
            }
            void Baseline() { foreach (var kind in Enum.GetValues<TongueDatasetKind>()) { Find(kind); Find(kind); } }
            void Optimized() { Read(); }
            Baseline(); Optimized();
            var oldTimes = new List<double>(); var newTimes = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                var timer = Stopwatch.StartNew(); Baseline(); oldTimes.Add(timer.Elapsed.TotalMilliseconds);
                timer.Restart(); Optimized(); newTimes.Add(timer.Elapsed.TotalMilliseconds);
            }
            double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
            Console.WriteLine($"PASS: all kinds, unknown/corrupt exclusion, ordering; 64 sessions x 1500 samples, 9 paired warm runs. Six-scan median={Median(oldTimes):F2} ms; shared-scan median={Median(newTimes):F2} ms.");
        }
        finally { Directory.Delete(root, true); }
    }
}
