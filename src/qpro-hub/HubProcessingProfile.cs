namespace QproFaceTracking.Hub;

internal sealed record HubProcessingProfile(string Id, string Label, string Guidance)
{
    internal static readonly HubProcessingProfile[] Choices =
    [
        new("responsive", "Responsive (default)", "Tongue and pupils process the newest camera frames at the selected camera FPS cap. This preserves the existing tracking cadence."),
        new("balanced", "Balanced", "Tongue keeps the selected camera cadence. Pupils process up to 12 pairs per second, reducing pupil work with slower pupil response and warm-up."),
        new("low-cpu", "Low CPU", "Tongue keeps the selected camera cadence. Pupils process up to 8 pairs per second, reducing pupil work further with slower pupil response and warm-up.")
    ];

    internal static HubProcessingProfile FromSavedId(string? id) =>
        Choices.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal)) ?? Choices[0];

    public override string ToString() => Label;
}

internal sealed partial class HubForm
{
    private string ProcessingProfileValue() =>
        (_processingProfile.SelectedItem as HubProcessingProfile)?.Id ?? "responsive";

    private void UpdateProcessingProfileNote() =>
        _processingProfileNote.Text =
            (_processingProfile.SelectedItem as HubProcessingProfile ?? HubProcessingProfile.Choices[0]).Guidance;
}
