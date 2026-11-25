using CSiAPIv1;
using System.Globalization;

namespace BeamSchedulePlugin;

internal sealed class BeamScheduleForm : Form
{
    private readonly ComboBox scope;
    private readonly ComboBox story;
    private readonly DataGridView groups;
    private readonly ComboBox spacingMode;
    private readonly TextBox spacing;
    private readonly cSapModel model;
    private readonly TextBox zoneI;
    private readonly TextBox zoneMiddle;
    private readonly TextBox zoneJ;
    private readonly ComboBox reinforcementMode;
    private readonly TextBox cover, barDiameters, maxLayers, verticalSpacing;
    private readonly TextBox linkDiameters, linkLegs, minLinkSpacing, maxLinkSpacing, linkSpacingStep;
    private readonly TextBox revitWorkbook;

    public BeamScheduleForm(cSapModel model)
    {
        this.model = model;
        Text = "RC Beam Schedule - Design Demands";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(780, 760);
        MinimumSize = new Size(620, 560);
        AutoScaleMode = AutoScaleMode.Font;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var notice = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(700, 0),
            Padding = new Padding(0, 0, 0, 10),
            Text = "Draft schedule: torsion is excluded from proposals and ratios. Reinforcement, torsion, "
                + "design freshness and detailing require engineering review."
        };
        layout.Controls.Add(notice, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        layout.Controls.Add(tabs, 0, 1);

        var scopePage = CreatePage("Scope and zones", tabs);
        var scopeSection = CreateSection("Beam selection");
        scopePage.Controls.Add(scopeSection);
        scope = AddChoice(scopeSection, "Include", "All beams", "Selected beams", "Specified groups");
        story = AddChoice(scopeSection, "Level", "All levels");
        foreach (string name in BeamDemandExport.ReadStoryNames(model)) story.Items.Add(name);
        AddNote(scopeSection, "The level filter applies to the chosen beam selection. Levels use ETABS story assignments.");
        AddNote(scopeSection, "Excel includes one summary row per beam and a separate station-results sheet.");
        reinforcementMode = AddChoice(scopeSection, "Reinforcement", "Automatic draft proposals", "Manual entry");

        var groupSection = CreateSection("Specified groups");
        scopePage.Controls.Add(groupSection);
        AddNote(groupSection, "Enter ETABS group names. Beams in multiple groups are exported once.");
        groups = new DataGridView
        {
            Height = 140,
            Dock = DockStyle.Fill,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false
        };
        groups.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ETABS group name" });
        AddWideControl(groupSection, groups);
        scope.SelectedIndexChanged += (_, _) => groups.Enabled = scope.SelectedIndex == 2;
        groups.Enabled = false;

        var zoneSection = CreateSection("Demand zones");
        scopePage.Controls.Add(zoneSection);
        AddNote(zoneSection, "All beams use three positive fractions totaling 1; there is no separate cantilever zoning. Boundaries use clear length between ETABS offsets, not bar cut-off lengths.");
        zoneI = AddText(zoneSection, "I-end zone fraction", "0.25");
        zoneMiddle = AddText(zoneSection, "Middle zone fraction", "0.50");
        zoneJ = AddText(zoneSection, "J-end zone fraction", "0.25");

        var rebarPage = CreatePage("Reinforcement", tabs);
        var barsSection = CreateSection("Longitudinal bars");
        rebarPage.Controls.Add(barsSection);
        cover = AddText(barsSection, "Cover to outside of links (mm)", "35");
        barDiameters = AddText(barsSection, "Allowed bar diameters in preference order (mm)", "16, 20, 25, 32, 40");
        maxLayers = AddText(barsSection, "Maximum number of layers", "3");
        spacingMode = AddChoice(barsSection, "Horizontal spacing basis",
            "Minimum clear spacing", "Minimum centre-to-centre spacing");
        spacing = AddText(barsSection, "Horizontal spacing (mm)", "25");
        verticalSpacing = AddText(barsSection, "Minimum vertical clear spacing (mm)", "25");
        AddNote(barsSection, "Diameters are tried in entered order; single-layer options take precedence. Check spacing and other detailing rules for your project.");

        var linksSection = CreateSection("Shear links");
        rebarPage.Controls.Add(linksSection);
        linkDiameters = AddText(linksSection, "Allowed link diameters in preference order (mm)", "10, 12, 16, 20");
        linkLegs = AddText(linksSection, "Allowed effective leg counts (comma-separated)", "2, 3, 4, 5");
        maxLinkSpacing = AddText(linksSection, "Maximum link spacing (mm)", "300");
        minLinkSpacing = AddText(linksSection, "Minimum link spacing (mm)", "100");
        linkSpacingStep = AddText(linksSection, "Link spacing increment (mm)", "50");
        AddNote(linksSection, "One outer link is two effective legs; each additional hook is one. Check hook anchorage and project detailing rules.");
        reinforcementMode.SelectedIndexChanged += (_, _) => rebarPage.Enabled = reinforcementMode.SelectedIndex == 0;

        var outputPage = CreatePage("Output and review", tabs);
        var outputSection = CreateSection("Schedule output");
        outputPage.Controls.Add(outputSection);
        AddNote(outputSection, "Excel: one row per beam plus station data and area ratios. CSV: station data only.");
        revitWorkbook = AddText(outputSection, "Revit beam schedule (.xlsx)", "");
        var mappingButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var browseRevit = new Button { Text = "Browse...", AutoSize = true };
        browseRevit.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = "Select Revit beam schedule",
                Filter = "Revit beam schedule (*.xlsx)|*.xlsx",
                CheckFileExists = true,
                Multiselect = false
            };
            if (picker.ShowDialog(this) == DialogResult.OK) revitWorkbook.Text = picker.FileName;
        };
        var clearRevit = new Button { Text = "Clear", AutoSize = true };
        clearRevit.Click += (_, _) => revitWorkbook.Clear();
        mappingButtons.Controls.Add(browseRevit);
        mappingButtons.Controls.Add(clearRevit);
        AddWideControl(outputSection, mappingButtons);
        AddNote(outputSection, "Optional, Excel-only geometric mapping (10 mm endpoint tolerance). "
            + "CSV ignores this selection. Leave blank for the existing export. Requires 'RC Beam Schedule' with millimetre endpoint columns.");
        AddNote(outputSection, "Mapping follows nine ordered geometric checks: endpoint, strict axis/split, "
            + "offset, elevation-offset, offset split, end-extension, wide-offset, extended split and chain-to-chain. "
            + "Review passes use the prototype's fixed tolerances, not nearest-neighbor assignment. Revit Marks and group IDs are included.");

        var reviewSection = CreateSection("Information to include");
        outputPage.Controls.Add(reviewSection);
        AddNote(reviewSection, "The export includes beam identification, geometry, station demands, governing combinations, "
            + "torsion demands and ETABS warnings and errors.");
        AddNote(reviewSection, "Torsion is excluded from proposals and ratios and must be designed separately. Missing results are flagged. "
            + "Area ratios do not verify detailing or code compliance.");

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var generate = new Button { Text = "Export schedule", AutoSize = true };
        generate.Click += Generate_Click;
        buttons.Controls.Add(close);
        buttons.Controls.Add(generate);
        layout.Controls.Add(buttons, 0, 2);
        CancelButton = close;
    }

    private void Generate_Click(object? sender, EventArgs e)
    {
        var groupNames = new List<string>();
        if (scope.SelectedIndex == 2)
        {
            groups.EndEdit();
            foreach (DataGridViewRow row in groups.Rows)
            {
                string name = Convert.ToString(row.Cells[0].Value)?.Trim() ?? "";
                if (name.Length > 0) groupNames.Add(name);
            }
            if (groupNames.Count == 0)
            {
                MessageBox.Show(this, "Enter at least one group name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var selection = scope.SelectedIndex switch
        {
            0 => BeamScope.All,
            1 => BeamScope.Selected,
            2 => BeamScope.Groups,
            _ => throw new InvalidOperationException("Choose a beam scope.")
        };
        string? selectedStory = story.SelectedIndex == 0 ? null : (string?)story.SelectedItem;

        using var dialog = new SaveFileDialog
        {
            Title = "Export draft beam schedule",
            FileName = BeamDemandExport.SuggestedFilename(DateTime.Now, selection, selectedStory),
            Filter = "Excel summary and stations (*.xlsx)|*.xlsx|CSV station data only (*.csv)|*.csv",
            DefaultExt = "xlsx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            UseWaitCursor = true;
            bool isWorkbook = Path.GetExtension(dialog.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
            RevitScheduleData? revit = null;
            string revitPath = revitWorkbook.Text.Trim();
            if (isWorkbook && revitPath.Length > 0)
            {
                if (!Path.GetExtension(revitPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Choose a Revit .xlsx workbook, or clear the Revit selection.");
                if (string.Equals(Path.GetFullPath(revitPath), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The export cannot overwrite the source Revit workbook. Choose a different output file.");
                try { revit = RevitScheduleReader.Read(revitPath); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException
                    or ArgumentException or NotSupportedException)
                {
                    throw new InvalidOperationException("Could not read the Revit beam schedule. Correct or clear the selection.\n" + ex.Message, ex);
                }
            }
            ZoneFractions? fractions = isWorkbook
                ? ZoneFractions.Create(ParseFraction(zoneI.Text), ParseFraction(zoneMiddle.Text), ParseFraction(zoneJ.Text))
                : null;
            ReinforcementOptions? options = isWorkbook && reinforcementMode.SelectedIndex == 0 ? ReadReinforcementOptions() : null;
            var report = BeamDemandExport.Read(model, selection, groupNames, selectedStory);
            var summary = fractions == null ? null : BeamScheduleSummary.Create(model, report, fractions, options);
            if (summary != null && revit != null) summary = BeamScheduleSummary.WithRevitMapping(summary, revit);
            DemandScheduleWriter.Write(dialog.FileName, report, summary);
            MessageBox.Show(this, $"Exported {report.BeamCount} beams and {report.StationCount} design stations."
                + (summary?.Mapping is { } mapping ? $"\nRevit: {mapping.Alignment}; {mapping.Pairs.Count} pairs; "
                    + $"{mapping.Links.Count} segment links across nine passes."
                    + $"\nCovered by strict matches or candidates: {mapping.MappedEtabsCount} ETABS / {mapping.MappedRevitCount} Revit beams; "
                    + $"{mapping.Revit.Skipped.Count} invalid Revit rows skipped. See 'Revit mapping' for review warnings." : "")
                + "\nThe schedule is a draft and requires engineering review.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export the schedule:\n{ex.Message}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private ReinforcementOptions ReadReinforcementOptions()
        {
            double c = ParseFraction(cover.Text), horizontal = ParseFraction(spacing.Text), vertical = ParseFraction(verticalSpacing.Text);
            if (c < 0 || !double.IsFinite(c) || horizontal <= 0 || !double.IsFinite(horizontal)
                || vertical <= 0 || !double.IsFinite(vertical))
                throw new InvalidOperationException("Cover must be non-negative and spacing must be positive.");
            int layers = ParsePositiveInt(maxLayers.Text, "maximum layers");
            if (layers > 10) throw new InvalidOperationException("Maximum layers cannot exceed 10.");
            int min = ParsePositiveInt(minLinkSpacing.Text, "minimum link spacing");
            int max = ParsePositiveInt(maxLinkSpacing.Text, "maximum link spacing");
            int step = ParsePositiveInt(linkSpacingStep.Text, "link spacing increment");
            if (min > max) throw new InvalidOperationException("Minimum link spacing cannot exceed maximum link spacing.");
            if (((long)max - min) / step > 10000)
                throw new InvalidOperationException("Link spacing settings produce more than 10001 candidates.");
            int[] legs = ParseList(linkLegs.Text, "link leg counts");
            if (legs.Any(n => n < 2)) throw new InvalidOperationException("Link leg counts must be at least two.");
            return new ReinforcementOptions(c, ParseList(barDiameters.Text, "bar diameters"), layers,
                spacingMode.SelectedIndex == 0, horizontal, vertical,
                ParseList(linkDiameters.Text, "link diameters"), legs, min, max, step);
        }

        private static int[] ParseList(string text, string name)
        {
            int[] values = text.Split(',').Select(value => ParsePositiveInt(value.Trim(), name)).ToArray();
            if (values.Distinct().Count() != values.Length)
                throw new InvalidOperationException($"Duplicate {name} are not allowed.");
            return values;
        }

        private static int ParsePositiveInt(string text, string name)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
                throw new InvalidOperationException($"Enter positive whole numbers for {name}.");
            return value;
        }

    private static double ParseFraction(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return value;
        throw new InvalidOperationException($"Invalid zone fraction: '{text}'.");
    }

    private static FlowLayoutPanel CreatePage(string title, TabControl tabs)
    {
        var tab = new TabPage(title);
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8)
        };
        tab.Controls.Add(content);
        tabs.TabPages.Add(tab);
        content.SizeChanged += (_, _) =>
        {
            int width = Math.Max(200, content.ClientSize.Width - content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (Control child in content.Controls)
            {
                child.Width = width;
            }
        };
        return content;
    }

    private static TableLayoutPanel CreateSection(string title)
    {
        var section = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Width = 680,
            MinimumSize = new Size(480, 0),
            Margin = new Padding(0, 0, 0, 14)
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        var heading = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = new Padding(0, 3, 0, 8)
        };
        AddWideControl(section, heading);
        return section;
    }

    private static TextBox AddText(TableLayoutPanel section, string label, string value)
    {
        var input = new TextBox { Text = value, Dock = DockStyle.Fill };
        AddField(section, label, input);
        return input;
    }

    private static ComboBox AddChoice(TableLayoutPanel section, string label, params string[] choices)
    {
        var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        input.Items.AddRange(choices);
        input.SelectedIndex = 0;
        AddField(section, label, input);
        return input;
    }

    private static void AddField(TableLayoutPanel section, string label, Control input)
    {
        int row = section.RowCount++;
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            MaximumSize = new Size(240, 0),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 6, 5)
        }, 0, row);
        input.Margin = new Padding(0, 3, 0, 3);
        section.Controls.Add(input, 1, row);
    }

    private static void AddNote(TableLayoutPanel section, string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 8)
        };
        AddWideControl(section, label);
    }

    private static void AddWideControl(TableLayoutPanel section, Control control)
    {
        int row = section.RowCount++;
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(control, 0, row);
        section.SetColumnSpan(control, 2);
    }
}
