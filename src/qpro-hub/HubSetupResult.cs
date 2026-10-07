using System.Text.Json;

namespace QproFaceTracking.Hub;

// Setup helpers must report their completed action. A clean process exit alone
// can also mean an early return, so it cannot establish installation readiness.
internal sealed class HubSetupResult
{
    private readonly object _sync = new();
    private readonly string _action;
    private int _completed;
    private string? _failure;

    private HubSetupResult(string action) => _action = action;

    internal static HubSetupResult? Create(string script, string[] args)
    {
        if (script.Equals("uninstall-vrcft-eye-bridge.ps1", StringComparison.OrdinalIgnoreCase))
            return new("module-uninstall");
        if (!script.Equals("controller-input.ps1", StringComparison.OrdinalIgnoreCase)) return null;
        var index = Array.FindIndex(args, arg => arg.Equals("-Action", StringComparison.OrdinalIgnoreCase));
        var action = index < 0 ? "install" : index + 1 < args.Length ? args[index + 1].ToLowerInvariant() : "";
        return action is "install" or "uninstall" ? new("controller-" + action) : null;
    }

    internal void Observe(string line)
    {
        var prefix = _action == "module-uninstall" ? "QPRO_MODULE_UNINSTALL " : "CONTROLLER_SETUP ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return;
        lock (_sync)
        {
            try
            {
                if (line.Length > 65536) throw new FormatException();
                using var document = JsonDocument.Parse(line[prefix.Length..], new JsonDocumentOptions { MaxDepth = 8 });
                var result = document.RootElement;
                if (result.ValueKind != JsonValueKind.Object) throw new FormatException();
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (result.EnumerateObject().Any(property => !names.Add(property.Name)) || names.Count > 16)
                    throw new FormatException();
                if (_action == "module-uninstall")
                {
                    _completed++;
                    if (result.GetProperty("removed").GetInt32() < 0 || result.GetProperty("restored").GetInt32() < 0 ||
                        result.GetProperty("remainingQpro").GetInt32() != 0) throw new FormatException();
                    var issues = result.GetProperty("recoveryIssues");
                    if (issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() > 32) throw new FormatException();
                    var messages = issues.EnumerateArray().Select(value => value.GetString()).ToArray();
                    if (messages.Any(message => string.IsNullOrWhiteSpace(message) || message.Length > 4096)) throw new FormatException();
                    if (messages.Length > 0)
                        _failure ??= "The Qpro module was removed, but saved-module recovery needs attention. " + string.Join(" ", messages);
                }
                else
                {
                    var phase = result.GetProperty("phase").GetString();
                    if (string.IsNullOrWhiteSpace(phase) || phase.Length > 64) throw new FormatException();
                    if (phase is "complete" or "uninstalled" or "already-uninstalled")
                    {
                        if (_action == "controller-install" ? phase != "complete" : phase == "complete")
                            throw new FormatException();
                        _completed++;
                    }
                }
                if (_completed > 1) _failure ??= "The setup helper emitted multiple completion reports. Its result is ambiguous.";
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
            {
                _failure ??= "The setup helper emitted an incomplete or malformed result. Check Activity before retrying.";
            }
        }
    }

    internal string? Failure()
    {
        lock (_sync)
            return _failure ?? (_completed == 1 ? null : "The setup helper did not confirm its completed action. Check Activity before retrying.");
    }
}
