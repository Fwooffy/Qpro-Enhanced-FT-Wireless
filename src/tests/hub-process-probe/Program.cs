using QproFaceTracking.Hub;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

foreach (var matchAt in new[] { -1, 0, 2 })
{
    var snapshot = Enumerable.Range(0, 3).Select(id => new Observation(id)).ToArray();
    var inspected = new List<int>();
    var found = HubProcessProbe.Any(snapshot, process =>
    {
        inspected.Add(process.Id);
        return process.Id == matchAt;
    });
    Check(found == (matchAt >= 0), "Process presence changed.");
    Check(inspected.Count == (matchAt >= 0 ? matchAt + 1 : snapshot.Length), "Check did not stop at its first match.");
    Check(snapshot.All(process => process.DisposeCount == 1), "A short-circuited process snapshot retained undisposed observations.");
}

var failed = Enumerable.Range(0, 3).Select(id => new Observation(id)).ToArray();
try
{
    HubProcessProbe.Any(failed, process => throw new IOException("Fixture process lookup failed."));
    throw new InvalidOperationException("Process lookup failure was hidden.");
}
catch (IOException)
{
    Check(failed.All(process => process.DisposeCount == 1), "An exception retained process observations.");
}
Check(!HubProcessProbe.Any(Array.Empty<Observation>(), _ => throw new InvalidOperationException()), "An empty process list was running.");
Console.WriteLine($"PASS: {checks} process snapshot lifetime checks; no external processes started or stopped.");

internal sealed class Observation(int id) : IDisposable
{
    internal int Id { get; } = id;
    internal int DisposeCount { get; private set; }
    public void Dispose() => DisposeCount++;
}
