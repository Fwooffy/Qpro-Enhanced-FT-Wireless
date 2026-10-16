using System.Reflection;
using QproFaceTracking.Hub;

internal static class UpdateNotificationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetColorMode(SystemColorMode.Dark);
            CheckState(); CheckWindowGuards(); CheckLifetime();
            bool checkOnly = args.Contains("--check-only", StringComparer.Ordinal);
            if (!checkOnly)
            {
                RenderNotice(args.FirstOrDefault(), prerelease: false);
                RenderNotice(args.FirstOrDefault(), prerelease: true);
            }
            Console.WriteLine($"PASS: {_passed} update-notification checks. {(checkOnly ? "No windows shown or rendered" : "Offscreen rendering only")}; no network, download, install, preferences or headset changes.");
        }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }

    static void CheckState()
    {
        var current = new Version(3, 0, 0);
        var state = new HubUpdateNotificationState();
        var stable = Release("3.0.1");
        var beta = Release("3.0.2", prerelease: true);
        state.Observe(stable, current, false);
        Check(state.Pending == stable, "A new stable release did not remain pending.");
        state.Observe(new(HubUpdateCheckState.Unavailable, "Sample offline failure"), current, false);
        Check(state.Pending == stable, "A temporary check failure erased the pending release.");
        var offline = new HubUpdateCheck(HubUpdateCheckState.Unavailable, "Sample offline failure");
        Check(state.ForReview(offline) == stable, "View update could not review a known release after a network failure.");
        state.Acknowledge(stable.Release!);
        Check(state.Pending is null, "Later did not dismiss this notification.");
        Check(state.ForReview(offline) == stable, "Later removed the checked release from subsequent review.");
        state.Observe(stable, current, false);
        Check(state.Pending is null, "The same release prompted twice in one Hub session.");
        state.Observe(beta, current, false);
        Check(state.Pending is null, "A stable-only channel announced a prerelease.");
        state.Observe(beta, current, true);
        Check(state.Pending == beta, "An opted-in prerelease did not remain pending.");
        state.RestrictChannel(false);
        Check(state.Pending is null, "Disabling prereleases left a test-release notification pending.");
        Check(state.ForReview(offline) == offline, "Disabling prereleases retained an excluded release for review.");
        state.Observe(beta, current, true);
        state.Acknowledge(beta.Release!);
        state.Observe(beta with { Release = beta.Release! with { Tag = "v3.0.2-beta.2", PrereleaseSuffix = "beta.2" } }, current, true);
        Check(state.Pending?.Release?.PrereleaseSuffix == "beta.2", "A new prerelease suffix was suppressed as an old release.");
        var promotion = Release("3.0.2");
        state.Observe(promotion, current, true);
        Check(state.Pending == promotion, "A stable promotion was hidden by the earlier prerelease.");
        state.Observe(new(HubUpdateCheckState.UpToDate, "Sample current channel"), current, true);
        Check(state.Pending is null, "An up-to-date result kept an obsolete pending notice.");
        Check(state.ForReview(offline) == offline, "A confirmed up-to-date result retained an obsolete release for review.");
        foreach (HubUpdateCheck check in new[] { Release("3.0.0"), Release("2.9.0"),
            new HubUpdateCheck(HubUpdateCheckState.Available, "No release identity"),
            new HubUpdateCheck(HubUpdateCheckState.UpToDate, "Inconsistent result", stable.Release) })
        {
            state.Observe(check, current, true);
            Check(state.Pending is null, "A non-new or inconsistent result could be announced.");
        }
        var manual = Release("3.1.0") with { State = HubUpdateCheckState.ManualDownloadOnly,
            Release = Release("3.1.0").Release! with { AssetName = null, DownloadUrl = null, Sha256 = null } };
        state.Observe(manual, current, false);
        Check(state.Pending == manual && !state.Pending.Release!.CanInstall,
            "A manual-only release was hidden or treated as installable.");
    }

    static void CheckWindowGuards()
    {
        for (int mask = 0; mask < 16; mask++)
        {
            bool ready = (mask & 1) != 0, busy = (mask & 2) != 0, modal = (mask & 4) != 0, closing = (mask & 8) != 0;
            Check(HubUpdateNotificationState.CanPrompt(ready, busy, modal, closing) == (mask == 1),
                "A busy, inactive, modal or closing Hub could be interrupted.");
        }
    }

    static void CheckLifetime()
    {
        string root = Path.Combine(Path.GetTempPath(), "qpro-notification-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var form = new HubForm(root, rememberLaunch: false, previewOnly: true);
            typeof(HubForm).GetMethod("QueueUpdateNotification", Private)!.Invoke(form, [Release("3.0.1")]);
            var state = Field<HubUpdateNotificationState>(form, "_updateNotification");
            var timer = Field<System.Windows.Forms.Timer>(form, "_updateNotificationTimer");
            Check(state.Pending is not null && timer.Enabled, "A deferred release did not start the notice timer.");
            typeof(HubForm).GetField("_updateCheck", Private)!.SetValue(form, new HubUpdateCheck(HubUpdateCheckState.Unavailable, "Sample later network failure"));
            typeof(HubForm).GetMethod("RefreshUpdateLink", Private)!.Invoke(form, null);
            Check(Field<LinkLabel>(form, "_updateLink").Text == "Update available", "A later network failure hid the pending update link.");
            var review = (HubUpdateCheck?)typeof(HubForm).GetMethod("UpdateCheckForReview", Private)!.Invoke(form, null);
            Check(review?.Release?.Tag == "v3.0.1", "The View update handler selected the unavailable check instead of the pending release.");
            state.Acknowledge(state.Pending!.Release!);
            review = (HubUpdateCheck?)typeof(HubForm).GetMethod("UpdateCheckForReview", Private)!.Invoke(form, null);
            Check(review?.Release?.Tag == "v3.0.1", "The View update handler lost the release when the notice was acknowledged.");
            typeof(HubForm).GetMethod("RefreshUpdateLink", Private)!.Invoke(form, null);
            Check(Field<LinkLabel>(form, "_updateLink").Text == "Update available", "Later removed the release's review link.");
            Check(!(bool)typeof(HubForm).GetMethod("CanShowUpdateNotification", Private)!.Invoke(form, null)!,
                "An invisible preview form could display a popup.");
            form.Dispose();
            Check(!timer.Enabled, "Disposal retained the notification timer.");
        }
        finally { Directory.Delete(root); }
    }

    static void RenderNotice(string? output, bool prerelease)
    {
        using var dialog = new HubUpdateAvailableDialog(Release("3.0.1", prerelease).Release!);
        Check(dialog.Text == "Qpro Companion updates" &&
            Field<Label>(dialog, "_release").Text.StartsWith("Qpro Companion V3.0.1", StringComparison.Ordinal),
            "The update notice kept the old public product name.");
        // Render the actual dialog controls on an offscreen surface that cannot
        // activate. The user's capture window keeps keyboard focus throughout.
        using var surface = new OffscreenPreviewForm { Text = dialog.Text, BackColor = dialog.BackColor, ForeColor = dialog.ForeColor,
            Font = dialog.Font, AutoScaleMode = dialog.AutoScaleMode, AutoScaleDimensions = dialog.AutoScaleDimensions,
            Size = dialog.Size, MinimumSize = dialog.MinimumSize, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), ShowInTaskbar = false, Opacity = 0, MinimizeBox = false, MaximizeBox = false };
        surface.Controls.Add(dialog.Controls[0]);
        surface.Show(); Application.DoEvents();
        var view = Field<DarkButton>(dialog, "_view"); var later = Field<DarkButton>(dialog, "_later");
        Check(view.DialogResult == DialogResult.OK && later.DialogResult == DialogResult.Cancel,
            "The notice lost its View update or Later action.");
        Check(dialog.CancelButton == later && dialog.AcceptButton == view, "The keyboard actions do not match the notice.");
        Check(Field<Label>(dialog, "_release").ForeColor == (prerelease ? HubForm.Warning : HubForm.Good),
            "The release channel lost its status color.");
        foreach (Size size in new[] { dialog.Size, dialog.MinimumSize })
        {
            surface.Size = size; surface.PerformLayout(); Application.DoEvents();
            using var bitmap = new Bitmap(surface.Width, surface.Height);
            surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, surface.Size));
            foreach (string name in new[] { "_heading", "_release", "_summary", "_view", "_later" })
            {
                Control control = Field<Control>(dialog, name);
                Point location = control.Location;
                for (Control? parent = control.Parent; parent is not null && parent != surface; parent = parent.Parent)
                    location.Offset(parent.Location);
                Rectangle bounds = new(location, control.ClientSize);
                Check(surface.ClientRectangle.Contains(bounds), "The notification clips " + name + ": " + bounds + "; client=" + surface.ClientRectangle + "; control=" + control.Bounds + ".");
                if (control is Label label) Check(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height,
                    "The notification clips its message text: " + name + ".");
            }
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                bitmap.Save(Path.Combine(output, "update-available-" + (prerelease ? "prerelease" : "stable") +
                    (size == dialog.MinimumSize ? "-compact" : string.Empty) + ".png"));
            }
        }
        surface.Close();
    }

    static HubUpdateCheck Release(string version, bool prerelease = false)
    {
        var number = Version.Parse(version);
        string tag = "v" + version + (prerelease ? "-beta.1" : "");
        var release = new HubUpdateRelease(number, tag, "Sample release",
            new Uri("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/" + tag),
            "Sample release notes. This fixture does not download or install an update.",
            "QproFaceTracking.V" + version + ".zip",
            new Uri("https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/download/" + tag + "/QproFaceTracking.V" + version + ".zip"),
            1024, new string('a', 64), prerelease, prerelease ? "beta.1" : null);
        return new(HubUpdateCheckState.Available, "Sample release available.", release);
    }

    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); _passed++; }

    private sealed class OffscreenPreviewForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
