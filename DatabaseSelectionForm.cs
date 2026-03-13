using System.Drawing;
using System.Windows.Forms;

namespace PcatImageSyncApp;

internal sealed class DatabaseSelectionForm : Form
{
    private readonly CheckedListBox _sourcesList = new();
    private readonly ComboBox _targetCombo = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _selectAllButton = new();
    private readonly Button _clearAllButton = new();

    public DatabaseSelectionForm(IReadOnlyList<CatalogInfo> catalogs)
    {
        Catalogs = catalogs
            .OrderBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SelectedSources = Array.Empty<CatalogInfo>();

        BuildUi();
        LoadCatalogs();
    }

    private IReadOnlyList<CatalogInfo> Catalogs { get; }

    public IReadOnlyList<CatalogInfo> SelectedSources { get; private set; }

    public CatalogInfo? SelectedTarget { get; private set; }

    private void BuildUi()
    {
        SuspendLayout();

        Text = "PCAT Image Sync";
        ClientSize = new Size(900, 620);
        MinimumSize = new Size(900, 620);
        MaximumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 4,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var introLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "Select one or more source .pcat files on the left and exactly one target .pcat file on the right.",
            Padding = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(introLabel, 0, 0);
        root.SetColumnSpan(introLabel, 2);

        var sourcesPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 8, 0),
        };
        sourcesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourcesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        sourcesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var sourcesLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "Source databases (multi-select)",
            Padding = new Padding(0, 0, 0, 6),
        };
        sourcesPanel.Controls.Add(sourcesLabel, 0, 0);

        _sourcesList.CheckOnClick = true;
        _sourcesList.Dock = DockStyle.Fill;
        _sourcesList.HorizontalScrollbar = true;
        _sourcesList.IntegralHeight = false;
        sourcesPanel.Controls.Add(_sourcesList, 0, 1);

        var sourceButtonsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };

        _selectAllButton.AutoSize = true;
        _selectAllButton.Text = "Select all";
        _selectAllButton.Click += (_, _) => SetAllSourceChecks(true);
        sourceButtonsPanel.Controls.Add(_selectAllButton);

        _clearAllButton.AutoSize = true;
        _clearAllButton.Text = "Clear";
        _clearAllButton.Click += (_, _) => SetAllSourceChecks(false);
        sourceButtonsPanel.Controls.Add(_clearAllButton);

        sourcesPanel.Controls.Add(sourceButtonsPanel, 0, 2);

        var targetPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(8, 0, 0, 0),
        };
        targetPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        targetPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        targetPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        targetPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var targetLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "Target database (single-select)",
            Padding = new Padding(0, 0, 0, 6),
        };
        targetPanel.Controls.Add(targetLabel, 0, 0);

        _targetCombo.Dock = DockStyle.Top;
        _targetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _targetCombo.Width = 260;
        targetPanel.Controls.Add(_targetCombo, 0, 1);

        var targetHelp = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "The selected target database will be scanned for missing 32 / 64 / 200 PNG images.",
            Padding = new Padding(0, 10, 0, 0),
        };
        targetPanel.Controls.Add(targetHelp, 0, 3);

        root.Controls.Add(sourcesPanel, 0, 1);
        root.Controls.Add(targetPanel, 1, 1);

        var noteLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "Items marked with [support folder not found] can still be selected, but they will be skipped and logged during processing.",
            Padding = new Padding(0, 10, 0, 10),
        };
        root.Controls.Add(noteLabel, 0, 2);
        root.SetColumnSpan(noteLabel, 2);

        var buttonsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };

        _okButton.AutoSize = true;
        _okButton.Text = "Start";
        _okButton.Click += OkButton_Click;
        buttonsPanel.Controls.Add(_okButton);

        _cancelButton.AutoSize = true;
        _cancelButton.Text = "Cancel";
        _cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        buttonsPanel.Controls.Add(_cancelButton);

        root.Controls.Add(buttonsPanel, 0, 3);
        root.SetColumnSpan(buttonsPanel, 2);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        Controls.Add(root);
        ResumeLayout(performLayout: true);
    }

    private void LoadCatalogs()
    {
        foreach (var catalog in Catalogs)
        {
            _sourcesList.Items.Add(catalog, false);
            _targetCombo.Items.Add(catalog);
        }

        if (_targetCombo.Items.Count > 0)
        {
            _targetCombo.SelectedIndex = 0;
        }
    }

    private void SetAllSourceChecks(bool isChecked)
    {
        for (var index = 0; index < _sourcesList.Items.Count; index++)
        {
            _sourcesList.SetItemChecked(index, isChecked);
        }
    }

    private void OkButton_Click(object? sender, EventArgs e)
    {
        var selectedTarget = _targetCombo.SelectedItem as CatalogInfo;
        var selectedSources = _sourcesList.CheckedItems
            .Cast<CatalogInfo>()
            .ToArray();

        if (selectedTarget is null)
        {
            MessageBox.Show(this, "Please select a target database.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (selectedSources.Length == 0)
        {
            MessageBox.Show(this, "Please select at least one source database.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (selectedSources.Any(source => string.Equals(source.PcatPath, selectedTarget.PcatPath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "The target database cannot also be selected as a source database.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SelectedTarget = selectedTarget;
        SelectedSources = selectedSources;
        DialogResult = DialogResult.OK;
        Close();
    }
}
