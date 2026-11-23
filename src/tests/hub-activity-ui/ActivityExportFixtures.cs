using System.Reflection;

internal static class ActivityExportFixtures
{
    internal static void Run(Form form, Button[] controls, RichTextBox log, Action<string> append, Action reset)
    {
        FieldInfo Field(string name) => form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Button Button(string text) => controls.Single(button => button.Text == text);
        var copy = Button("Copy diagnostics");
        var save = Button("Save log…");
        var pause = Button("Pause display");
        var latest = Button("Jump to latest");
        var details = Button("Show tracking details");
        var backend = (Label)Field("_healthBackend").GetValue(form)!;
        var hint = (Label)Field("_activityExportHint").GetValue(form)!;
        var pauseHint = (Label)Field("_activityDisplayHint").GetValue(form)!;
        var actionTitle = (Label)Field("_actionTitle").GetValue(form)!;
        var clipboard = Field("_writeDiagnosticClipboard");
        var chooseFile = Field("_chooseDiagnosticFile");
        var oldClipboard = clipboard.GetValue(form);
        var oldChooser = chooseFile.GetValue(form);
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "activity.txt");
        var originalTitle = actionTitle.Text;
        string? copied = null;
        int choices = 0;
        var answer = DialogResult.Cancel;
        var destination = path;
        try
        {
            clipboard.SetValue(form, (Action<string>)(value => copied = value));
            chooseFile.SetValue(form, (Func<SaveFileDialog, IWin32Window, DialogResult>)((dialog, owner) =>
            {
                Require(ReferenceEquals(owner, form) && dialog.OverwritePrompt && dialog.RestoreDirectory &&
                    dialog.FileName == "Qpro-diagnostics.txt" && dialog.Filter == "Text log (*.txt)|*.txt",
                    "Save did not present the owned text-log chooser with overwrite protection.");
                choices++;
                dialog.FileName = destination;
                return answer;
            }));
            reset();
            CheckRestoreCancellation(form, Button("Restore stock VRCFT"), temporary.Path);
            Require(!backend.Visible, "Tracking details unexpectedly started expanded.");
            details.PerformClick();
            Require(backend.Visible && details.Text == "Hide tracking details" && details.AccessibleName == details.Text,
                "The health disclosure did not reveal the actual backend details.");
            details.PerformClick();
            Require(!backend.Visible && details.Text == "Show tracking details", "Tracking details did not collapse.");

            Require(copy.Parent == save.Parent && hint.Parent == copy.Parent && !pauseHint.Visible,
                "Export actions or feedback were separated, or an idle pause hint remained visible.");
            copy.PerformClick();
            Require(copied?.Contains("TRACKING HEALTH") == true && copied.EndsWith("ACTIVITY\n", StringComparison.Ordinal) &&
                hint.Visible && hint.Text == "Diagnostics copied.", "Copy on an empty log omitted context or success feedback.");
            latest.PerformClick();
            Require(log.TextLength == 0 && log.SelectionStart == 0, "Jump on an empty log changed it.");
            File.WriteAllText(path, "preserve on cancel");
            save.PerformClick();
            Require(choices == 1 && File.ReadAllText(path) == "preserve on cancel", "Cancelling the save replaced the chosen file.");

            pause.PerformClick();
            Require(pauseHint.Visible && pauseHint.Text.Contains("Tracking and logging continue"),
                "Pausing omitted its contextual collection status.");
            var profilePath = Path.Combine("C:" + Path.DirectorySeparatorChar, "Users", "Fixture Person", "Downloads", "trace.txt");
            append($"[Worker] ERROR: sample {profilePath}, ADB target: TESTDEVICE567");
            copy.PerformClick();
            Require(log.TextLength == 0 && copied is not null && copied.Contains("%USERPROFILE%") &&
                copied.Contains("[redacted]") && !copied.Contains("Fixture Person") && !copied.Contains("TESTDEVICE567"),
                "Copy did not redact newly collected history while paused.");
            answer = DialogResult.OK;
            save.PerformClick();
            Require(choices == 2 && File.ReadAllText(path) == copied && hint.Text == "Diagnostics saved." &&
                actionTitle.Text == originalTitle, "Saving from the frozen view lost collection, status or the latest action result.");

            clipboard.SetValue(form, (Action<string>)(_ => throw new InvalidOperationException("Fixture clipboard busy")));
            copy.PerformClick();
            Require(hint.Text.Contains("Could not copy") && log.TextLength == 0, "A paused reader could not see the clipboard failure.");
            destination = temporary.Path; // An existing directory cannot be overwritten as a log file.
            save.PerformClick();
            Require(hint.Text.Contains("Could not save") && File.ReadAllText(path) == copied && log.TextLength == 0,
                "Save failure did not retain the previous file and report the error in the frozen view.");
            latest.PerformClick();
            Require(log.Text.Contains("Fixture clipboard busy") && log.Text.Contains("Could not save diagnostics") &&
                pause.Text == "Pause display", "Jump after an export failure did not return to current diagnostics.");
            Console.WriteLine("PASS: Activity button clicks for tracking details, empty and paused reading, copy, save, cancellation, redaction and failed exports.");
        }
        finally
        {
            clipboard.SetValue(form, oldClipboard);
            chooseFile.SetValue(form, oldChooser);
            reset();
        }
    }

    private static void CheckRestoreCancellation(Form form, Button restore, string temporaryPath)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var formType = form.GetType();
        var preview = formType.GetField("_previewOnly", fields)!;
        var prompt = formType.GetField("_confirmStockRestore", fields)!;
        var originalPrompt = prompt.GetValue(form);
        var installation = formType.Assembly.GetType("QproFaceTracking.Hub.HubModuleInstallation")!;
        var hasBackup = installation.GetMethod("HasSavedBackups", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fixtureRoot = (string)formType.GetField("_root", fields)!.GetValue(form)!;
        Require(!Directory.Exists(fixtureRoot), "Preview source fixture should be empty.");
        var research = Path.Combine(fixtureRoot, "research");
        var recovery = Path.Combine(temporaryPath, "recovery");
        bool HasBackup() => (bool)hasBackup.Invoke(null, [research, recovery])!;
        Require(!restore.Visible && !HasBackup(), "A fresh preview offered legacy module removal.");
        var backup = Directory.CreateDirectory(Path.Combine(research, "vrcft-official-virtual-desktop-backup"));
        var savedFile = Path.Combine(backup.FullName, "fixture.txt");
        File.WriteAllText(savedFile, "keep me");
        int prompts = 0;
        try
        {
            prompt.SetValue(form, (Func<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon, MessageBoxDefaultButton, DialogResult>)
                ((owner, message, title, buttons, icon, defaultButton) =>
                {
                    Require(ReferenceEquals(owner, form) && title == restore.Text && buttons == MessageBoxButtons.YesNo &&
                        defaultButton == MessageBoxDefaultButton.Button2 && message.Contains("source-module backups"),
                        "Legacy recovery did not ask for confirmation with No as its default.");
                    prompts++;
                    return DialogResult.No;
                }));
            // Only enter the synchronous confirmation gate. The dialog always declines before any helper or cleanup.
            preview.SetValue(form, false);
            var card = restore.Parent!.Parent!;
            card.Visible = false;
            card.Visible = true;
            Require(HasBackup() && restore.Visible && restore.Enabled, "The Activity card failed to offer the fixture backup.");
            restore.PerformClick();
            Require(prompts == 1 && File.ReadAllText(savedFile) == "keep me",
                "Declining the actual restore action touched its saved module.");
        }
        finally
        {
            preview.SetValue(form, true);
            prompt.SetValue(form, originalPrompt);
            restore.Visible = false;
            File.Delete(savedFile);
            backup.Delete();
            Directory.Delete(research);
            Directory.Delete(fixtureRoot);
        }
        Console.WriteLine("PASS: legacy restore hidden on a fresh setup; temporary backup enables its confirmation and No preserves files.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("qpro-activity-export-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
