namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private const string StockRestoreTitle = "Restore stock VRCFT";
    private const string StockRestoreNext = "Reopen VRCFaceTracking. If your usual source is missing, install or enable its official module in VRCFaceTracking.";

    internal static string StockRestoreSummary(int removed, int restored)
        => (removed == 0 ? "No installed Qpro module was found." : $"Removed {removed} Qpro module {(removed == 1 ? "entry" : "entries")}.") + " " +
           (restored == 0 ? "No official module backup was restored." : $"Restored {restored} saved module {(restored == 1 ? "backup" : "backups")}.");

    internal enum StockRestoreOutcome { CleanupUnconfirmed, HostOpen, HelperFailed, Completed }

    internal static async Task<StockRestoreOutcome> RunStockRestoreStepsAsync(
        bool stopRequired, Func<Task<bool>> stop, Func<bool> hostOpen, Func<Task<bool>> remove)
    {
        if (stopRequired && !await stop()) return StockRestoreOutcome.CleanupUnconfirmed;
        if (hostOpen()) return StockRestoreOutcome.HostOpen;
        return await remove() ? StockRestoreOutcome.Completed : StockRestoreOutcome.HelperFailed;
    }

    private async Task RestoreStockVrcftAsync()
    {
        if (_previewOnly || UtilityActionIsBusy(allowTrackingTransition: true)) return;
        if (_starting || _stopping)
        {
            SetActionFeedback("Wait for tracking", "Tracking is starting or stopping.", "Wait for that operation to finish, then retry Restore stock VRCFT.");
            return;
        }

        bool stopRequired = LiveTrackingRunning || _trackingProcesses.Count != 0;
        if (MessageBox.Show(this,
                (stopRequired ? "First, stop this Hub's live tracking overrides. " : "") +
                "Remove the Qpro tracking modules from VRCFaceTracking on this PC? If there is a verified official module backup and its original location is free, the Hub will restore it. Close VRCFaceTracking and its module helper before continuing.\n\n" +
                "Your VRCFaceTracking settings, other modules, personal models and captures are kept. This does not disable headset Magisk modules.",
                StockRestoreTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        _setupActionRunning = true;
        try
        {
            UpdateControlState();
            var outcome = await RunStockRestoreStepsAsync(stopRequired, TryStopTrackingAsync, VrcftModuleProcessRunning,
                () => RunUtilityAsync(StockRestoreTitle, "uninstall-vrcft-eye-bridge.ps1"));
            if (outcome == StockRestoreOutcome.CleanupUnconfirmed)
                SetActionFeedback("Module reset paused", "The Hub could not confirm tracking stopped cleanly. No module files were removed.",
                    "Check the Stop tracking result in Activity, finish cleanup and retry.", true);
            else if (outcome == StockRestoreOutcome.HostOpen)
            {
                const string detail = "VRCFaceTracking or its ModuleProcess helper is still open. No module files were removed.";
                const string next = "Exit VRCFaceTracking, wait for its helper to close, then retry Restore stock VRCFT.";
                SetActionFeedback("Close VRCFaceTracking", detail, next, true);
                MessageBox.Show(this, detail + "\n\n" + next, StockRestoreTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            // RunUtilityAsync publishes the verified helper result or its specific failure.
        }
        catch (Exception error) { ShowWorkflowFailure(StockRestoreTitle, error); }
        finally { _setupActionRunning = false; UpdateControlState(); }
    }
}
