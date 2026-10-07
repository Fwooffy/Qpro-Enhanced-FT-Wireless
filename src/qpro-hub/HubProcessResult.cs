using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace QproFaceTracking.Hub;

internal sealed record HubProcessResult(int? ExitCode, bool TimedOut, string Output, string Error, string? CleanupError)
{
    internal string Diagnostics => string.Join(Environment.NewLine,
        new[] { Output.TrimEnd(), Error.TrimEnd(), CleanupError }.Where(text => !string.IsNullOrWhiteSpace(text)));

    internal static async Task<bool> WaitForOutputDrainAsync(Process process, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); return true; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
    }

    // One-off checks must retain both streams even when their deadline expires.
    // A descendant holding a pipe open also has a bounded drain deadline.
    internal static async Task<HubProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("The helper process could not start.");
        using var reads = new CancellationTokenSource();
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var outputTask = ReadAsync(process.StandardOutput, output, reads.Token);
        var errorTask = ReadAsync(process.StandardError, errors, reads.Token);
        bool timedOut = false;
        string? cleanupError = null;
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                cleanupError = "Helper termination could not be confirmed: " + error.Message;
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException)
            {
                cleanupError = "The helper is still running after its termination request. " + cleanupError;
            }
        }
        finally
        {
            // WaitForExitAsync also waits for redirected event readers, but our
            // stream readers need their own deadline if another process owns a pipe.
            reads.CancelAfter(TimeSpan.FromSeconds(5));
            var complete = await Task.WhenAll(outputTask, errorTask);
            if (complete.Any(value => !value))
                cleanupError = "The helper output did not finish draining; its result remains unverified. " + cleanupError;
        }
        return new(process.HasExited ? process.ExitCode : null, timedOut, output.ToString(), errors.ToString(), cleanupError);
    }

    private static async Task<bool> ReadAsync(StreamReader reader, StringBuilder captured, CancellationToken cancellation)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
                captured.Append(buffer, 0, count);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (IOException error)
        {
            captured.AppendLine("Output read failed: " + error.Message);
            return false;
        }
    }
}
