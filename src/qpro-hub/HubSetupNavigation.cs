using System.Drawing;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private Action<int>? _selectSetupStep;
    private Action? _fitSetupNavigationDpi;
    private int _selectedSetupStep;

    // Static previews select the same view as a user would. Selection never
    // invokes installation, a device helper, or a settings write.
    internal void SelectSetupStepForPreview(int step)
    {
        if (_previewOnly && !IsDisposed && !Disposing)
            _selectSetupStep?.Invoke(Math.Clamp(step, 0, 2));
    }

    private Control BuildGuidedSetupWorkspace(Panel page, Control[] stepCards, Action openFacialTracking)
    {
        foreach (var action in new[] { _reconnectUsbButton, _connectWirelessButton })
            action.VisualStyle = HubButtonStyle.Primary;
        _reconnectUsbButton.Text = "Check USB connection";
        _reconnectUsbButton.AccessibleName = "Check USB connection";
        var workspace = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 0, 14),
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) workspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        workspace.Controls.Add(_setupProgressContainer, 0, 0);

        var steps = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = false, ColumnCount = 3, RowCount = 1,
            Margin = new Padding(0, 0, 0, 12),
        };
        steps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        string[] stepNames = ["VRChat output", "Headset", "PC runtime"];
        var buttons = new DarkButton[stepNames.Length];
        for (var index = 0; index < stepNames.Length; index++)
        {
            var selected = index;
            steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / stepNames.Length));
            var button = SecondaryButton(stepNames[index]);
            button.Enabled = true;
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(index == 0 ? 0 : 4, 0, index == stepNames.Length - 1 ? 0 : 4, 0);
            button.Padding = new Padding(6, 4, 6, 4);
            button.AccessibleName = $"Setup section: {stepNames[index]}";
            button.Click += (_, _) => _selectSetupStep?.Invoke(selected);
            buttons[index] = button;
            steps.Controls.Add(button, index, 0);
        }
        workspace.Controls.Add(steps, 0, 1);

        var body = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3, Margin = Padding.Empty,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < stepCards.Length; index++)
        {
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stepCards[index].Margin = Padding.Empty;
            stepCards[index].Visible = index == 0;
            body.Controls.Add(stepCards[index], 0, index);
        }
        workspace.Controls.Add(body, 0, 2);

        var features = ActionButton("Choose tracking features", (_, _) => openFacialTracking());
        features.AccessibleName = features.Text;
        features.Anchor = AnchorStyles.Right;
        features.Margin = new Padding(0, 8, 0, 0);
        workspace.Controls.Add(features, 0, 3);

        _fitSetupNavigationDpi = () =>
            steps.Height = Math.Max((int)Math.Ceiling(40 * DeviceDpi / 96F), buttons[0].Font.Height + buttons[0].Padding.Vertical);
        _fitSetupNavigationDpi();

        _selectSetupStep = requested =>
        {
            _selectedSetupStep = Math.Clamp(requested, 0, 2);
            body.SuspendLayout();
            try
            {
                for (var index = 0; index < stepCards.Length; index++)
                {
                    stepCards[index].Visible = index == _selectedSetupStep;
                    buttons[index].BackColor = index == _selectedSetupStep ? Selected : Raised;
                    buttons[index].Emphasized = index == _selectedSetupStep;
                    buttons[index].AccessibleDescription = index == _selectedSetupStep
                        ? "Current setup section."
                        : "Show this setup section.";
                }
            }
            finally { body.ResumeLayout(true); }
            page.AutoScrollPosition = Point.Empty;
        };
        _selectSetupStep(0);
        return workspace;
    }

    private static Label MirrorStatus(Label source, Label? mirror = null, string? accessibleName = null)
    {
        mirror ??= new Label { AutoSize = true, Margin = Padding.Empty };
        void UpdateText()
        {
            mirror.Text = source.Text;
            if (accessibleName is not null) mirror.AccessibleDescription = source.Text.TrimStart('○', '●', ' ');
        }
        mirror.AccessibleName = accessibleName;
        UpdateText();
        mirror.ForeColor = source.ForeColor;
        source.TextChanged += (_, _) => UpdateText();
        source.ForeColorChanged += (_, _) => mirror.ForeColor = source.ForeColor;
        return mirror;
    }
}
