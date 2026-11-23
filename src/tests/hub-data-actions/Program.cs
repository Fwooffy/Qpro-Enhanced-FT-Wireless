using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using QproFaceTracking.Hub;

internal static class DataActionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly (string Record, string Train, string Flag, string Type, string Field)[] CaptureKinds =
    [
        ("1. Record refinement", "2. Train personalized copy", "-TongueRefinementCalibration", "tongue-stereo-refinement-v2", "_quickDatasets"),
        ("1. Record focused dataset", "2. Train focused copy", "-TongueArcCalibration", "tongue-stereo-arc-v3", "_focusedDatasets"),
        ("1. Record full dataset", "2. Train new personal model", "-TongueStillCalibration", "tongue-stereo-stills-v1", "_fullDatasets"),
    ];
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var temporary = Directory.CreateTempSubdirectory("qpro-data-actions-");
        try
        {
            TestModelActions(Directory.CreateDirectory(Path.Combine(temporary.FullName, "models-test")).FullName);
            TestPersonalize(Directory.CreateDirectory(Path.Combine(temporary.FullName, "data-test")).FullName);
            TestPlainPreview(Directory.CreateDirectory(Path.Combine(temporary.FullName, "preview-test")).FullName);
            Console.WriteLine($"PASS {_checks} Personalize and Model manager click checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static T Field<T>(HubForm form, string name) =>
        (T)typeof(HubForm).GetField(name, Private)!.GetValue(form)!;

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Button Button(Control parent, string text) =>
        Descendants(parent).OfType<Button>().Single(button => button.Text == text && button.Visible);

    private static void Click(Control parent, string text)
    {
        var button = Button(parent, text);
        Check(button.Enabled, "Disabled button: " + text);
        button.PerformClick();
        Application.DoEvents();
    }

    private static HubForm Open(Fixture fixture, bool inject = true)
    {
        var form = new HubForm(fixture.Root, rememberLaunch: false, previewOnly: true)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Opacity = 0,
        };
        Field<HubEnvironment>(form, "_environment").SetPreviewTrackingSource(false);
        if (inject) form.ConfigurePreviewDataInteraction(fixture.Interaction);
        else typeof(HubForm).GetMethod("ReloadProfiles", Private)!.Invoke(form, null);
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static void SelectModel(ListBox models, int version)
    {
        models.SelectedIndex = Enumerable.Range(0, models.Items.Count).Single(index =>
            ((FileChoice)models.Items[index]!).Primary.EndsWith($"-v{version}-gate.pt", StringComparison.Ordinal));
    }

    private static void TestModelActions(string root)
    {
        var fixture = new Fixture(root);
        fixture.WriteModel(8, new JsonObject { ["displayName"] = "Fixture bundled", ["modelKind"] = "developer-tongue",
            ["bundledDeveloperModel"] = true, ["origin"] = "bundled developer model" });
        fixture.WriteModel(40, new JsonObject { ["displayName"] = "Fixture personal", ["origin"] = "quick refinement",
            ["datasetSession"] = "private-name.qpsession.json", ["isExperimental"] = true });
        using var form = Open(fixture);
        Click(form, "Model manager");
        ListBox models = Field<ListBox>(form, "_modelList");
        SelectModel(models, 8);
        Check(!Button(form, "Delete").Enabled, "The bundled model was deletable.");
        SelectModel(models, 40);
        var liveModel = Field<ComboBox>(form, "_tongueModels").SelectedItem;
        Check(liveModel is FileChoice bundle && bundle.Primary == fixture.ModelPath(8, "-gate.pt"),
            "Browsing the model manager changed the live model.");

        fixture.Name = "Model & Test";
        Click(form, "Rename");
        var renamed = fixture.ReadMetadata(40);
        Check((string?)renamed["displayName"] == fixture.Name && (bool?)renamed["isExperimental"] == true &&
            (string?)renamed["datasetSession"] == "private-name.qpsession.json", "Renaming dropped lineage or experiment status.");
        Check(File.ReadAllText(fixture.ModelPath(40, "-gate.pt")) == "synthetic gate 40", "Renaming rewrote the checkpoint.");

        Click(form, "Export");
        Check(fixture.SuggestedPackage == "Model & Test.qptonguemodel" && !File.Exists(fixture.ExportPath),
            "Canceled Export wrote a package or suggested the wrong name.");
        fixture.PackagePath = fixture.ExportPath;
        Click(form, "Export");
        using (var archive = ZipFile.OpenRead(fixture.ExportPath))
        {
            Check(archive.Entries.Select(entry => entry.FullName).Order().SequenceEqual(new[] { "direction.pt", "gate.pt", "manifest.json" }),
                "Export included something outside the two checkpoints and manifest.");
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            var metadata = JsonNode.Parse(reader.ReadToEnd())!;
            Check(metadata["datasetSession"] is null && metadata["bundledDeveloperModel"] is null,
                "Export included local dataset lineage or bundle ownership.");
        }
        Click(form, "Import");
        Check((string?)fixture.ReadMetadata(41)["origin"] == "imported package" &&
            File.ReadAllText(fixture.ModelPath(41, "-gate.pt")) == "synthetic gate 40" &&
            File.ReadAllText(fixture.ModelPath(41, "-direction.pt")) == "synthetic direction 40",
            "Import did not install the synthetic pair at the next version.");
        fixture.PackagePath = Path.Combine(root, "invalid.qptonguemodel");
        File.WriteAllText(fixture.PackagePath, "not a model package");
        Click(form, "Import");
        Check(fixture.LastTitle == "Model import failed" && models.Items.Count == 3 &&
            !Directory.EnumerateDirectories(fixture.Models, ".qpro-import-*").Any(), "Invalid import leaked files or changed the list.");

        SelectModel(models, 41);
        string backup = fixture.ModelPath(41, "-backup.pt");
        File.WriteAllText(backup, "separate file");
        fixture.Confirm = false;
        Click(form, "Delete");
        Check(File.Exists(fixture.ModelPath(41, "-gate.pt")) && fixture.DefaultButton == MessageBoxDefaultButton.Button2,
            "Model delete skipped its safe confirmation.");
        fixture.Confirm = true;
        using (File.Open(fixture.ModelPath(41, "-direction.pt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Click(form, "Delete");
            Check(fixture.LastTitle == "Model delete failed" && File.Exists(fixture.ModelPath(41, "-gate.pt")) &&
                File.Exists(fixture.ModelPath(41, ".metadata.json")), "Failed model deletion split the pair or removed its metadata.");
        }
        Click(form, "Delete");
        Check(!File.Exists(fixture.ModelPath(41, "-gate.pt")) && !File.Exists(fixture.ModelPath(41, "-direction.pt")) &&
            !File.Exists(fixture.ModelPath(41, ".metadata.json")) && File.Exists(backup) && File.Exists(fixture.ExportPath),
            "Model Delete did not remove only the selected pair and its local details.");
    }

    private static void TestPersonalize(string root)
    {
        var fixture = new Fixture(root);
        fixture.WriteModel(8);
        string decoy = fixture.CapturePath("other-capture");
        fixture.WriteCapture(decoy, CaptureKinds[0].Type, labels: false, displayName: "Other recording");
        using var form = Open(fixture);
        Click(form, "Personalize");
        Check(!Button(form, CaptureKinds[0].Train).Enabled && Field<Label>(form, "_quickQueueStatus").Text.Contains("labels missing"),
            "A recording without native labels was shown as ready to train.");
        Check(!Descendants(form).OfType<Button>().Any(button => button.Text == CaptureKinds[2].Record && button.Visible),
            "Full capture was visible before expanding the additional choices.");
        Click(form, "Show focused and full captures");

        fixture.Missing = ["connect and authorize the Quest Pro", "start SteamVR", "set up the PC runtime"];
        foreach (var kind in CaptureKinds)
        {
            Click(form, kind.Record);
            Check(fixture.LastTitle == "Capture is not ready" && fixture.LastMessage.Contains("SteamVR") && fixture.Workflows.Count == 0,
                "Missing capture prerequisites started a helper.");
        }
        fixture.Missing = [];
        fixture.Confirm = false;
        Click(form, CaptureKinds[0].Record);
        Check(fixture.Workflows.Count == 0 && fixture.DefaultButton == MessageBoxDefaultButton.Button2,
            "Declining capture started a helper.");
        fixture.Confirm = true;
        fixture.Name = "New synthetic capture";
        fixture.Decoy = decoy;
        foreach (var kind in CaptureKinds)
        {
            Click(form, kind.Record);
            var run = fixture.Workflows.Last();
            Check(run.Script == "build-and-run.ps1" && run.Args.Contains(kind.Flag) &&
                run.Args.Contains("-DirectVrchat") && Argument(run.Args, "-TrackingSource") == "VirtualDesktop",
                "Record opened the wrong camera workflow.");
            var record = Argument(run.Args, "-RecordPath");
            var queue = Field<ComboBox>(form, kind.Field);
            Check(Path.GetDirectoryName(record) == Path.Combine(root, "captures") &&
                (string?)JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(record, ".qpsession.json")))!["displayName"] == fixture.Name &&
                queue.SelectedItem is DatasetChoice choice && choice.Dataset.CapturePath == record && Button(form, kind.Train).Enabled,
                "Recording did not name and queue its exact output.");
        }
        Check((string?)JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(decoy, ".qpsession.json")))!["displayName"] == "Other recording",
            "Recording changed the name of the newer unrelated session.");

        var quickQueue = Field<ComboBox>(form, "_quickDatasets");
        var quick = ((DatasetChoice)quickQueue.SelectedItem!).Dataset;
        string quickLabels = Path.ChangeExtension(quick.CapturePath, ".qplabel.jsonl");
        File.Delete(quickLabels);
        Click(form, CaptureKinds[0].Train);
        Check(fixture.LastTitle == "Dataset files are missing" && !Button(form, CaptureKinds[0].Train).Enabled && fixture.Workflows.Count == 3,
            $"A label file removed after selection left a stale action (dialog={fixture.LastTitle}, enabled={Button(form, CaptureKinds[0].Train).Enabled}, launches={fixture.Workflows.Count}).");
        fixture.WriteLabels(quick.CapturePath);
        form.ConfigurePreviewDataInteraction(fixture.Interaction);
        var parents = Field<ComboBox>(form, "_tongueModels");
        parents.SelectedIndex = -1;
        Click(form, CaptureKinds[0].Train);
        Check(fixture.LastTitle == "Select a base model" && fixture.Workflows.Count == 3,
            "Quick refinement picked an unselected base model.");
        parents.SelectedIndex = 0;
        fixture.RuntimeReady = false;
        Click(form, CaptureKinds[2].Train);
        Check(fixture.LastTitle == "Training runtime is missing" && fixture.Workflows.Count == 3,
            "Training started while the PC runtime was missing.");
        fixture.RuntimeReady = true;
        foreach (var kind in CaptureKinds)
        {
            var queue = Field<ComboBox>(form, kind.Field);
            var dataset = ((DatasetChoice)queue.SelectedItem!).Dataset;
            var parent = ((FileChoice)parents.SelectedItem!).Primary;
            Click(form, kind.Train);
            var run = fixture.Workflows.Last();
            string expectedScript = kind.Field == "_fullDatasets" ? "train-latest-tongue-stills.ps1" : "train-latest-tongue-refinement.ps1";
            Check(run.Script == expectedScript && Argument(run.Args, "-SessionPath") == dataset.SessionPath &&
                (kind.Field == "_fullDatasets" ? !run.Args.Contains("-BaseVersion") :
                    parent.EndsWith($"-v{Argument(run.Args, "-BaseVersion")}-gate.pt", StringComparison.Ordinal)),
                "Train used the wrong session, variant, or model base.");
            Check(fixture.LastTitle == "Tongue model ready" && queue.Items.Count == 0 &&
                (string?)fixture.ReadMetadata(fixture.LastTrained)["datasetSession"] == Path.GetFileName(dataset.SessionPath),
                "A trained pair was not saved with the selected capture or the queue did not refresh.");
        }

        var quickRecorded = Field<ComboBox>(form, "_quickRecordedDatasets");
        Control quickCard = quickRecorded.Parent!.Parent!;
        Click(quickCard, "Show saved recordings");
        quickRecorded.SelectedIndex = Enumerable.Range(0, quickRecorded.Items.Count).Single(index =>
            ((RecordedDatasetChoice)quickRecorded.Items[index]!).Dataset.SessionPath == quick.SessionPath);
        var cache = Directory.CreateDirectory(Path.Combine(root, "training",
            Path.GetFileNameWithoutExtension(quick.CapturePath) + "-personal-refinement-224px"));
        File.WriteAllText(Path.Combine(cache.FullName, "fixture.npy"), "fixture");
        using (File.Open(quickLabels, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Click(quickCard, "Delete selected dataset…");
            Check(fixture.LastTitle == "Could not delete dataset" && File.Exists(quick.SessionPath) && File.Exists(quick.CapturePath) &&
                Directory.Exists(cache.FullName), "A locked labels file caused partial dataset deletion.");
        }
        Click(quickCard, "Delete selected dataset…");
        Check(!File.Exists(quick.SessionPath) && !File.Exists(quick.CapturePath) && !File.Exists(quickLabels) && !Directory.Exists(cache.FullName) &&
            File.Exists(fixture.ModelPath(9, "-gate.pt")) && File.Exists(decoy), "Dataset removal deleted the wrong files or touched the trained pair.");

        foreach (var (name, index) in new[] { ("_focusedRecordedDatasets", 1), ("_fullRecordedDatasets", 2) })
        {
            var recorded = Field<ComboBox>(form, name);
            var card = recorded.Parent!.Parent!;
            Click(card, "Show saved recordings");
            var owned = ((RecordedDatasetChoice)recorded.SelectedItem!).Dataset;
            Check(owned.CapturePath == Argument(fixture.Workflows[index].Args, "-RecordPath"),
                "The recordings dropdown selected another capture kind.");
            fixture.Confirm = false;
            Click(card, "Delete selected dataset…");
            Check(fixture.LastMessage.Contains(owned.SessionPath) && File.Exists(owned.CapturePath), "Delete confirmed the wrong capture.");
            fixture.Confirm = true;
            Click(card, "Delete selected dataset…");
            Check(!File.Exists(owned.CapturePath) && File.Exists(decoy), "Delete routed to the wrong capture kind.");
        }
    }

    private static void TestPlainPreview(string root)
    {
        var fixture = new Fixture(root);
        fixture.WriteModel(41);
        string recording = fixture.CapturePath("preview");
        fixture.WriteCapture(recording, CaptureKinds[0].Type);
        using var form = Open(fixture, inject: false);
        Click(form, "Personalize");
        Click(form, CaptureKinds[0].Record);
        Click(form, CaptureKinds[0].Train);
        var card = Field<ComboBox>(form, "_quickRecordedDatasets").Parent!.Parent!;
        Click(card, "Show saved recordings");
        Click(card, "Delete selected dataset…");
        Click(form, "Model manager");
        foreach (string action in new[] { "Rename", "Export", "Import", "Delete" }) Click(form, action);
        Check(File.Exists(recording) && Directory.GetFiles(fixture.Models).Length == 2 &&
            Directory.GetFiles(Path.Combine(root, "captures")).Length == 3,
            "A plain render preview wrote model or capture files.");
    }

    private static string Argument(string[] arguments, string option) => arguments[Array.IndexOf(arguments, option) + 1];

    private sealed class Fixture(string root)
    {
        internal string Root => root;
        internal string Models => Path.Combine(root, "models");
        internal string ExportPath => Path.Combine(root, "fixture.qptonguemodel");
        internal string? PackagePath, SuggestedPackage, Name, Decoy;
        internal string LastTitle = "", LastMessage = "";
        internal MessageBoxDefaultButton DefaultButton;
        internal bool Confirm = true, RuntimeReady = true;
        internal IReadOnlyList<string> Missing = [];
        internal int LastTrained;
        internal readonly List<(string Script, string[] Args)> Workflows = [];

        internal HubDataInteraction Interaction => new(
            (text, title, buttons, _, defaultButton) =>
            {
                LastTitle = title;
                LastMessage = text;
                DefaultButton = defaultButton;
                return buttons == MessageBoxButtons.YesNo ? Confirm ? DialogResult.Yes : DialogResult.No : DialogResult.OK;
            },
            (_, _, _) => Name,
            (_, suggested) => { SuggestedPackage = suggested; return PackagePath; },
            () => Task.FromResult(Missing),
            () => RuntimeReady,
            (_, script, arguments) =>
            {
                Workflows.Add((script, arguments));
                if (script == "build-and-run.ps1")
                {
                    string kind = CaptureKinds.Single(kind => arguments.Contains(kind.Flag)).Type;
                    WriteCapture(Argument(arguments, "-RecordPath"), kind);
                    if (Decoy is not null)
                        File.SetLastWriteTimeUtc(Path.ChangeExtension(Decoy, ".qpsession.json"), DateTime.UtcNow.AddMinutes(1));
                }
                else
                {
                    LastTrained = HubModelPackage.NextVersion(Models);
                    WriteModel(LastTrained);
                }
                return Task.FromResult(true);
            });

        internal string ModelPath(int version, string suffix) => Path.Combine(Models, $"qpro-stereo-tongue-v{version}{suffix}");
        internal JsonNode ReadMetadata(int version) => JsonNode.Parse(File.ReadAllText(ModelPath(version, ".metadata.json")))!;
        internal string CapturePath(string name) => Path.Combine(root, "captures", name + ".qpcap");

        internal void WriteModel(int version, JsonObject? metadata = null)
        {
            Directory.CreateDirectory(Models);
            File.WriteAllText(ModelPath(version, "-gate.pt"), $"synthetic gate {version}");
            File.WriteAllText(ModelPath(version, "-direction.pt"), $"synthetic direction {version}");
            if (metadata is not null) File.WriteAllText(ModelPath(version, ".metadata.json"), metadata.ToJsonString());
        }

        internal void WriteCapture(string path, string type, bool labels = true, string? displayName = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic recording");
            File.WriteAllText(Path.ChangeExtension(path, ".qpsession.json"), JsonSerializer.Serialize(
                new { sessionType = type, completed = true, samples = new[] { new { frameIndex = 0 } }, displayName }));
            if (labels) WriteLabels(path);
        }

        internal void WriteLabels(string capture) =>
            File.WriteAllText(Path.ChangeExtension(capture, ".qplabel.jsonl"), """{"type":"sample","values":[0.5]}""");
    }
}
