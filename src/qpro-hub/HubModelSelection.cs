using System.Text.Json.Nodes;

namespace QproFaceTracking.Hub;

internal sealed record HubModelOption(string GatePath, int Version, JsonObject? Metadata);

internal static class HubModelSelection
{
    internal static string? ChooseGatePath(IEnumerable<HubModelOption> choices, string? previous)
    {
        var models = choices.ToArray();
        // Refresh and restored preferences must keep the wearer's own choice.
        var remembered = models.FirstOrDefault(model =>
            string.Equals(model.GatePath, previous, StringComparison.OrdinalIgnoreCase));
        if (remembered is not null) return remembered.GatePath;
        var developer = models.Where(model => HubModelMetadata.IsBundledDeveloper(model.Version, model.Metadata))
            .OrderByDescending(model => model.Version).FirstOrDefault();
        return developer?.GatePath ?? models.FirstOrDefault()?.GatePath;
    }
}
