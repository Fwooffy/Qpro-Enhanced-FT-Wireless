using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal enum ActivitySeverity { Normal, Warning, Error }

/// <summary>Classifies raw process output without changing the text retained in Activity.</summary>
internal sealed class HubActivityClassifier
{
    private enum Continuation { Python, PowerShell }
    private readonly Dictionary<string, Continuation> _continuations = new(StringComparer.Ordinal);
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    internal void Reset() => _continuations.Clear();

    internal ActivitySeverity Classify(string line)
    {
        var source = string.Empty;
        var payload = line ?? string.Empty;
        var prefix = Regex.Match(payload, @"^\[(?<source>[^\]\r\n]+)\](?: |$)");
        if (prefix.Success && !IsLevel(prefix.Groups["source"].Value))
        {
            source = prefix.Groups["source"].Value;
            // Remove only the separator: indentation belongs to the original process output.
            payload = payload[prefix.Length..];
        }
        var text = payload.Trim();
        if (text.Length == 0) return ActivitySeverity.Normal;

        var exit = Regex.Match(text, @"^(?:(?<name>.+?)\s+)?(?:finished|exited)\s+with\s+(?:exit\s+)?code\s*(?<code>[+-]?\d+)(?=$|[\s.,;])", IgnoreCase);
        if (exit.Success)
        {
            _continuations.Remove(source);
            // Setup completion summaries are unprefixed but refer to a prefixed process.
            if (exit.Groups["name"].Success) _continuations.Remove(exit.Groups["name"].Value);
            return exit.Groups["code"].Value.TrimStart('+', '-').All(c => c == '0')
                ? ActivitySeverity.Normal : ActivitySeverity.Error;
        }
        var start = Regex.Match(text, @"^Starting (?<name>.+?)(?:…|\.{3}|\.)?$", IgnoreCase);
        if (start.Success)
        {
            _continuations.Remove(source);
            _continuations.Remove(start.Groups["name"].Value);
        }

        if (Regex.IsMatch(text, @"^(?:\+\s*)?(?:CategoryInfo|FullyQualifiedErrorId)\s*:", IgnoreCase) ||
            Regex.IsMatch(text, @"^(?:.+\.ps1|&|[A-Za-z]+-[A-Za-z]+)\s+:\s+", IgnoreCase) ||
            Regex.IsMatch(text, @"^.+\s+:\s+Traceback \(most recent call last\):$", IgnoreCase) ||
            Regex.IsMatch(text, @"^At .+:(?:line|char):\d+", IgnoreCase))
        {
            _continuations[source] = Continuation.PowerShell;
            return ActivitySeverity.Error;
        }
        if (Regex.IsMatch(text, @"^(?:\+\s*)?(?:Exception Group )?Traceback \(most recent call last\):$", IgnoreCase) ||
            Regex.IsMatch(text, @"^(?:During handling of the above exception, another exception occurred:|The above exception was the direct cause of the following exception:)$"))
        {
            _continuations[source] = Continuation.Python;
            return ActivitySeverity.Error;
        }
        if (Regex.IsMatch(text, @"^(?:(?:[A-Za-z_]\w*\.)*[A-Z]\w*(?:Error|Exception)|Exception|KeyboardInterrupt|SystemExit|GeneratorExit)(?::|$)") ||
            Regex.IsMatch(text, @"^Unhandled (?:exception|error)\b", IgnoreCase))
        {
            _continuations[source] = Continuation.Python;
            return ActivitySeverity.Error;
        }

        if (_continuations.TryGetValue(source, out var continuation))
        {
            var indented = payload.Length > 0 && char.IsWhiteSpace(payload[0]);
            if (indented || (continuation == Continuation.PowerShell &&
                    Regex.IsMatch(text, @"^(?:\+\s|~+\s*$|Line\s*\||\d+\s*\|)", IgnoreCase)) ||
                (continuation == Continuation.Python &&
                    Regex.IsMatch(text, @"^(?:File ""|[~^]+$|\[Previous line repeated \d+ more times\])")))
                return ActivitySeverity.Error;
            _continuations.Remove(source);
        }

        var level = Regex.Match(text, @"^(?:\[(?<level>ERROR|FATAL|CRITICAL|WARN(?:ING)?|INFO|DEBUG|TRACE)\]|(?<level>ERROR|FATAL|CRITICAL|WARN(?:ING)?)\s*:)(?:\s*|$)", IgnoreCase);
        var message = level.Success ? text[level.Length..] : text;
        var evidence = RemoveNegatedSignals(message);

        if (Regex.IsMatch(evidence, @"^(?:CONTROLLER_ERROR|HANDS_CLEANUP_FAILED)\b") ||
            Regex.IsMatch(evidence, @"^.+\.py:\s+error:\s+", IgnoreCase) ||
            (level.Success && Regex.IsMatch(level.Groups["level"].Value, @"^(?:ERROR|FATAL|CRITICAL)$", IgnoreCase) &&
                !NegatesLevel(message, "errors?|failures?") && evidence.Length > 0))
            return ActivitySeverity.Error;

        // A stated fallback or warning is recoverable, even when it explains an earlier GPU failure.
        if (Regex.IsMatch(evidence, @"\b(?:GPU|CPU)\s+fallback\b|\bfalling back\b|\bGPU\b.*\bfallback\b", IgnoreCase) ||
            (level.Success && Regex.IsMatch(level.Groups["level"].Value, @"^WARN", IgnoreCase) &&
                !NegatesLevel(message, "warnings?") && evidence.Length > 0))
            return ActivitySeverity.Warning;

        if (Regex.IsMatch(evidence, @"\b(?:failed|failure)\b(?!\s+(?:handling|recovery)\b)|\b(?:could not|cannot|unable to)\s+\w+|\b[1-9]\d*\s+(?:errors?|failures?)\b|\b(?:errors?|failures?)\s*[:=]\s*[1-9]\d*(?![\d.])\b", IgnoreCase))
        {
            if (Regex.IsMatch(text, @"^.+\s+:\s+")) _continuations[source] = Continuation.PowerShell;
            return ActivitySeverity.Error;
        }
        if (Regex.IsMatch(evidence, @"\b(?:unsupported|unverified|needs? attention|not supported|not compatible|not verified|not validated|no validated eye[ -]?profile)\b|\b[1-9]\d*\s+warnings?\b", IgnoreCase))
            return ActivitySeverity.Warning;
        return ActivitySeverity.Normal;
    }

