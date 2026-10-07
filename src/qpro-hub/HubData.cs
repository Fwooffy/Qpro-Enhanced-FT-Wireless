using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private DatasetInfo? FindLatestDataset(TongueDatasetKind kind, bool requireCompleted, DateTime? newerThan = null)
    {
        return FindDatasets(kind, requireCompleted, newerThan).FirstOrDefault();
    }

    private IReadOnlyList<DatasetInfo> FindDatasets(TongueDatasetKind kind, bool requireCompleted, DateTime? newerThan = null)
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
        var result = new List<DatasetInfo>();
        foreach (var path in sessionPaths)
        {
            if (newerThan is not null && File.GetLastWriteTimeUtc(path) < newerThan.Value) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (node is null) continue;
                var sessionType = node["sessionType"]?.GetValue<string>() ?? string.Empty;
                if (!HubTongueDatasetKind.MatchesSessionType(kind, sessionType)) continue;
                var completed = node["completed"]?.GetValue<bool>() ?? false;
                if (requireCompleted && !completed) continue;
                var samples = node["samples"] as JsonArray;
                var capture = Regex.Replace(path, "\\.qpsession\\.json$", ".qpcap", RegexOptions.IgnoreCase);
                if (!File.Exists(capture)) continue;
                var fallback = "Dataset " + Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                var displayName = node["displayName"]?.GetValue<string>()?.Trim();
                var legacyDiagonalOnly = kind == TongueDatasetKind.Focused &&
                    HubTongueDatasetKind.IsLegacyDiagonalOnly(node["prompts"] as JsonArray);
                result.Add(new DatasetInfo(path, capture, string.IsNullOrWhiteSpace(displayName) ? fallback : displayName, samples?.Count ?? 0, completed, legacyDiagonalOnly));
            }
            catch { }
        }
        return result;
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
        var models = Path.Combine(_root, "models");
        if (Directory.Exists(models))
        {
            foreach (var metadata in Directory.GetFiles(models, "qpro-stereo-tongue-v*.metadata.json"))
            {
                try
                {
                    var session = JsonNode.Parse(File.ReadAllText(metadata))?["datasetSession"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(session)) trainedSessions.Add(session);
                }
                catch { }
            }
        }

        LoadQueue(_quickDatasets, _quickQueueStatus, TongueDatasetKind.Quick, quickSelection, trainedSessions);
        LoadQueue(_focusedDatasets, _focusedQueueStatus, TongueDatasetKind.Focused, focusedSelection, trainedSessions);
        LoadQueue(_fullDatasets, _fullQueueStatus, TongueDatasetKind.Full, fullSelection, trainedSessions);
        LoadRecordedDatasets(_quickRecordedDatasets, TongueDatasetKind.Quick, quickRecordedSelection, trainedSessions);
        LoadRecordedDatasets(_focusedRecordedDatasets, TongueDatasetKind.Focused, focusedRecordedSelection, trainedSessions);
        LoadRecordedDatasets(_fullRecordedDatasets, TongueDatasetKind.Full, fullRecordedSelection, trainedSessions);
    }

    private void LoadRecordedDatasets(ComboBox box, TongueDatasetKind kind, string? previous, HashSet<string> trainedSessions)
    {
        // The training queue also finds older release folders. The delete list is limited to this extracted copy.
        var captures = Path.GetFullPath(Path.Combine(_root, "captures"));
        var all = FindDatasets(kind, requireCompleted: false)
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
        var box = kind switch
        {
            TongueDatasetKind.Quick => _quickRecordedDatasets,
            TongueDatasetKind.Focused => _focusedRecordedDatasets,
            _ => _fullRecordedDatasets,
        };
        if (box.SelectedItem is not RecordedDatasetChoice choice)
        {
            MessageBox.Show(this, "There is no recorded dataset to delete in this folder.", "No dataset selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_datasetOperationBusy || LiveTrackingRunning)
        {
            MessageBox.Show(this, "Finish capture or training and stop live tracking before deleting a dataset.", "Dataset is in use", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var dataset = choice.Dataset;
        if (MessageBox.Show(this,
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
            MessageBox.Show(this, error.Message, "Could not delete dataset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        foreach (var path in files.Where(File.Exists)) File.Delete(path);
        foreach (var path in caches.Where(Directory.Exists)) Directory.Delete(path, recursive: true);
    }

    private void LoadQueue(ComboBox box, Label status, TongueDatasetKind kind, string? previous, HashSet<string> trainedSessions)
    {
        var all = FindDatasets(kind, requireCompleted: false).ToList();
        var ready = all.Where(dataset => dataset.Completed && dataset.SampleCount > 0 && !trainedSessions.Contains(Path.GetFileName(dataset.SessionPath))).ToList();
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
        status.Text = ready.Count == 0
            ? (incomplete > 0 ? $"Queue empty · {incomplete} incomplete" : "Queue empty — record first")
            : $"{ready.Count} dataset{(ready.Count == 1 ? string.Empty : "s")} ready to train";
        status.ForeColor = ready.Count > 0 ? Good : Muted;
    }

    private static void SetDatasetDisplayName(string sessionPath, string displayName)
    {
        var node = JsonNode.Parse(File.ReadAllText(sessionPath))?.AsObject()
            ?? throw new InvalidDataException("The dataset session metadata is invalid.");
        node["displayName"] = CleanDisplayName(displayName);
        var temporary = sessionPath + ".tmp";
        File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, sessionPath, true);
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
        return HasMoustacheModelIcon(metadata) ? "Mustachio" : version == 8 ? "Developer-trained tongue model" : "Personal tongue model";
    }

    private static bool HasMoustacheModelIcon(JsonObject? metadata) => HubModelMetadata.IsMustachio(metadata);

    private static bool IsExperimentalModelMetadata(JsonObject? metadata) => HubModelMetadata.IsExperimental(metadata);

    private bool IsExperimentalTongueModel(int version) => IsExperimentalModelMetadata(ReadModelMetadata(version));

    private static void CopyModelClassification(JsonObject? source, JsonObject destination)
    {
        HubModelMetadata.CopyPortableDetails(source, destination);
    }

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
        if (_modelList.SelectedItem is not FileChoice model) { MessageBox.Show(this, "Select a tongue model first."); return; }
        var version = VersionFromPath(model.Primary);
        var current = ModelFriendlyName(version, ReadModelMetadata(version));
        var name = PromptForText("Rename tongue model", "Choose the friendly name shown in the hub. The model files remain paired and unchanged.", current);
        if (name is null) return;
        var existing = ReadModelMetadata(version);
        var origin = existing?["origin"] is JsonValue originValue && originValue.TryGetValue<string>(out var previousOrigin)
            ? previousOrigin : version == 8 ? "bundled developer model" : "renamed local model";
        WriteModelMetadata(version, name, null, origin);
        AppendLog($"Renamed tongue model v{version} to “{name}”.");
        ReloadProfiles();
    }

    private void DeleteSelectedModel()
    {
        if (_modelList.SelectedItem is not FileChoice model) { MessageBox.Show(this, "Select a tongue model first."); return; }
        if (LiveTrackingRunning) { MessageBox.Show(this, "Stop live tracking before deleting a model."); return; }
        var version = VersionFromPath(model.Primary);
        if (version == 8)
        {
            MessageBox.Show(this, "The bundled developer v8 model is protected so the hub always retains a working demo. You can export or rename it, but not delete it.", "Bundled model protected");
            return;
        }
        if (MessageBox.Show(this, $"Delete “{model.Label}” from this installation?\n\nThis removes its paired checkpoints and cannot be undone unless you exported it first.", "Delete tongue model", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var models = Path.Combine(_root, "models");
        foreach (var path in Directory.GetFiles(models, $"qpro-stereo-tongue-v{version}-*")) File.Delete(path);
        if (File.Exists(ModelMetadataPath(version))) File.Delete(ModelMetadataPath(version));
        AppendLog($"Deleted tongue model v{version}.");
        ReloadProfiles();
    }

    private void ExportSelectedModel()
    {
        if (_modelList.SelectedItem is not FileChoice model) { MessageBox.Show(this, "Select a tongue model first."); return; }
        var version = VersionFromPath(model.Primary);
        using var dialog = new SaveFileDialog
        {
            Title = "Export tongue model",
            Filter = "Qpro tongue model (*.qptonguemodel)|*.qptonguemodel",
            FileName = SafeFileName(ModelFriendlyName(version, ReadModelMetadata(version))) + ".qptonguemodel",
            AddExtension = true,
            DefaultExt = "qptonguemodel"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
        using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
        var manifest = new JsonObject
        {
            ["format"] = "qpro-tongue-model-package-v1",
            ["displayName"] = ModelFriendlyName(version, ReadModelMetadata(version)),
            ["sourceVersion"] = version,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        CopyModelClassification(ReadModelMetadata(version), manifest);
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), Encoding.UTF8)) writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        archive.CreateEntryFromFile(model.Primary, "gate.pt", CompressionLevel.Optimal);
        archive.CreateEntryFromFile(model.Secondary!, "direction.pt", CompressionLevel.Optimal);
        AppendLog($"Exported “{model.Label}” to {dialog.FileName}.");
    }

    private void ImportModel()
    {
        if (LiveTrackingRunning) { MessageBox.Show(this, "Stop live tracking before importing a model."); return; }
        using var dialog = new OpenFileDialog { Title = "Import tongue model", Filter = "Qpro tongue model (*.qptonguemodel)|*.qptonguemodel", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, "Only import model files from someone you trust. PyTorch model files are executable data when loaded.\n\nContinue?", "Trust this model?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        try
        {
            var version = HubModelPackage.NextVersion(models);
            var manifest = HubModelPackage.Import(dialog.FileName, models, version, packageManifest =>
                HubModelMetadata.Create(version,
                    CleanDisplayName(packageManifest["displayName"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(dialog.FileName)),
                    null, "imported package", null, packageManifest));
            var displayName = manifest["displayName"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(dialog.FileName);
            AppendLog($"Imported “{CleanDisplayName(displayName)}” as local model v{version}.");
            ReloadProfiles();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Model import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? PromptForText(string title, string prompt, string initial)
    {
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
