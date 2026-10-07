using System.Globalization;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed record HubTrainingStage(int Index, int Total, string Name, int Epochs, string Device);
internal sealed record HubTrainingEpoch(int Current, int Total, string Focus);

internal static class HubTrainingProgress
{
    internal static bool TryStage(string line, out HubTrainingStage? stage)
    {
        stage = null;
        var match = Regex.Match(line, @"^TRAIN_STAGE index=(?<index>\d+) total=(?<total>\d+) name=(?<name>\S+) epochs=(?<epochs>\d+) device=(?<device>\S+)(?:\s|$)");
        if (!match.Success || !Positive(match, "index", out var index) || !Positive(match, "total", out var total) ||
            !Positive(match, "epochs", out var epochs) || index > total) return false;
        stage = new(index, total, match.Groups["name"].Value, epochs, match.Groups["device"].Value);
        return true;
    }

    internal static bool TryEpoch(string line, out HubTrainingEpoch? epoch)
    {
        epoch = null;
        var match = Regex.Match(line, @"^TRAIN_EPOCH current=(?<current>\d+) total=(?<total>\d+) focus=(?<focus>\S+)(?:\s|$)");
        if (!match.Success || !int.TryParse(match.Groups["current"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var current) ||
            !Positive(match, "total", out var total) || current > total) return false;
        epoch = new(current, total, match.Groups["focus"].Value);
        return true;
    }

    private static bool Positive(Match match, string group, out int value) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}
