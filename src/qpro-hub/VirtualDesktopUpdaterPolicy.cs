using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed record VirtualDesktopUpdaterStatus(string Title, string Detail, bool CanPause,
    bool CanRestore, bool IsPaused, string? InstallRoot = null, string? StreamerVersion = null,
    bool RequiresAdmin = false);

internal static class VirtualDesktopUpdaterPolicy
{
    private const string Marker = "QPRO_VD_UPDATER ";

    internal static Task<VirtualDesktopUpdaterStatus> InspectAsync(string runtimeRoot,
        CancellationToken cancellationToken = default) => InspectCoreAsync(runtimeRoot, cancellationToken);

    internal static Task<VirtualDesktopUpdaterStatus> PauseAsync(string runtimeRoot,
        CancellationToken cancellationToken = default) => ChangeAsync(runtimeRoot, "Pause", cancellationToken);

    internal static Task<VirtualDesktopUpdaterStatus> RestoreAsync(string runtimeRoot,
        CancellationToken cancellationToken = default) => ChangeAsync(runtimeRoot, "Restore", cancellationToken);

    private static async Task<VirtualDesktopUpdaterStatus> ChangeAsync(string runtimeRoot, string action,
        CancellationToken cancellationToken)
    {
        var before = await InspectCoreAsync(runtimeRoot, cancellationToken, requireComplete: true);
        if (action == "Pause" && !before.CanPause || action == "Restore" && !before.CanRestore) return before;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            var start = StartInfo(runtimeRoot, action);
            start.ArgumentList.Add("-ExpectedUserSid");
            start.ArgumentList.Add(sid);
            if (before.RequiresAdmin && !elevated)
            {
                start.UseShellExecute = true;
                start.Verb = "runas";
                start.CreateNoWindow = false;
                start.WindowStyle = ProcessWindowStyle.Hidden;
            }
            using var process = await Task.Run(() => Process.Start(start), CancellationToken.None)
                ?? throw new IOException("The updater control did not start.");
            // Once an approved system change starts, cancellation must not leave
            // the UI reporting a guessed result. The helper has bounded actions.
            using var actionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await process.WaitForExitAsync(actionTimeout.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Windows has not finished the updater-control action. It may still be running; check its administrator prompt and do not assume the hold changed.");
            }
            if (process.ExitCode != 0)
            {
                var current = await InspectCoreAsync(runtimeRoot, CancellationToken.None);
                throw new InvalidOperationException($"The control helper stopped with code {process.ExitCode}. {current.Detail}");
            }
            return await InspectCoreAsync(runtimeRoot, CancellationToken.None, requireComplete: true);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return before with { Title = "Administrator approval cancelled", Detail = "No updater setting was changed. " + before.Detail };
        }
    }

    private static async Task<VirtualDesktopUpdaterStatus> InspectCoreAsync(string runtimeRoot,
        CancellationToken cancellationToken, bool requireComplete = false)
    {
        try
        {
            var start = StartInfo(runtimeRoot, "Status");
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            using var process = Process.Start(start) ?? throw new IOException("The updater check did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            string[] lines = (await output).Split('\n').Where(line => line.StartsWith(Marker, StringComparison.Ordinal)).ToArray();
            _ = await errors;
            if (lines.Length != 1) throw new InvalidDataException("The updater check returned no complete result.");
            var result = JsonSerializer.Deserialize<VirtualDesktopUpdaterStatus>(lines[0][Marker.Length..],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result is null) throw new InvalidDataException("The updater result was empty.");
            if (requireComplete && process.ExitCode != 0) throw new IOException(result.Detail);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            if (requireComplete) throw new TimeoutException("Windows did not finish checking the Streamer updater. No result was confirmed.");
            return new("Streamer updater check timed out", "Windows did not finish the check. Try again when any installer or Windows Firewall operation has finished.", false, false, false);
        }
        catch (Exception error) when (!requireComplete)
        {
            return new("Streamer updater check unavailable", error.Message, false, false, false);
        }
    }

    private static ProcessStartInfo StartInfo(string runtimeRoot, string action)
    {
        string script = Path.Combine(Path.GetFullPath(runtimeRoot), "virtual-desktop-updater.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Re-extract this Companion build; the Streamer updater helper is missing.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtimeRoot,
        };
        foreach (string value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action })
            start.ArgumentList.Add(value);
        return start;
    }
}
