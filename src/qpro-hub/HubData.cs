using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed record HubDataInteraction(
    Func<string, string, MessageBoxButtons, MessageBoxIcon, MessageBoxDefaultButton, DialogResult> Message,
    Func<string, string, string, string?> Text,
    Func<bool, string?, string?> Package,
    Func<Task<IReadOnlyList<string>>> CapturePrerequisites,
    Func<bool> RuntimeReady,
    Func<string, string, string[], Task<bool>> RunWorkflow);

internal sealed partial class HubForm
{
    private HubDataInteraction? _previewDataInteraction;
    private bool DataActionsAvailable => !_previewOnly || _previewDataInteraction is not null;

    internal void ConfigurePreviewDataInteraction(HubDataInteraction interaction)
    {
        if (!_previewOnly) throw new InvalidOperationException("The test interaction requires an offline preview.");
        _previewDataInteraction = interaction;
        ReloadProfiles();
    }

    private DialogResult DataMessage(string text, string title, MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.Information, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        _previewDataInteraction?.Message(text, title, buttons, icon, defaultButton) ??
        MessageBox.Show(this, text, title, buttons, icon, defaultButton);

    private bool DataRuntimeReady() => _previewDataInteraction?.RuntimeReady() ?? BackendReady();

    private Task<bool> RunDatasetWorkflowAsync(string label, string script, params string[] args) =>
        _previewDataInteraction?.RunWorkflow(label, script, args) ?? RunUtilityAsync(label, script, args);

    private bool DataChangeBlocked()
    {
        if (_closingInProgress) return true;
        if (!_starting && !_stopping && !_trackingCleanupPending && !_gazeRecoveryRunning &&
            !_datasetOperationBusy && !_utilityActionRunning && !_setupActionRunning && !LiveTrackingRunning)
            return false;
        DataMessage("Stop optional tracking and finish any current setup, capture, or training before changing these files.",
            "Files are in use");
        return true;
    }

    private IReadOnlyList<DatasetInfo> FindDatasets(TongueDatasetKind kind, bool requireCompleted, DateTime? newerThan = null)
        => ReadDatasets(kind, requireCompleted, newerThan).Select(entry => entry.Dataset).ToArray();

    private IReadOnlyList<(TongueDatasetKind Kind, DatasetInfo Dataset)> ReadDatasets(
        TongueDatasetKind? requestedKind = null, bool requireCompleted = false, DateTime? newerThan = null)
    {
        var captureDirectories = new List<string> { Path.Combine(_root, "captures") };
        var rootInfo = new DirectoryInfo(_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (rootInfo.Parent is not null && rootInfo.Parent.Name.Equals("dist", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                captureDirectories.AddRange(
                    Directory.GetDirectories(rootInfo.Parent.FullName, "QproFaceTracking-*")
                        .Select(path => Path.Combine(path, "captures")));
            }
            catch { }
        }
        var sessionPaths = captureDirectories
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => Directory.GetFiles(path, "*.qpsession.json"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (sessionPaths.Count == 0) return [];
        var result = new List<(TongueDatasetKind Kind, DatasetInfo Dataset)>();
        foreach (var path in sessionPaths)
        {
            if (newerThan is not null && File.GetLastWriteTimeUtc(path) < newerThan.Value) continue;
            if (ReadDataset(path, requestedKind, requireCompleted) is { } entry) result.Add(entry);
        }
        return result;
    }

    private static (TongueDatasetKind Kind, DatasetInfo Dataset)? ReadDataset(
        string path, TongueDatasetKind? requestedKind = null, bool requireCompleted = false)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (node is null) return null;
            var sessionType = node["sessionType"]?.GetValue<string>() ?? string.Empty;
            var kind = requestedKind ?? Enum.GetValues<TongueDatasetKind>()
                .FirstOrDefault(candidate => HubTongueDatasetKind.MatchesSessionType(candidate, sessionType));
            if (!HubTongueDatasetKind.MatchesSessionType(kind, sessionType)) return null;
            var completed = node["completed"]?.GetValue<bool>() ?? false;
            if (requireCompleted && !completed) return null;
            var capture = Regex.Replace(path, "\\.qpsession\\.json$", ".qpcap", RegexOptions.IgnoreCase);
            if (!File.Exists(capture)) return null;
            var fallback = "Dataset " + Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            var displayName = node["displayName"]?.GetValue<string>()?.Trim();
            var legacyDiagonalOnly = kind == TongueDatasetKind.Focused &&
                HubTongueDatasetKind.IsLegacyDiagonalOnly(node["prompts"] as JsonArray);
            return (kind, new DatasetInfo(path, capture, string.IsNullOrWhiteSpace(displayName) ? fallback : displayName,
                (node["samples"] as JsonArray)?.Count ?? 0, completed, legacyDiagonalOnly));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return null; }
    }

    private static bool HasDatasetLabels(DatasetInfo dataset)
    {
        try { return new FileInfo(Path.ChangeExtension(dataset.CapturePath, ".qplabel.jsonl")).Length > 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private void ReloadDatasetQueues()
    {
        var quickSelection = (_quickDatasets.SelectedItem as DatasetChoice)?.Dataset.SessionPath;
        var focusedSelection = (_focusedDatasets.SelectedItem as DatasetChoice)?.Dataset.SessionPath;
        var fullSelection = (_fullDatasets.SelectedItem as DatasetChoice)?.Dataset.SessionPath;
        var quickRecordedSelection = (_quickRecordedDatasets.SelectedItem as RecordedDatasetChoice)?.Dataset.SessionPath;
        var focusedRecordedSelection = (_focusedRecordedDatasets.SelectedItem as RecordedDatasetChoice)?.Dataset.SessionPath;
        var fullRecordedSelection = (_fullRecordedDatasets.SelectedItem as RecordedDatasetChoice)?.Dataset.SessionPath;
        var trainedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int version in TongueModelVersions())
        {
            if (ReadModelMetadata(version)?["datasetSession"] is JsonValue value &&
                value.TryGetValue<string>(out var session) && !string.IsNullOrWhiteSpace(session))
                trainedSessions.Add(session);
        }

        // Session files can contain thousands of samples. Share one fresh scan
        // across all six lists instead of reading and parsing each file six times.
        var datasets = ReadDatasets();
        IReadOnlyList<DatasetInfo> ForKind(TongueDatasetKind kind) => datasets
            .Where(entry => entry.Kind == kind).Select(entry => entry.Dataset).ToArray();
        var quick = ForKind(TongueDatasetKind.Quick);
        var focused = ForKind(TongueDatasetKind.Focused);
        var full = ForKind(TongueDatasetKind.Full);
        LoadQueue(_quickDatasets, _quickQueueStatus, quick, quickSelection, trainedSessions);
        LoadQueue(_focusedDatasets, _focusedQueueStatus, focused, focusedSelection, trainedSessions);
        LoadQueue(_fullDatasets, _fullQueueStatus, full, fullSelection, trainedSessions);
        LoadRecordedDatasets(_quickRecordedDatasets, quick, quickRecordedSelection, trainedSessions);
        LoadRecordedDatasets(_focusedRecordedDatasets, focused, focusedRecordedSelection, trainedSessions);
        LoadRecordedDatasets(_fullRecordedDatasets, full, fullRecordedSelection, trainedSessions);
    }

    private void LoadRecordedDatasets(ComboBox box, IReadOnlyList<DatasetInfo> datasets, string? previous, HashSet<string> trainedSessions)
    {
        // The training queue also finds older release folders. The delete list is limited to this extracted copy.
        var captures = Path.GetFullPath(Path.Combine(_root, "captures"));
        var all = datasets
            .Where(dataset => string.Equals(Path.GetDirectoryName(Path.GetFullPath(dataset.SessionPath)), captures, StringComparison.OrdinalIgnoreCase));
        box.Items.Clear();
        foreach (var dataset in all)
            box.Items.Add(new RecordedDatasetChoice(dataset, trainedSessions.Contains(Path.GetFileName(dataset.SessionPath))));
        for (var index = 0; index < box.Items.Count; index++)
        {
            if (string.Equals(((RecordedDatasetChoice)box.Items[index]!).Dataset.SessionPath, previous, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                break;
            }
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void DeleteRecordedDataset(TongueDatasetKind kind)
    {
        if (!DataActionsAvailable) return;
        var box = kind switch
        {
            TongueDatasetKind.Quick => _quickRecordedDatasets,
            TongueDatasetKind.Focused => _focusedRecordedDatasets,
            _ => _fullRecordedDatasets,
        };
        if (box.SelectedItem is not RecordedDatasetChoice choice)
        {
            DataMessage("There is no recorded dataset to delete in this folder.", "No dataset selected");
            return;
        }
        if (DataChangeBlocked()) return;
        var dataset = choice.Dataset;
        if (DataMessage(
                $"Permanently delete the recorded dataset “{dataset.DisplayName}”?\n\nThis removes its camera recording, session details, labels, and prepared training cache from this extracted copy. Any trained tongue model stays available.\n\n{dataset.SessionPath}",
                "Delete dataset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            DeleteDatasetFiles(_root, dataset.SessionPath);
            AppendLog($"Deleted {kind.ToString().ToLowerInvariant()} dataset “{dataset.DisplayName}”. Trained models were kept.");
            ReloadDatasetQueues();
        }
        catch (Exception error)
        {
            ReloadDatasetQueues();
            DataMessage(error.Message, "Could not delete dataset", icon: MessageBoxIcon.Warning);
        }
    }

    private static void DeleteDatasetFiles(string root, string sessionPath)
    {
        var captures = Path.GetFullPath(Path.Combine(root, "captures"));
        var session = Path.GetFullPath(sessionPath);
        const string suffix = ".qpsession.json";
        var fileName = Path.GetFileName(session);
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || fileName.Length <= suffix.Length ||
            !string.Equals(Path.GetDirectoryName(session), captures, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only recordings in this Qpro copy's captures folder can be deleted here.");
        if (!Directory.Exists(captures) || (new DirectoryInfo(captures).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The captures folder is missing or points outside this Qpro copy.");
        var stem = fileName[..^suffix.Length];
        var files = new[] { session, Path.Combine(captures, stem + ".qpcap"), Path.Combine(captures, stem + ".qplabel.jsonl") };
        foreach (var path in files.Where(File.Exists))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A recording file is a link; no dataset files were deleted.");
        var training = Path.GetFullPath(Path.Combine(root, "training"));
        if (Directory.Exists(training) && (new DirectoryInfo(training).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The training cache folder points outside this Qpro copy; no dataset files were deleted.");
        var caches = new[] { Path.Combine(training, stem + "-personal-refinement-224px"), Path.Combine(training, stem + "-tongue-stills-224px") };
        foreach (var path in caches.Where(Directory.Exists))
            if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A prepared training cache is a link; no dataset files were deleted.");
        RemoveDataFiles(root, files.Where(File.Exists), caches.Where(Directory.Exists));
    }

    private static void RemoveDataFiles(string root, IEnumerable<string> files, IEnumerable<string> directories)
    {
        var staging = Path.Combine(root, ".qpro-remove-" + Guid.NewGuid().ToString("N"));
        var entries = files.Select(path => (Path: path, Directory: false))
            .Concat(directories.Select(path => (Path: path, Directory: true))).ToArray();
        if (entries.Length == 0) return;
        var moved = new List<(string Original, string Staged, bool Directory)>();
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var entry in entries)
            {
                var destination = Path.Combine(staging, Path.GetFileName(entry.Path));
                Move(entry.Path, destination, entry.Directory);
                moved.Add((entry.Path, destination, entry.Directory));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                foreach (var entry in moved.AsEnumerable().Reverse())
                    Move(entry.Staged, entry.Original, entry.Directory);
                Directory.Delete(staging);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Removal could not finish. Check the retained files in {staging}.",
                    new AggregateException(error, rollbackError));
            }
            throw;
        }
        try { Directory.Delete(staging, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The selected files were removed from the Hub. Windows could not clear the temporary folder {staging}.", error);
        }

        static void Move(string source, string destination, bool directory)
        {
            if (directory) Directory.Move(source, destination);
            else File.Move(source, destination);
        }
    }

    private void LoadQueue(ComboBox box, Label status, IReadOnlyList<DatasetInfo> all, string? previous, HashSet<string> trainedSessions)
    {
        var untrained = all.Where(dataset => dataset.Completed && dataset.SampleCount > 0 &&
            !trainedSessions.Contains(Path.GetFileName(dataset.SessionPath))).ToList();
        var ready = untrained.Where(HasDatasetLabels).ToList();
        // ComboBox.Items.Clear does not raise SelectedIndexChanged when it
        // implicitly discards a selection. Update the bound Train button too.
        box.SelectedIndex = -1;
        box.Items.Clear();
        foreach (var dataset in ready) box.Items.Add(new DatasetChoice(dataset));
        for (var index = 0; index < box.Items.Count; index++)
        {
            if (string.Equals(((DatasetChoice)box.Items[index]!).Dataset.SessionPath, previous, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                break;
            }
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        var incomplete = all.Count(dataset => !dataset.Completed || dataset.SampleCount == 0);
        var missingLabels = untrained.Count - ready.Count;
        status.Text = ready.Count > 0
            ? $"{ready.Count} dataset{(ready.Count == 1 ? string.Empty : "s")} ready to train"
            : missingLabels > 0 ? "Queue empty · native reference labels missing"
            : incomplete > 0 ? $"Queue empty · {incomplete} incomplete" : "Queue empty — record first";
        status.ForeColor = ready.Count > 0 ? Good : Muted;
    }

    private static void SetDatasetDisplayName(string sessionPath, string displayName)
    {
        var node = JsonNode.Parse(File.ReadAllText(sessionPath))?.AsObject()
            ?? throw new InvalidDataException("The dataset session metadata is invalid.");
        node["displayName"] = CleanDisplayName(displayName);
        var temporary = sessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, sessionPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private IEnumerable<int> TongueModelVersions()
    {
        var models = Path.Combine(_root, "models");
        if (!Directory.Exists(models)) yield break;
        foreach (var gate in Directory.GetFiles(models, "qpro-stereo-tongue-v*-gate.pt"))
        {
            var version = VersionFromPath(gate);
            if (version > 0 && File.Exists(Path.Combine(models, $"qpro-stereo-tongue-v{version}-direction.pt")))
                yield return version;
        }
    }

    private string ModelDisplayName(int version)
    {
        var metadata = ReadModelMetadata(version);
        var name = ModelFriendlyName(version, metadata);
        var experimental = IsExperimentalModelMetadata(metadata) ? " · highly experimental" : string.Empty;
        var demo = version == 8 && metadata is null ? " demo" : string.Empty;
        return $"{name}{experimental} · v{version}{demo}";
    }

    private string ModelMetadataPath(int version) => Path.Combine(_root, "models", $"qpro-stereo-tongue-v{version}.metadata.json");

    private JsonObject? ReadModelMetadata(int version)
    {
        try
        {
            var path = ModelMetadataPath(version);
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() : null;
        }
        catch { return null; }
    }

    private static string ModelFriendlyName(int version, JsonObject? metadata)
    {
        if (metadata?["displayName"] is JsonValue value && value.TryGetValue<string>(out var name) &&
            !string.IsNullOrWhiteSpace(name))
            return CleanDisplayName(name);
        return HasMoustacheModelIcon(metadata) ? "Mustachio" : HubModelMetadata.IsDeveloper(metadata)
            ? "Developer tongue model" : version == 8 ? "Developer-trained tongue model" : "Personal tongue model";
    }

    private static bool HasMoustacheModelIcon(JsonObject? metadata) => HubModelMetadata.IsMustachio(metadata);

    private static bool IsExperimentalModelMetadata(JsonObject? metadata) => HubModelMetadata.IsExperimental(metadata);

    private bool IsExperimentalTongueModel(int version) => IsExperimentalModelMetadata(ReadModelMetadata(version));

    private void WriteModelMetadata(int version, string displayName, string? datasetPath, string origin, JsonObject? classification = null)
    {
        var existing = ReadModelMetadata(version);
        var payload = HubModelMetadata.Create(version, CleanDisplayName(displayName),
            datasetPath is null ? null : Path.GetFileName(datasetPath), origin, existing, classification);
        // Experimental status belongs to the model, so changing its friendly
        // name or transferring it cannot silently remove its warning.
        var destination = ModelMetadataPath(version);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void RenameSelectedModel()
    {
        if (!DataActionsAvailable) return;
        if (_modelList.SelectedItem is not FileChoice model) { DataMessage("Select a tongue model first.", "No model selected"); return; }
        if (DataChangeBlocked()) return;
        var version = VersionFromPath(model.Primary);
        var current = ModelFriendlyName(version, ReadModelMetadata(version));
        var name = PromptForText("Rename tongue model", "Choose the friendly name shown in the hub. The model files remain paired and unchanged.", current);
        if (name is null) return;
        var existing = ReadModelMetadata(version);
        var origin = existing?["origin"] is JsonValue originValue && originValue.TryGetValue<string>(out var previousOrigin)
            ? previousOrigin : HubModelMetadata.IsBundledDeveloper(version, existing) ? "bundled developer model" : "renamed local model";
        try
        {
            WriteModelMetadata(version, name, null, origin);
            AppendLog($"Renamed tongue model v{version} to “{name}”.");
            ReloadProfiles();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { DataMessage(error.Message, "Model rename failed", icon: MessageBoxIcon.Warning); }
    }

    private void DeleteSelectedModel()
    {
        if (!DataActionsAvailable) return;
        if (_modelList.SelectedItem is not FileChoice model) { DataMessage("Select a tongue model first.", "No model selected"); return; }
        if (DataChangeBlocked()) return;
        var version = VersionFromPath(model.Primary);
        if (HubModelMetadata.IsBundledDeveloper(version, ReadModelMetadata(version)))
        {
            DataMessage("The bundled developer model stays available. You can export or rename it.", "Bundled model protected");
            return;
        }
        if (DataMessage($"Delete “{model.Label}” from this installation?\n\nThis removes its paired checkpoints and model details.",
            "Delete tongue model", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var models = Path.Combine(_root, "models");
        try
        {
            if ((new DirectoryInfo(models).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The model folder points outside this copy of Qpro Companion.");
            var paths = new[] { model.Primary, model.Secondary!, ModelMetadataPath(version) };
            RemoveDataFiles(_root, paths.Where(File.Exists), []);
            AppendLog($"Deleted tongue model v{version}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { DataMessage(error.Message, "Model delete failed", icon: MessageBoxIcon.Warning); }
        finally { ReloadProfiles(); }
    }

    private void ExportSelectedModel()
    {
        if (!DataActionsAvailable) return;
        if (_modelList.SelectedItem is not FileChoice model) { DataMessage("Select a tongue model first.", "No model selected"); return; }
        var version = VersionFromPath(model.Primary);
        var metadata = ReadModelMetadata(version);
        var displayName = ModelFriendlyName(version, metadata);
        var package = SelectModelPackage(export: true, SafeFileName(displayName) + ".qptonguemodel");
        if (package is null) return;
        try
        {
            HubModelPackage.Export(package, model.Primary, model.Secondary!, version, displayName, metadata);
            AppendLog($"Exported “{model.Label}” to {package}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DataMessage(error.Message, "Model export failed", icon: MessageBoxIcon.Error);
        }
    }

    private void ImportModel()
    {
        if (!DataActionsAvailable) return;
        if (DataChangeBlocked()) return;
        var package = SelectModelPackage(export: false);
        if (package is null) return;
        var fallback = Path.GetFileNameWithoutExtension(package);
        var models = Path.Combine(_root, "models");
        try
        {
            var version = HubModelPackage.NextVersion(models);
            var manifest = HubModelPackage.Import(package, models, version, packageManifest =>
                HubModelMetadata.Create(version,
                    CleanDisplayName(packageManifest["displayName"]?.GetValue<string>() ?? fallback),
                    null, "imported package", null, packageManifest));
            var displayName = manifest["displayName"]?.GetValue<string>() ?? fallback;
            AppendLog($"Imported “{CleanDisplayName(displayName)}” as local model v{version}.");
            ReloadProfiles();
        }
        catch (Exception error)
        {
            DataMessage(error.Message, "Model import failed", icon: MessageBoxIcon.Error);
        }
    }

    private string? SelectModelPackage(bool export, string? suggestedName = null)
    {
        if (_previewDataInteraction is { } interaction) return interaction.Package(export, suggestedName);
        using FileDialog dialog = export
            ? new SaveFileDialog { FileName = suggestedName ?? "tongue-model.qptonguemodel", AddExtension = true, DefaultExt = "qptonguemodel" }
            : new OpenFileDialog { CheckFileExists = true };
        dialog.Title = export ? "Export tongue model" : "Import tongue model";
        dialog.Filter = "Qpro tongue model (*.qptonguemodel)|*.qptonguemodel";
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private string? PromptForText(string title, string prompt, string initial)
    {
        if (_previewDataInteraction is { } interaction)
            return interaction.Text(title, prompt, initial) is { } name ? CleanDisplayName(name) : null;
        using var dialog = new TextPromptDialog(title, prompt, initial, UiFontName, Background, Panel, Raised, Border, Accent);
        return dialog.ShowDialog(this) == DialogResult.OK ? CleanDisplayName(dialog.Value) : null;
    }

    private static string CleanDisplayName(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean.Length switch { 0 => "Unnamed tongue model", > 80 => clean[..80].Trim(), _ => clean };
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(value.Where(character => !invalid.Contains(character) && !char.IsControl(character)).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(clean) ? "tongue-model" : clean;
    }
}
