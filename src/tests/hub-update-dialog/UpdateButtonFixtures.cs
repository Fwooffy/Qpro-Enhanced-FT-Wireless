using System.Reflection;
using QproFaceTracking.Hub;

internal static partial class UpdateDialogTests
{
    private static void Escape(Form dialog) => typeof(Form).GetMethod("ProcessDialogKey", Private)!.Invoke(dialog, [Keys.Escape]);

    private static void CheckUpdateButtons(HubUpdateCheck release, Version current)
    {
        var plan = new HubComponentPlan([
            new(HubComponentKind.Runtime, "Runtime", "Synthetic runtime", "sample", "fixture"),
            new(HubComponentKind.Rocm, "GPU", "Synthetic GPU", "sample", "fixture")], []);
        using (var hub = new HubForm(Path.GetTempPath(), rememberLaunch: false, previewOnly: true))
        {
            // Skip startup probes during construction. Supply both destinations explicitly
            // so a routing regression cannot start a release request or inspect a runtime.
            var preview = typeof(HubForm).GetField("_previewOnly", Private)!;
            var header = Field<LinkLabel>(hub, "_updateLink");
            var route = typeof(HubForm).GetMethod("HandleHubUpdateClickAsync", Private)!;
            typeof(HubForm).GetField("_componentPlan", Private)!.SetValue(hub, plan);
            preview.SetValue(hub, false);
            try
            {
                foreach (var (check, appRequested, expected) in new[] {
                    ((HubUpdateCheck?)null, false, "components"), (null, true, "components,app"), (release, false, "app") })
                {
                    typeof(HubForm).GetField("_updateCheck", Private)!.SetValue(hub, check);
                    typeof(HubForm).GetMethod("RefreshUpdateLink", Private)!.Invoke(hub, null);
                    Check(header.Text == (check is null ? "Component updates" : "Update available"), "Wrong header destination label.");
                    var visits = new List<string>();
                    Func<Task<bool>> components = () => { visits.Add("components"); return Task.FromResult(appRequested); };
                    Func<Task> app = () => { visits.Add("app"); return Task.CompletedTask; };
                    ((Task)route.Invoke(hub, [components, app])!).GetAwaiter().GetResult();
                    Check(string.Join(',', visits) == expected, "Header dispatched to " + string.Join(',', visits));
                }
            }
            finally { preview.SetValue(hub, true); }
        }

        Uri? opened = null;
        foreach (var (button, result) in new[] { ("_later", DialogResult.Cancel), ("_view", DialogResult.OK) })
        {
            using var notice = new HubUpdateAvailableDialog(release.Release!);
            ShowForTest(notice); Field<DarkButton>(notice, button).PerformClick();
            Check(notice.DialogResult == result, "Update notice returned the wrong action for " + button);
        }
        HubUpdateDialog ReleaseDialog(HubUpdateCheck check) => new(check, current, true, null,
            (_, _, _) => throw new Exception("A navigation button started a download."), openLink: uri => opened = uri);
        foreach (var check in new[] { release, new(HubUpdateCheckState.UpToDate, "Fixture is current.") })
        {
            using var dialog = ReleaseDialog(check);
            ShowForTest(dialog);
            var automatic = Field<CheckBox>(dialog, "_automatic");
            automatic.Checked = false;
            Check(!dialog.CheckAutomatically, "Automatic-check preference did not follow its checkbox.");
            Field<DarkButton>(dialog, "_github").PerformClick();
            Check(opened == (check.Release?.ReleasePage ?? HubUpdateReleaseParser.ReleasesPage), "GitHub button selected the wrong destination.");
            Escape(dialog); Pump();
            Check(!dialog.Visible && !dialog.OpenComponents && dialog.DialogResult != DialogResult.OK, "Escape did not dismiss app updates safely.");
        }
        using (var dialog = ReleaseDialog(release))
        {
            ShowForTest(dialog); Field<DarkButton>(dialog, "_components").PerformClick();
            Check(dialog.OpenComponents && !dialog.Visible && dialog.StagedUpdate is null, "Components did not leave release review cleanly.");
        }
        foreach (bool chooseComponents in new[] { false, true })
        {
            var staged = new HubStagedUpdate("fixture.exe", "fixture-runtime", "old-runtime", 0, []);
            using var ready = new HubUpdateDialog(release, current, true, null, (_, _, _) => Task.FromResult(staged));
            ShowForTest(ready); Field<DarkButton>(ready, "_install").PerformClick(); Pump();
            if (chooseComponents) Field<DarkButton>(ready, "_components").PerformClick(); else Escape(ready);
            Check(!ready.Visible && ready.DialogResult != DialogResult.OK && ready.StagedUpdate == staged && ready.OpenComponents == chooseComponents,
                "Leaving a verified stage failed to return it for cleanup without confirming restart.");
        }
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<HubComponentKind>();
        using (var components = new HubComponentUpdateDialog(plan, (item, progress) =>
        {
            calls.Add(item.Kind); progress("Synthetic progress");
            return item.Kind == HubComponentKind.Runtime ? pending.Task : Task.FromResult<string?>(null);
        }, offerAppUpdates: true))
        {
            ShowForTest(components);
            var update = Field<DarkButton>(components, "_update"); var app = Field<DarkButton>(components, "_appUpdates");
            update.PerformClick(); Escape(components); components.Close(); Pump();
            Check(components.Visible && !app.Enabled && !update.Enabled && calls.Count == 1, "A running component update lost its close/navigation guard.");
            pending.SetResult("Synthetic check failed"); Pump();
            Check(calls.SequenceEqual([HubComponentKind.Runtime]) && Field<Label>(components, "_status").Text.Contains("Synthetic check failed"), "Failed component continued to another helper.");
            Escape(components); Pump();
            Check(!components.Visible && components.DialogResult != DialogResult.Retry, "Escape did not close the completed component review.");
        }
        using (var components = new HubComponentUpdateDialog(plan, (_, _) => throw new Exception("Review during tracking applied changes."),
            offerAppUpdates: true, trackingBusy: true))
        {
            ShowForTest(components);
            Check(!Field<DarkButton>(components, "_update").Enabled && Field<Label>(components, "_status").Text.Contains("Stop tracking"), "Tracking-time component review enabled updates.");
            Field<DarkButton>(components, "_appUpdates").PerformClick();
            Check(components.DialogResult == DialogResult.Retry, "App updates did not request release review.");
        }
        calls.Clear();
        using (var components = new HubComponentUpdateDialog(plan, (item, _) => { calls.Add(item.Kind); return Task.FromResult<string?>(null); }))
        {
            ShowForTest(components); Field<DarkButton>(components, "_update").PerformClick(); Pump();
            Check(calls.SequenceEqual([HubComponentKind.Runtime, HubComponentKind.Rocm]) && Field<Label>(components, "_status").Text.Contains("completed"), "Successful component plan did not finish all steps in order.");
            Field<DarkButton>(components, "_close").PerformClick();
            Check(!components.Visible, "Component Close left its window open.");
        }
    }
}
