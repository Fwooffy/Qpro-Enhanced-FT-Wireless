using Qpro.Shared;
using QproFaceTracking.Hub;

internal static partial class NativePassthroughTests
{
    private static HubForm RouteForm(string root, string config, FeedFixture fixture, Func<bool> host, bool bundled = true) =>
        new(root, false, true, fixture.Create,
            (steam, _, report) => fixture.Create(steam, report).Session,
            faceOutputConfigDirectory: config, competingFaceHost: host, packageProvidesBackup: () => bundled)
        { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-6000, -6000) };

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static HubOutputRouteSwitch OpenRouteSwitch(HubForm form)
    {
        var choice = Field<HubOutputRouteSwitch>(form, "_outputRouteSetup");
        if (!choice.Visible)
        {
            Descendants(form).OfType<Button>().Single(button => button.AccessibleName == "First-time setup").PerformClick();
            Descendants(form).OfType<Button>().Single(button => button.AccessibleName == "Setup section: VRChat output").PerformClick();
        }
        Wait(() => choice.Visible && choice.Enabled, "The VRChat output switch was unavailable in setup.");
        return choice;
    }

    private static RadioButton Segment(HubOutputRouteSwitch choice, bool backup)
    {
        string name = backup ? "VRCFaceTracking backup" : "Direct to VRChat";
        return Descendants(choice).OfType<RadioButton>().Single(button => button.AccessibleName == name || button.Text == name);
    }

    private static bool ShowsRoute(HubOutputRouteSwitch choice, int index) =>
        choice.SelectedIndex == index && Segment(choice, false).Checked == (index == 0) && Segment(choice, true).Checked == (index == 1);

    private static void TestOutputRouteOwnership(string root)
    {
        root = Directory.CreateDirectory(Path.Combine(root, "full-bundle")).FullName;
        var config = Path.Combine(root, "handoff");
        bool host = false;
        using var fixture = new FeedFixture();
        using (var form = RouteForm(root, config, fixture, () => host))
        {
            form.Show();
            fixture.MakeGameVisible();
            Wait(() => fixture.Feeds.Count == 1 && fixture.ReceiveJaw(.35f), "Direct did not send the fixture jaw.");
            Check(FaceOutputOwnership.TryAcquireModule(config) is null, "The module could acquire ownership while direct was active.");
            var previous = fixture.Feeds.Single();
            var choice = OpenRouteSwitch(form);
            Check(ShowsRoute(choice, 0), "Fresh startup did not select only Direct to VRChat.");
            previous.HoldRefresh();
            try
            {
                Wait(() => previous.RefreshPending, "The ownership handoff missed the held discovery.");
                Segment(choice, true).PerformClick();
                Wait(() => previous.RefreshCancelled, "Choosing backup did not cancel direct discovery.");
                Check(!choice.Enabled && !previous.Disposed && FaceOutputOwnership.ReadRoute(config) == FaceOutputRoute.Unconfigured &&
                    FaceOutputOwnership.TryAcquireModule(config) is null, "Backup became available before the outgoing source finished.");
            }
            finally { previous.AllowRefresh(); }
            Wait(() => previous.Disposed && FaceOutputOwnership.ReadRoute(config) == FaceOutputRoute.Vrcft && choice.Enabled && ShowsRoute(choice, 1),
                "The explicit backup did not finish taking over.");
            fixture.AssertPortReleased();
            Check(fixture.ReceiveJaw(0f), "The outgoing direct output did not release its owned jaw.");
            using (var module = FaceOutputOwnership.TryAcquireModule(config))
            {
                Check(module is not null, "The module could not open after the handoff.");
                Segment(choice, false).PerformClick();
                Wait(() => choice.Enabled, "The refused switch remained busy.");
                Check(ShowsRoute(choice, 1) && FaceOutputOwnership.ReadRoute(config) == FaceOutputRoute.Vrcft && fixture.Attempts == 1,
                    "A running module gave up its preference or direct duplicated its reader.");
            }
            form.Close();
        }

        using (var form = RouteForm(root, config, fixture, () => host))
        {
            form.Show();
            Wait(() => Field<bool>(form, "_nativeIdleShown"), "The reopened backup Hub never received Shown.");
            var choice = OpenRouteSwitch(form);
            Check(ShowsRoute(choice, 1) && fixture.Attempts == 1, "Relaunch lost backup and opened direct.");
            host = true;
            var check = (Task)Call(form, "CheckVrchatConnectionAsync",
                (Func<CancellationToken, Task<VrchatOscTarget?>>)(_ => throw new Exception("Backup invoked direct discovery.")))!;
            Complete(check, "Backup connection check did not finish.");
            Check(Field<Label>(form, "_directVrchatStatus").Text.Contains("VRCFaceTracking"), "Backup check returned a direct status.");
            host = false;
            Complete((Task)Call(form, "ObserveOutputOwnershipAsync")!, "Backup host exit observation did not finish.");
            Check(Field<Label>(form, "_directVrchatStatus").Text.Contains("Open VRCFaceTracking") && fixture.Attempts == 1,
                "Closing VRCFaceTracking left a verified backup status or silently started direct.");
            host = true;
            Segment(choice, false).PerformClick();
            Check(ShowsRoute(choice, 1) && fixture.Attempts == 1, "Return to direct admitted a competing legacy host.");
            host = false;
            foreach (var flag in new[] { "_tongue", "_gaze", "_pupil", "_hybridHands", "_controllerTouchpad" })
                Field<CheckBox>(form, flag).Checked = false;
            Field<CheckBox>(form, "_eyebrowBoost").Checked = true;
            Complete((Task)Call(form, "StartTrackingAsync")!, "Backup adjustment startup stalled.");
            Check(Field<object?>(form, "_backupFaceSession") is not null && fixture.Attempts == 1,
                "A backup-only adjustment opened a direct writer or failed to start.");
            Check(FaceOutputOwnership.TryAcquireExtras(config) is null, "Two copies of Companion could start backup face adjustments.");
            var backup = Field<object>(form, "_backupFaceSession");
            var presence = backup.GetType().GetField("_presence", Private)!;
            foreach (var flag in new[] { "_eyebrowBoost", "_individualCheekPuff", "_individualCheekSuck" })
                Field<CheckBox>(form, flag).Checked = false;
            Check(presence.GetValue(backup) is null && Field<Button>(form, "_stop").Enabled,
                "Unchecking the final backup adjustment kept the module's overrides active or hid Stop.");
            Field<CheckBox>(form, "_eyebrowBoost").Checked = true;
            Check(presence.GetValue(backup) is not null, "Re-enabling backup adjustments did not notify the module.");
            using (var other = RouteForm(root, config, fixture, () => false))
            {
                other.Show();
                Wait(() => Field<bool>(other, "_nativeIdleShown"), "The second backup Hub never received Shown.");
                var otherSwitch = OpenRouteSwitch(other);
                Segment(otherSwitch, false).PerformClick();
                Check(ShowsRoute(otherSwitch, 1) && FaceOutputOwnership.ReadRoute(config) == FaceOutputRoute.Vrcft && fixture.Attempts == 1,
                    "Another Hub changed the route during active backup extras.");
                other.Close();
            }
            using (var peer = FaceOutputOwnership.TryAcquireTransition(config))
            {
                Check(peer is not null, "The Hub incorrectly owned the module receiver during backup.");
                FaceOutputOwnership.SaveRoute(FaceOutputRoute.Direct, config);
            }
            Complete((Task)Call(form, "ObserveOutputOwnershipAsync")!, "External route change did not release backup extras.");
            Wait(() => ShowsRoute(choice, 0) && fixture.Attempts == 2, "Direct did not resume after the external return.");
            Check(Field<object?>(form, "_backupFaceSession") is null, "The backup adjustments survived a route change.");
            host = true;
            Complete((Task)Call(form, "ObserveOutputOwnershipAsync")!, "A late legacy host did not pause direct output.");
            fixture.AssertPortReleased();
            Check(fixture.Feeds.Last().Disposed && Field<bool>(form, "_offerBackup"), "Competing legacy host did not release the writer and offer backup.");
            host = false;
            Complete((Task)Call(form, "ObserveOutputOwnershipAsync")!, "Direct did not resume when the other host closed.");
            Wait(() => fixture.Attempts == 3 && fixture.ReceiveJaw(.35f), "The recovered direct writer did not send OSC.");
            form.Close();
            Wait(() => fixture.Feeds.Last().Disposed, "Close kept the resumed direct receiver.");
            Wait(() => { using var lease = FaceOutputOwnership.TryAcquireTransition(config); return lease is not null; },
                "Close retained the direct ownership file.");
        }
        Console.WriteLine("PASS direct/backup preference, delayed handoff and neutral, owner exclusion, restart, backup checks and adjustments, external change and legacy host");
    }

    private static void TestInvalidRouteAndHostConflict(string root)
    {
        var config = Directory.CreateDirectory(Path.Combine(root, "invalid")).FullName;
        File.WriteAllText(Path.Combine(config, "face-output-route.txt"), "unsupported");
        using var fixture = new FeedFixture();
        using var form = RouteForm(root, config, fixture, () => false, bundled: false);
        form.Show();
        Wait(() => Field<bool>(form, "_nativeIdleShown"), "The Hub never received Shown for an invalid route.");
        var choice = OpenRouteSwitch(form);
        Check(ShowsRoute(choice, -1) && fixture.Attempts == 0, "A malformed route selected an output or opened a receiver.");
        Segment(choice, false).PerformClick();
        Wait(() => fixture.Attempts == 1 && FaceOutputOwnership.ReadRoute(config) == FaceOutputRoute.Direct, "An explicit direct selection did not repair the invalid route.");
        Segment(choice, true).PerformClick();
        Check(ShowsRoute(choice, 0) && FaceOutputOwnership.ReadRoute(config) == FaceOutputRoute.Direct && !fixture.Feeds[0].Disposed,
            "An incomplete package dropped the working writer to switch to a missing backup.");
        form.Close();
        Wait(() => fixture.Feeds[0].Disposed, "Closing the repaired route leaked its source.");
        IDisposable? transition = null;
        Wait(() => (transition = FaceOutputOwnership.TryAcquireTransition(config)) is not null, "The repaired form still owns the direct output.");
        using (transition) FaceOutputOwnership.SaveRoute(FaceOutputRoute.Vrcft, config);
        using var reopened = RouteForm(root, config, fixture, () => false, bundled: false);
        reopened.Show();
        Wait(() => Field<bool>(reopened, "_nativeIdleShown"), "The direct-only Hub never received Shown.");
        Check(Field<Label>(reopened, "_directVrchatStatus").Text.Contains("does not include") && fixture.Attempts == 1,
            "Reopening backup in a direct-only package hid its missing bundle or silently started direct.");
        var reopenedSwitch = OpenRouteSwitch(reopened);
        Check(ShowsRoute(reopenedSwitch, 1), "A missing bundle changed the saved backup switch position.");
        Segment(reopenedSwitch, false).PerformClick();
        Wait(() => ShowsRoute(reopenedSwitch, 0) && FaceOutputOwnership.ReadRoute(config) == FaceOutputRoute.Direct && fixture.Attempts == 2,
            "Selecting Direct after reopening backup failed to start one receiver and save the switch position.");
        reopened.Close();
        Wait(() => fixture.Feeds.Last().Disposed, "Closing after an explicit return to Direct retained the receiver.");
        Wait(() => { using var lease = FaceOutputOwnership.TryAcquireTransition(config); return lease is not null; },
            "Closing after an explicit return retained face ownership.");
        using var direct = RouteForm(root, config, fixture, () => false, bundled: false);
        direct.Show();
        Wait(() => fixture.Attempts == 3, "Relaunching the saved direct route did not start its receiver.");
        Check(ShowsRoute(OpenRouteSwitch(direct), 0) && fixture.Feeds.Count(source => !source.Disposed) == 1,
            "Relaunching direct did not restore the matching switch position and exactly one writer.");
        direct.Close();
        Wait(() => fixture.Feeds.Last().Disposed, "The reopened direct receiver survived close.");
        Wait(() => { using var lease = FaceOutputOwnership.TryAcquireTransition(config); return lease is not null; },
            "The reopened direct writer retained ownership after close.");
        Console.WriteLine("PASS invalid route recovery, missing-bundle restart and direct-only package protection");
    }
}
