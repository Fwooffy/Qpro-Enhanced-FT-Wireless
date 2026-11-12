using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

// Copy and Save share this path. Redaction operates on the diagnostic text;
// it never reads the files named in a traceback or attaches model/camera data.
internal static class HubDiagnosticPrivacy
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex ProfileRoot = Pattern(@"\b[A-Z]:[\\/]+Users[\\/]+[^\\/:\r\n""<>|]+");
    private static readonly Regex QuotedIdentifier = Pattern(
        "(?<key>\"(?:serial|deviceSerial|adbTarget|headsetSerial)\"[ \\t]*:[ \\t]*\")(?<value>(?:[^\"\\\\\\r\\n]|\\\\.)*)(?<end>\")");
    private static readonly Regex IdentifierField = Pattern(
        @"(?<key>\b(?:ADB target|(?:headset |device )?serial|ANDROID_SERIAL|QPRO_ADB_TARGET)[ \t]*[:=][ \t]*)(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;}]+)");
    private static readonly Regex AdbSelector = Pattern(@"(?<key>\badb(?:\.exe)?[ \t]+-s[ \t]+)(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;}]+)");
    private static readonly Regex AdbDeviceRow = Pattern(@"\b(?<serial>[A-Z0-9]{8,64})(?<state>[ \t]+(?:device|offline|unauthorized|recovery|sideload)(?=[ \t\r\n]|$))");
    private static readonly Regex Ipv4 = Pattern(@"(?<![\w.])(?<address>\d{1,3}(?:\.\d{1,3}){3})(?<port>:\d{1,5})?(?![\w.])");
    private static readonly Regex Ipv6 = Pattern(@"\[[A-F0-9:.%_-]{3,90}\]|(?<![\w:])[A-F0-9]*:[A-Z0-9:.%_-]{2,90}(?![\w:])");
    private static readonly Regex NetworkContext = Pattern(@"(?:https?://|(?:address|endpoint|host|ip|target|connected to|connect to|wireless quest ready)[\s""':=\[]*)$");

    internal static string Sanitize(string text, string? userProfile = null, params string?[] connectionIdentifiers)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                var root = userProfile.TrimEnd('\\', '/');
                foreach (var spelling in new[] { root.Replace("\\", "\\\\"), root, root.Replace('\\', '/') }.Distinct())
                    text = Pattern(Regex.Escape(spelling) + @"(?=$|[\\/\s""'])").Replace(text, "%USERPROFILE%");
            }
            // Also covers logs pasted from a different PC and JSON-escaped paths.
            text = ProfileRoot.Replace(text, "%USERPROFILE%");
            foreach (var identifier in connectionIdentifiers.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
            {
                // Accept only a connection token, not arbitrary content from a
                // malformed settings file. Boundary checks preserve hashes/builds.
                if (identifier!.Length is < 4 or > 128 || identifier.Any(char.IsWhiteSpace)) continue;
                text = Pattern(@"(?<![\w])" + Regex.Escape(identifier) + @"(?![\w])").Replace(text, "[redacted]");
            }
            text = QuotedIdentifier.Replace(text, match => match.Groups["key"].Value + "[redacted]" + match.Groups["end"].Value);
            text = IdentifierField.Replace(text, match => match.Groups["key"].Value + "[redacted]");
            text = AdbSelector.Replace(text, match => match.Groups["key"].Value + "[redacted]");
            text = AdbDeviceRow.Replace(text, match => match.Groups["serial"].Value.Any(char.IsDigit)
                ? "[redacted]" + match.Groups["state"].Value : match.Value);

            // Handle IPv4-mapped IPv6 before the embedded IPv4 address.
            text = Ipv6.Replace(text, match => IPAddress.TryParse(match.Value.Trim('[', ']'), out var address) && IsLocalAddress(address)
                ? "[redacted]" : match.Value);
            var beforeAddresses = text;
            text = Ipv4.Replace(text, match =>
            {
                if (!IPAddress.TryParse(match.Groups["address"].Value, out var address) || !IsLocalAddress(address)) return match.Value;
                var bytes = address.GetAddressBytes();
                // 10.x/172.x may also be software versions. A port or network
                // field establishes their meaning; keep unlabelled versions.
                var context = beforeAddresses.Substring(Math.Max(0, match.Index - 80), Math.Min(80, match.Index));
                if (!match.Groups["port"].Success && bytes[0] is 10 or 172 && !NetworkContext.IsMatch(context)) return match.Value;
                return "[redacted]" + match.Groups["port"].Value;
            });
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // Never return the original unredacted text when a pathological
            // worker line exceeds the redactor's time budget.
            return "Diagnostics could not be prepared for sharing because a log line was too complex. Review Activity locally and copy only the relevant error after removing personal details.";
        }
    }

    private static Regex Pattern(string value) => new(value, Options, MatchTimeout);

    private static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsLocalAddress(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
              bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc);
    }
}
