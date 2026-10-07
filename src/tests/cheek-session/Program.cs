using System.Diagnostics;
using Qpro.Shared;

int checks = 0;
try
{
    if (args.Length == 2 && args[0] == "--owner")
    {
        // The parent terminates only this isolated test child to prove that a
        // crashed Companion cannot leave a persistent cheek-override lease.
        using var owner = new CheekTrackingSession(args[1]);
        Console.WriteLine("OWNER_READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return;
    }

    string name = UniqueName();
    Check(!CheekTrackingSession.IsActive(name), "a new process has no session");
    using (var first = new CheekTrackingSession(name))
    {
        Check(CheekTrackingSession.IsActive(name), "Start publishes session ownership");
        for (int i = 0; i < 25; i++) Check(CheekTrackingSession.IsActive(name), "readers observe the same active session");
        using (var second = new CheekTrackingSession(name))
        {
            first.Dispose();
            Check(CheekTrackingSession.IsActive(name), "one owner closing keeps another owner's session");
        }
        Check(!CheekTrackingSession.IsActive(name), "last owner closing releases session");
        first.Dispose();
        Check(!CheekTrackingSession.IsActive(name), "repeated Stop is harmless");
    }
    using (var reopened = new CheekTrackingSession(name)) Check(CheekTrackingSession.IsActive(name), "reopening creates a new session");
    Check(!CheekTrackingSession.IsActive(name), "reopened session also releases cleanly");

    string unsetName = UniqueName();
    using (var unset = new EventWaitHandle(false, EventResetMode.ManualReset, unsetName))
        Check(!CheekTrackingSession.IsActive(unsetName), "an unsignalled object does not grant override permission");

    string crashedName = UniqueName();
    using (Process child = await StartOwner(crashedName))
    {
        Check(CheekTrackingSession.IsActive(crashedName), "ownership is visible across processes");
        for (int i = 0; i < 25; i++) Check(CheekTrackingSession.IsActive(crashedName), "cross-process probes release reader handles");
        StopChild(child);
    }
    Check(!CheekTrackingSession.IsActive(crashedName), "process death releases ownership without cleanup code");

    string sharedName = UniqueName();
    using (var parentOwner = new CheekTrackingSession(sharedName))
    {
        using Process child = await StartOwner(sharedName);
        Check(CheekTrackingSession.IsActive(sharedName), "parent and child can own a session together");
        StopChild(child);
        Check(CheekTrackingSession.IsActive(sharedName), "one crashing owner does not stop another active owner");
    }
    Check(!CheekTrackingSession.IsActive(sharedName), "all cross-process owners released");
    Console.WriteLine($"PASS: {checks} cheek session lifecycle checks, including isolated child-process termination.");
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL after {checks} checks: {error}");
    Environment.ExitCode = 1;
}

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
}

string UniqueName() => @"Local\QproCheekSessionRegression." + Guid.NewGuid().ToString("N");

async Task<Process> StartOwner(string name)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("--owner");
    start.ArgumentList.Add(name);
    Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated session owner.");
    try
    {
        string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (ready != "OWNER_READY") throw new InvalidOperationException("Isolated session owner was not ready: " + ready);
        return child;
    }
    catch
    {
        StopChild(child);
        child.Dispose();
        throw;
    }
}

void StopChild(Process child)
{
    if (!child.HasExited) child.Kill(entireProcessTree: true);
    if (!child.WaitForExit(5000)) throw new TimeoutException("Isolated session owner did not stop.");
}
