using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private string LiveOptionsPath => Path.Combine(_root, "config", "live-options.json");

    private void RestoreLiveOptions()
    {
        if (_previewOnly) return;
        try
        {
            if (!File.Exists(LiveOptionsPath) || new FileInfo(LiveOptionsPath).Length > 16_384) return;
            var options = JsonSerializer.Deserialize<HubLiveOptions>(File.ReadAllText(LiveOptionsPath));
            if (options?.Schema != 1) return;
            ApplyLiveOptions(options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { AppendLog("Could not restore Live tracking choices: " + error.Message); }
    }

    internal void ApplyLiveOptions(HubLiveOptions options)
    {
        if (options.Schema != 1) return;
        RestoreModel(_tongueModels, options.Model);
        RestoreModel(_eyeProfiles, options.EyeProfile);
        _tongue.Checked = options.Tongue;
        _pupil.Checked = options.Pupil;
        _cameraCheekPuff.Checked = options.CameraCheeks;
        _hybridHands.Checked = options.Hands;
        _controllerTouchpad.Checked = options.Touchpad;
        _smoothing.Value = Math.Clamp(options.Smoothing, _smoothing.Minimum, _smoothing.Maximum);
        RestoreChoice(_fps, options.Fps);
        RestoreChoice(_pupilSensitivity, options.PupilSensitivity);
        RestoreChoice(_visibilityMode, options.Visibility);
        RestoreChoice(_touchpadMode, options.TouchpadMode);
        UpdateControlState();
    }

    private void SaveLiveOptions()
    {
        if (_previewOnly) return;
        var options = CaptureLiveOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(LiveOptionsPath)!);
        string temporary = LiveOptionsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(options));
            File.Move(temporary, LiveOptionsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal HubLiveOptions CaptureLiveOptions() => new(1, _tongue.Checked, _pupil.Checked, _cameraCheekPuff.Checked,
            _hybridHands.Checked, _controllerTouchpad.Checked, _smoothing.Value,
            _fps.SelectedItem?.ToString(), _pupilSensitivity.SelectedItem?.ToString(),
            _visibilityMode.SelectedItem?.ToString(), _touchpadMode.SelectedItem?.ToString(),
            Path.GetFileName((_tongueModels.SelectedItem as FileChoice)?.Primary),
            Path.GetFileName((_eyeProfiles.SelectedItem as FileChoice)?.Primary));

    private static void RestoreChoice(ComboBox box, string? text)
    {
        if (text is null || text.Length > 256) return;
        for (int index = 0; index < box.Items.Count; index++)
            if (string.Equals(box.Items[index]?.ToString(), text, StringComparison.Ordinal))
            { box.SelectedIndex = index; return; }
    }

    private static void RestoreModel(ComboBox box, string? name)
    {
        if (name is null || name.Length > 256 || name != Path.GetFileName(name)) return;
        for (int index = 0; index < box.Items.Count; index++)
            if (box.Items[index] is FileChoice choice &&
                string.Equals(Path.GetFileName(choice.Primary), name, StringComparison.OrdinalIgnoreCase))
            { box.SelectedIndex = index; return; }
    }
}

internal sealed record HubLiveOptions(int Schema, bool Tongue, bool Pupil, bool CameraCheeks,
    bool Hands, bool Touchpad, int Smoothing, string? Fps, string? PupilSensitivity,
    string? Visibility, string? TouchpadMode, string? Model, string? EyeProfile);