    private static bool IsLevel(string text) => Regex.IsMatch(text, @"^(?:ERROR|FATAL|CRITICAL|WARN(?:ING)?|INFO|DEBUG|TRACE)$", IgnoreCase);

    private static bool NegatesLevel(string text, string names) => Regex.IsMatch(text,
        @"\b(?:no|zero|0|without)\s+(?:(?:reported|fatal|critical)\s+)?(?:" + names + @")\b" +
        @"|\b(?:" + names + @")\s*[:=]\s*0\b(?!\.\d)" +
        @"|\b(?:" + names + @")\s+(?:were\s+|was\s+)?not\s+(?:found|reported|detected)\b", IgnoreCase);

    private static string RemoveNegatedSignals(string text)
    {
        // Remove only the negated phrase. A later failure in the same line must still win.
        var clean = Regex.Replace(text,
            @"\b(?:no|zero|0|without)\s+(?:(?:reported|fatal|critical)\s+)?(?:errors?|warnings?|failures?|failed\s+\w+|unsupported\s+\w+|unverified\s+\w+|(?:GPU|CPU)\s+fallbacks?)\b(?:\s+(?:found|reported|detected|occurred))?" +
            @"|\b(?:errors?|warnings?|failures?|failed)\s*[:=]\s*0\b(?!\.\d)" +
            @"|\b(?:errors?|warnings?|failures?)\s+(?:were\s+|was\s+)?not\s+(?:found|reported|detected)\b" +
            @"|\b(?:not|never)\s+(?:failed|unsupported|unverified)\b" +
            @"|\b(?:does not|do not|did not)\s+(?:need|require)\s+attention\b" +
            @"|\b(?:no|without)\s+(?:need for\s+)?attention\b" +
            @"|\b(?:GPU|CPU)\s+fallback\s*(?:[:=]\s*(?:false|0)|(?:is\s+)?(?:disabled|not (?:needed|required|active)))\b", string.Empty, IgnoreCase);
        return clean.Trim(' ', '\t', '.', ',', ';', ':', '!');
    }
}
