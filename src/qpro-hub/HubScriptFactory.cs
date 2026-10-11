using System.Diagnostics;

namespace QproFaceTracking.Hub;

internal sealed class HubScriptFactory
{
    private readonly string _root;
    private readonly HubEnvironment _environment;

    internal HubScriptFactory(string root, HubEnvironment environment)
    {
        _root = root;
        _environment = environment;
    }

    internal ProcessStartInfo Create(string script, IEnumerable<string> arguments, bool hidden)
    {
        var scriptPath = Path.Combine(_root, script);
        if (!File.Exists(scriptPath)) throw new FileNotFoundException($"The Hub script is missing: {script}", scriptPath);
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = hidden,
        };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }.Concat(arguments))
            info.ArgumentList.Add(value);

        // PC setup has no headset dependency. Avoid probing the existing
        // runtime and ADB configuration before the setup process can report
        // its own progress (especially when the existing runtime is damaged).
        if (!script.Equals("setup-runtime.ps1", StringComparison.OrdinalIgnoreCase) &&
            !script.Equals("runtime-recovery.ps1", StringComparison.OrdinalIgnoreCase))
        {
            var python = _environment.FindPythonRuntime();
            if (python is not null) info.Environment["QPRO_PYTHON"] = python;
            var adb = _environment.FindAdb();
            if (adb is not null) info.Environment["QPRO_ADB"] = adb;
            info.Environment.Remove("ANDROID_SERIAL");
            info.Environment.Remove("QPRO_ADB_TARGET");
            var target = _environment.GetConfiguredAdbTarget();
            if (!string.IsNullOrWhiteSpace(target))
            {
                info.Environment["ANDROID_SERIAL"] = target;
                info.Environment["QPRO_ADB_TARGET"] = target;
            }
        }
        return info;
    }
}
