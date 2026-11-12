using System.Drawing;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private Action? _fitOptionalToolsWidth;

    private sealed record OptionalSetupTool(string Name, string Summary, Control Body,
        Label? Status = null, Func<Task>? Opened = null);

    private sealed record OptionalSetupGroup(string Name, params OptionalSetupTool[] Tools);

    private Control BuildOptionalToolsWorkspace(params OptionalSetupGroup[] groups)
    {
        var workspace = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty,
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        workspace.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var categories = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = false, ColumnCount = groups.Length, RowCount = 1,
            Height = 40, Margin = new Padding(0, 6, 0, 10),
        };
        var categoryButtons = new DarkButton[groups.Length];
        var categoryPages = new TableLayoutPanel[groups.Length];
        var fittingCategories = false;
        var fittingWorkspaceWidth = false;
        var columnDefinitions = new Dictionary<TableLayoutPanel, (SizeType Type, float Width)[]>();
        var reservedActionRows = new Dictionary<TableLayoutPanel, Dictionary<int, (SizeType Type, float Height)>>();
        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = groups.Length, Margin = Padding.Empty,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < groups.Length; index++)
        {
            categories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / groups.Length));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var selected = index;
            var category = SecondaryButton(groups[index].Name);
            category.Enabled = true;
            category.AutoSize = false;
            category.Dock = DockStyle.Fill;
            category.Margin = new Padding(index == 0 ? 0 : 4, 0, index == groups.Length - 1 ? 0 : 4, 0);
            category.Padding = new Padding(8, 4, 8, 4);
            category.UseMnemonic = false;
            category.Tag = "optional-tools-category";
            category.AccessibleName = "Optional tools category: " + groups[index].Name;
            category.AccessibleDescription = "Show this group of optional tools. Changing groups does not run a check or installation.";
            category.Click += (_, _) => SelectCategory(selected);
            categoryButtons[index] = category;
            categories.Controls.Add(category, index, 0);

            var page = new BufferedTableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = groups[index].Tools.Length, Margin = Padding.Empty,
                Visible = index == 0,
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            categoryPages[index] = page;
            var expanded = new bool[groups[index].Tools.Length];
            var toolButtons = new DarkButton[groups[index].Tools.Length];
            for (var toolIndex = 0; toolIndex < groups[index].Tools.Length; toolIndex++)
            {
                var tool = groups[index].Tools[toolIndex];
                var chosen = toolIndex;
                page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var row = new BufferedTableLayoutPanel
                {
                    Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 4, 0, 7),
                    Margin = new Padding(0, 0, 0, 5), BackColor = Panel,
                };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (var entry = 0; entry < 3; entry++) row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var show = DisclosureButton("Show " + tool.Name);
                show.Dock = DockStyle.Top;
                show.Margin = Padding.Empty;
                show.Font = new Font(UiFontName, 10F, FontStyle.Bold);
                show.AccessibleName = show.Text;
                show.AccessibleDescription = tool.Summary;
                toolButtons[toolIndex] = show;
                row.Controls.Add(show, 0, 0);
                var summary = tool.Status ?? Info(tool.Summary);
                summary.Tag = "responsive-info";
                summary.Margin = new Padding(24, 0, 8, 5);
                row.Controls.Add(summary, 0, 1);
                tool.Body.Dock = DockStyle.Top;
                tool.Body.Visible = false;
                tool.Body.BackColor = Inset;
                tool.Body.Margin = new Padding(0, 5, 0, 0);
                tool.Body.Padding = new Padding(12);
                tool.Body.Tag = "optional-tool-body";
                tool.Body.AccessibleName = "Optional tool: " + tool.Name;
                row.Controls.Add(tool.Body, 0, 2);
                page.Controls.Add(row, 0, toolIndex);
                show.Click += async (_, _) =>
                {
                    var opening = !expanded[chosen];
                    page.SuspendLayout();
                    try
                    {
                        // Keep expansion state separate from Control.Visible:
                        // a hidden category also hides its expanded child.
                        for (var peer = 0; peer < expanded.Length; peer++)
                        {
                            expanded[peer] = peer == chosen && opening;
                            groups[selected].Tools[peer].Body.Visible = expanded[peer];
                            toolButtons[peer].Text = (expanded[peer] ? "Hide " : "Show ") + groups[selected].Tools[peer].Name;
                            toolButtons[peer].AccessibleName = toolButtons[peer].Text;
                        }
                    }
                    finally { page.ResumeLayout(true); }
                    FitWorkspaceWidth();
                    if (opening && tool.Opened is not null) await tool.Opened();
                };
            }
            content.Controls.Add(page, 0, index);
        }
        workspace.Controls.Add(categories, 0, 0);
        workspace.Controls.Add(content, 0, 1);

        void SelectCategory(int selected)
        {
            content.SuspendLayout();
            try
            {
                for (var index = 0; index < categoryPages.Length; index++)
                {
                    categoryPages[index].Visible = index == selected;
                    categoryButtons[index].BackColor = index == selected ? Selected : Raised;
                    categoryButtons[index].Emphasized = index == selected;
                    categoryButtons[index].AccessibleDescription = index == selected
                        ? "Current optional tools group. Changing groups does not run a check or installation."
                        : "Show this group of optional tools. Changing groups does not run a check or installation.";
                }
            }
            finally { content.ResumeLayout(true); }
            FitWorkspaceWidth();
        }
        void FitCategories()
        {
            if (fittingCategories || IsDisposed || Disposing || !workspace.Visible || WindowState == FormWindowState.Minimized
                || _windowPositionDepth > 0 || _sizeMessageDepth > 0 || categories.ClientSize.Width <= 0) return;
            fittingCategories = true;
            categories.SuspendLayout();
            try
            {
                var scale = DeviceDpi / 96F;
                int Px(int logical) => Math.Max(1, (int)Math.Ceiling(logical * scale));
                var widest = categoryButtons.Max(button => TextRenderer.MeasureText(button.Text, button.Font,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + Px(24));
                var columns = categories.ClientSize.Width >= (widest + Px(8)) * groups.Length ? groups.Length : 1;
                var rows = columns == 1 ? groups.Length : 1;
                var rowHeight = Math.Max(Px(40), (int)Math.Ceiling(categoryButtons[0].Font.GetHeight()) + Px(16));
                categories.ColumnCount = columns;
                categories.RowCount = rows;
                categories.ColumnStyles.Clear();
                categories.RowStyles.Clear();
                for (var column = 0; column < columns; column++)
                    categories.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,
                        categories.ClientSize.Width / columns + (column < categories.ClientSize.Width % columns ? 1 : 0)));
                for (var row = 0; row < rows; row++) categories.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
                for (var index = 0; index < categoryButtons.Length; index++)
                {
                    categories.SetCellPosition(categoryButtons[index], new TableLayoutPanelCellPosition(columns == 1 ? 0 : index, columns == 1 ? index : 0));
                    categoryButtons[index].Margin = columns == 1
                        ? new Padding(0, 0, 0, index == categoryButtons.Length - 1 ? 0 : Px(4))
                        : new Padding(index == 0 ? 0 : Px(4), 0, index == categoryButtons.Length - 1 ? 0 : Px(4), 0);
                }
                categories.Height = rowHeight * rows;
            }
            finally { categories.ResumeLayout(true); fittingCategories = false; }
        }
        void FitWorkspaceWidth()
        {
            var owner = workspace.Parent;
            if (fittingWorkspaceWidth || IsDisposed || Disposing || !workspace.Visible || WindowState == FormWindowState.Minimized
                || _windowPositionDepth > 0 || _sizeMessageDepth > 0
                || owner is null || owner.ClientSize.Width <= owner.Padding.Horizontal) return;
            fittingWorkspaceWidth = true;
            try
            {
                // Auto-sized percent columns can retain an expanded child's
                // old preferred width. Constrain the column before measuring
                // height, using the actual card width rather than that child.
                var available = Math.Max(1, owner.ClientSize.Width - owner.Padding.Horizontal - workspace.Margin.Horizontal);
                FitControlWidth(workspace, available);
                FitCategories();
            }
            finally { fittingWorkspaceWidth = false; }
        }
        void FitControlWidth(Control control, int available)
        {
            available = Math.Max(1, available);
            if (control is Label { AutoSize: true })
                available = Math.Min(available, (int)Math.Ceiling(900 * DeviceDpi / 96F));
            var maximum = new Size(available, control.MaximumSize.Height);
            if (control.MaximumSize != maximum) control.MaximumSize = maximum;
            if (control is Label label && label.AutoSize)
            {
                if (label.MaximumSize != new Size(available, 0)) label.MaximumSize = new Size(available, 0);
                return;
            }
            if (control is not TableLayoutPanel table) return;
            if (table.Width != available) table.Width = available;
            if (ReferenceEquals(table, categories)) return;
            var inner = Math.Max(1, available - table.Padding.Horizontal);
            if (!columnDefinitions.TryGetValue(table, out var definitions))
            {
                var initialScale = DeviceDpi / 96F;
                // Store fixed columns in logical units so a later monitor DPI
                // change does not restore their old physical pixel widths.
                definitions = Enumerable.Range(0, table.ColumnCount).Select(index =>
                    index < table.ColumnStyles.Count
                        ? (table.ColumnStyles[index].SizeType, table.ColumnStyles[index].SizeType == SizeType.Absolute
                            ? table.ColumnStyles[index].Width / initialScale : table.ColumnStyles[index].Width)
                        : (SizeType.Percent, 1F)).ToArray();
                columnDefinitions.Add(table, definitions);
            }
            var widths = new int[definitions.Length];
            var percentTotal = definitions.Where(column => column.Type == SizeType.Percent).Sum(column => column.Width);
            for (var column = 0; column < widths.Length; column++)
            {
                var definition = definitions[column];
                if (definition.Type == SizeType.Absolute) widths[column] = (int)Math.Ceiling(definition.Width * DeviceDpi / 96F);
                else if (definition.Type == SizeType.AutoSize)
                {
                    widths[column] = table.Controls.Cast<Control>().Where(child =>
                        table.GetPositionFromControl(child).Column == column && table.GetColumnSpan(child) == 1)
                        .Select(child => (child is Label label
                            ? TextRenderer.MeasureText(label.Text, label.Font, Size.Empty,
                                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width
                            : child.GetPreferredSize(new Size(inner, 0)).Width) + child.Margin.Horizontal)
                        .DefaultIfEmpty(0).Max();
                }
            }
            var remaining = Math.Max(1, inner - widths.Sum());
            var assignedPercent = 0;
            var percentColumns = definitions.Count(column => column.Type == SizeType.Percent);
            for (var column = 0; column < widths.Length; column++)
            {
                if (definitions[column].Type == SizeType.Percent)
                {
                    percentColumns--;
                    widths[column] = percentColumns == 0 ? Math.Max(1, remaining - assignedPercent)
                        : Math.Max(1, (int)Math.Floor(remaining * definitions[column].Width / Math.Max(1, percentTotal)));
                    assignedPercent += widths[column];
                }
                while (table.ColumnStyles.Count <= column) table.ColumnStyles.Add(new ColumnStyle());
                table.ColumnStyles[column].SizeType = SizeType.Absolute;
                if (table.ColumnStyles[column].Width != widths[column]) table.ColumnStyles[column].Width = widths[column];
            }
            foreach (Control child in table.Controls)
            {
                var position = table.GetPositionFromControl(child);
                var width = position.Column < 0 ? inner : widths.Skip(position.Column).Take(table.GetColumnSpan(child)).Sum();
                FitControlWidth(child, Math.Max(1, width - child.Margin.Horizontal));
            }
            // A collapsed tool can report zero preferred height for fixed-size
            // setup controls. Reserve their rows explicitly before the body is
            // measured, including tables populated with implicit row positions.
            if (table.AutoSize)
            {
                var actionRows = table.Controls.Cast<Control>()
                    .GroupBy(child => table.GetPositionFromControl(child).Row)
                    .Where(row => row.Key >= 0 && row.All(child => child is Button or ComboBox) &&
                        row.Any(child => child.Visible && (child is ComboBox || !child.AutoSize))).ToArray();
                if (!reservedActionRows.TryGetValue(table, out var originalRows))
                    reservedActionRows.Add(table, originalRows = new());
                // Conditional recovery controls must leave no reserved gap when
                // hidden after a saved-runtime scan returns no usable copies.
                foreach (var row in originalRows.Keys.Except(actionRows.Select(row => row.Key)).ToArray())
                {
                    table.RowStyles[row].SizeType = originalRows[row].Type;
                    table.RowStyles[row].Height = originalRows[row].Height;
                    originalRows.Remove(row);
                }
                foreach (var row in actionRows)
                {
                    var actionHeight = (int)Math.Ceiling(42 * DeviceDpi / 96F);
                    foreach (var action in row.Where(child => child is Button && !child.AutoSize))
                        if (action.Height != actionHeight) action.Height = actionHeight;
                    var height = row.Max(child => (child is ComboBox ? Math.Max(child.Height, child.PreferredSize.Height) : actionHeight) + child.Margin.Vertical);
                    while (table.RowStyles.Count <= row.Key) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    var style = table.RowStyles[row.Key];
                    if (!originalRows.ContainsKey(row.Key)) originalRows.Add(row.Key, (style.SizeType, style.Height));
                    style.SizeType = SizeType.Absolute;
                    if (style.Height != height) style.Height = height;
                }
            }
        }
        // Complete this measurement from the setup page's responsive pass after
        // native size dispatch has settled. Minimize must keep wrapping frozen.
        _fitOptionalToolsWidth = FitWorkspaceWidth;
        Control? widthOwner = null;
        void OwnerSizeChanged(object? sender, EventArgs eventArgs) => FitWorkspaceWidth();
        workspace.ParentChanged += (_, _) =>
        {
            if (widthOwner is not null) widthOwner.SizeChanged -= OwnerSizeChanged;
            widthOwner = workspace.Parent;
            if (widthOwner is not null) widthOwner.SizeChanged += OwnerSizeChanged;
            FitWorkspaceWidth();
        };
        workspace.SizeChanged += (_, _) => FitWorkspaceWidth();
        categories.SizeChanged += (_, _) => FitCategories();
        categories.FontChanged += (_, _) => FitCategories();
        workspace.VisibleChanged += (_, _) => { if (workspace.Visible) FitWorkspaceWidth(); };
        SelectCategory(0);
        FitCategories();
        return workspace;
    }
}
