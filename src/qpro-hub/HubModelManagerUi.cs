using System.Text.Json.Nodes;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private void BindModelDeleteAction(Button button)
    {
        var tooltip = new ToolTip(_uiComponents);
        void RefreshAvailability()
        {
            if (IsDisposed || Disposing || button.IsDisposed || button.Disposing) return;
            var selected = _modelList.SelectedItem as FileChoice;
            var version = selected is null ? 0 : VersionFromPath(selected.Primary);
            var protectedModel = version > 0 && HubModelMetadata.IsBundledDeveloper(version, ReadModelMetadata(version));
            button.Enabled = selected is not null && !protectedModel;
            button.AccessibleDescription = protectedModel
                ? "The bundled developer model stays available and cannot be deleted."
                : selected is null ? "Select a local model to delete." : "Delete this model after confirmation.";
            tooltip.SetToolTip(button, button.AccessibleDescription);
        }
        EventHandler changed = (_, _) => RefreshAvailability();
        _modelList.SelectedIndexChanged += changed;
        button.Disposed += (_, _) => _modelList.SelectedIndexChanged -= changed;
        RefreshAvailability();
    }

    private Control BuildModelManagerDetails()
    {
        var details = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Inset,
            Padding = new Padding(16),
            Margin = Padding.Empty,
            AccessibleName = "Selected tongue model details",
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var heading = SectionCaption("SELECTED MODEL");
        heading.Tag = "responsive-info";
        heading.Margin = new Padding(0, 0, 0, 8);
        details.Controls.Add(heading);

        var name = Info("Select a model");
        name.ForeColor = Color.White;
        name.Font = new Font(UiFontName, 11F, FontStyle.Bold);
        name.UseMnemonic = false;
        name.Margin = new Padding(0, 0, 0, 12);
        details.Controls.Add(name);

        var facts = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label Fact(string caption)
        {
            var row = facts.RowCount++;
            facts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = FieldLabel(caption);
            label.Dock = DockStyle.None;
            label.TextAlign = ContentAlignment.TopLeft;
            label.Margin = new Padding(0, 0, 14, 8);
            var value = Info("—");
            value.UseMnemonic = false;
            value.Margin = new Padding(0, 0, 0, 8);
            facts.Controls.Add(label, 0, row);
            facts.Controls.Add(value, 1, row);
            return value;
        }
        var version = Fact("Version");
        var kind = Fact("Kind");
        var status = Fact("Status");
        details.Controls.Add(facts);

        var useHeading = SectionCaption("USE AND LIMITS");
        useHeading.Tag = "responsive-info";
        useHeading.Margin = new Padding(0, 4, 0, 6);
        details.Controls.Add(useHeading);
        var guidance = Info("Choose a model from the list to see its details.");
        guidance.UseMnemonic = false;
        details.Controls.Add(guidance);

        var activation = Info("Selecting here only shows details. To use a model, select it on Facial tracking while tracking is stopped, then press Start tracking.");
        activation.ForeColor = Accent;
        activation.Margin = Padding.Empty;
        details.Controls.Add(activation);

        void RefreshDetails()
        {
            details.SuspendLayout();
            try
            {
                if (_modelList.SelectedItem is not FileChoice selected)
                {
                    name.Text = "Select a model";
                    version.Text = kind.Text = status.Text = "—";
                    status.ForeColor = Muted;
                    guidance.Text = "Choose a model from the list to see its details. Import a .qptonguemodel package if the list is empty.";
                    return;
                }

                // Read only the selected model's friendly sidecar. Browsing this
                // list never selects a tracking model or rewrites its metadata.
                var modelVersion = VersionFromPath(selected.Primary);
                var metadata = modelVersion > 0 ? ReadModelMetadata(modelVersion) : null;
                var experimental = selected.IsExperimental || HubModelMetadata.IsExperimental(metadata);
                var mustachio = selected.HasMoustacheIcon || HubModelMetadata.IsMustachio(metadata);
                var developer = HubModelMetadata.IsDeveloper(metadata);
                var bundled = HubModelMetadata.IsBundledDeveloper(modelVersion, metadata);
                var origin = metadata?["origin"] is JsonValue originValue &&
                    originValue.TryGetValue<string>(out var originText) ? originText : null;
                var personal = origin is "quick refinement" or "legacy diagonal-only refinement"
                    or "focused diagonal and facial hair refinement" or "full personal dataset";
                var imported = origin == "imported package";

                name.Text = modelVersion > 0
                    ? ModelFriendlyName(modelVersion, metadata)
                    : CleanDisplayName(selected.Label);
                version.Text = modelVersion > 0 ? $"v{modelVersion}" : "Unavailable";
                kind.Text = metadata is null ? "Unclassified local model"
                    : bundled ? "Bundled developer model"
                    : personal ? "Personal refinement"
                    : imported ? "Imported model"
                    : developer ? "Developer model · local copy"
                    : mustachio ? "Mustachio branch"
                    : "Local model · source unclassified";
                status.Text = experimental ? "Highly experimental"
                    : metadata is null ? "Unknown · metadata unavailable"
                    : "Not marked experimental";
                status.ForeColor = experimental ? Warning : Muted;

                guidance.Text = metadata is null
                    ? "No readable metadata was found. Its source and validation history cannot be confirmed here. Check its response on your own face before use."
                    : bundled
                        ? "Developer baseline for tongue tracking. Quick refinement creates a personal copy for your face."
                        : mustachio
                            ? "Experimental beard/moustache branch. Check its response with your own facial hair and headset fit."
                            : experimental
                                ? "Marked experimental. Check it carefully on your own face; this marker does not establish tracking quality or wearer validation."
                                : "A local tongue model. Metadata alone does not establish its quality for your face; check its response before use.";
            }
            finally { details.ResumeLayout(true); }
        }

        EventHandler selectionChanged = (_, _) => RefreshDetails();
        _modelList.SelectedIndexChanged += selectionChanged;
        details.Disposed += (_, _) => _modelList.SelectedIndexChanged -= selectionChanged;
        RefreshDetails();
        return details;
    }
}
