using QproFaceTracking.Hub;

internal static class StockRestoreFixtures
{
    internal static void Run(HubForm form)
    {
        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        var copy = Descendants(form).Single(control => control.Text == "Copy diagnostics");
        var buttons = copy.Parent!.Controls;
        if (buttons[buttons.IndexOf(copy) + 1].Text != "Restore stock VRCFT")
            throw new Exception("The reset action must be beside Copy diagnostics.");

        void Scenario(bool live, bool stopped, bool open, bool helperOk, HubForm.StockRestoreOutcome expected, string expectedCalls)
        {
            var calls = new List<string>();
            var result = HubForm.RunStockRestoreStepsAsync(live,
                () => { calls.Add("stop"); return Task.FromResult(stopped); },
                () => { calls.Add("host"); return open; },
                () => { calls.Add("uninstall"); return Task.FromResult(helperOk); }).GetAwaiter().GetResult();
            if (result != expected || string.Join(",", calls) != expectedCalls)
                throw new Exception($"Unexpected reset sequence: {result}, {string.Join(",", calls)}");
        }
        Scenario(true, false, false, true, HubForm.StockRestoreOutcome.CleanupUnconfirmed, "stop");
        Scenario(true, true, true, true, HubForm.StockRestoreOutcome.HostOpen, "stop,host");
        Scenario(false, false, true, true, HubForm.StockRestoreOutcome.HostOpen, "host");
        Scenario(false, false, false, false, HubForm.StockRestoreOutcome.HelperFailed, "host,uninstall");
        Scenario(true, true, false, true, HubForm.StockRestoreOutcome.Completed, "stop,host,uninstall");
        var result = HubSetupResult.Create("uninstall-vrcft-eye-bridge.ps1", [])!;
        result.Observe("""QPRO_MODULE_UNINSTALL {"removed":1,"restored":0,"remainingQpro":0,"recoveryIssues":[]}""");
        if (result.Failure() is not null || result.UninstalledModuleCounts is not { Removed: 1, Restored: 0 } ||
            !HubForm.StockRestoreSummary(1, 0).Contains("No official module backup was restored."))
            throw new Exception("A missing backup must not be described as restoring an official module.");
        Console.WriteLine("PASS: Activity reset placement, tracking cleanup, host and helper gates, and missing backup feedback");
    }
}
