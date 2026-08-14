using System.Windows.Forms;

namespace VibeSuperTonic.Launcher.Ui;

/// <summary>
/// Edits <c>pronunciations.json</c>: a list of text-substitution rules applied
/// to every SAPI fragment before it reaches the model. Designed for fixing
/// mispronunciations the model's front-end can't help with — e.g. "NOT" being
/// read as the letters N-O-T, or a glyph like "Bᵠ" being read as a single word.
///
/// Master toggle at top kills all rules without deleting them. Per-row toggle
/// lets the user temporarily disable a single rule. Whole-word matching is on
/// by default (case-sensitive by default) — keeps "NOT" from also rewriting
/// "NOTE" and "CANNOT".
/// </summary>
internal sealed class PronunciationsTab : UserControl
{
    private readonly CheckBox _masterEnabled;
    private readonly DataGridView _grid;
    private readonly Button _save, _removeRow, _testRun;
    private readonly TextBox _testIn, _testOut;
    private readonly Label _pathLabel;

    public PronunciationsTab()
    {
        // Banner row — explains why this tab exists.
        var banner = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(255, 248, 220),
            Text =
                "Fix words the engine mispronounces. Each rule replaces 'Match' with 'Replace' before synthesis.\n" +
                "Examples: \"NOT\" → \"not!\" (avoid letter-spelling), or \"Bᵠ\" → \"B phi\" (spell out odd glyphs).",
        };

        // Master enable + path footer
        var topRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            ColumnCount = 2,
        };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _masterEnabled = new CheckBox
        {
            Text = "Enable pronunciations",
            AutoSize = true,
            Margin = new Padding(8, 8, 8, 0),
            Checked = true,
        };
        topRow.Controls.Add(_masterEnabled, 0, 0);

        _pathLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 4, 8, 0),
            ForeColor = Color.DimGray,
            Text = "",
        };
        topRow.Controls.Add(_pathLabel, 1, 0);

        // Buttons
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(8),
        };
        _save = new Button { Text = "Save", AutoSize = true };
        _save.Click += (_, _) => SaveFromGrid();
        _removeRow = new Button { Text = "Remove selected", AutoSize = true };
        _removeRow.Click += (_, _) => RemoveSelected();
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_removeRow);

        // Test pane — input → apply rules → output. Helps the user verify a
        // rule fires before saving it. Works against the in-grid state, not
        // the saved file, so edits show effects immediately.
        var testBox = new GroupBox
        {
            Text = "Test",
            Dock = DockStyle.Bottom,
            Height = 120,
            Padding = new Padding(8, 18, 8, 8),
        };
        var testGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
        };
        testGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        testGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        testGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        testGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        testGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        testGrid.Controls.Add(new Label { Text = "Input:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        _testIn = new TextBox { Dock = DockStyle.Fill, Text = "What's intentionally NOT auto-wired" };
        testGrid.Controls.Add(_testIn, 1, 0);
        _testRun = new Button { Text = "Apply rules →", AutoSize = true, Margin = new Padding(8, 2, 0, 2) };
        _testRun.Click += (_, _) => RunTest();
        testGrid.Controls.Add(_testRun, 2, 0);

        testGrid.Controls.Add(new Label { Text = "Output:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
        _testOut = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Control };
        testGrid.Controls.Add(_testOut, 1, 1);
        testBox.Controls.Add(testGrid);

        // The grid itself — set up columns by hand so we control widths,
        // edit behavior, and column types (checkbox vs text).
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            BackgroundColor = SystemColors.Window,
        };
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "On",
            Width = 40,
            Resizable = DataGridViewTriState.False,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Match",
            HeaderText = "Match",
            Width = 180,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Replace",
            HeaderText = "Replace with",
            Width = 220,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "WholeWord",
            HeaderText = "Whole word",
            Width = 80,
            Resizable = DataGridViewTriState.False,
            ToolTipText = "On = match only when surrounded by non-letter characters. " +
                          "Off = match anywhere, including inside other words.",
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "CaseSensitive",
            HeaderText = "Case",
            Width = 50,
            Resizable = DataGridViewTriState.False,
            ToolTipText = "On = 'NOT' matches but 'not' and 'Not' don't. Off = match any case.",
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Notes",
            HeaderText = "Notes (optional)",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });

        // Bool cells need an explicit click→commit dance so the user sees the
        // visual flip immediately instead of after focus moves to another cell.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        // Order controls so docks stack correctly (Bottom first, then Fill, etc.)
        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(testBox);
        Controls.Add(topRow);
        Controls.Add(banner);

        LoadIntoGrid();
    }

    private void LoadIntoGrid()
    {
        var cfg = PronunciationsRegistry.Load();
        _masterEnabled.Checked = cfg.Enabled;
        _grid.Rows.Clear();
        foreach (var r in cfg.Rules)
        {
            int idx = _grid.Rows.Add(r.Enabled, r.Match, r.Replace, r.WholeWord, r.CaseSensitive, r.Notes);
            _grid.Rows[idx].Tag = r;
        }
        _pathLabel.Text = $"File: {PronunciationsRegistry.Path}";
    }

    private void RemoveSelected()
    {
        // Collect first because removing during iteration mutates the collection.
        var toRemove = new List<DataGridViewRow>();
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (!row.IsNewRow) toRemove.Add(row);
        foreach (var r in toRemove) _grid.Rows.Remove(r);
    }

    private PronunciationsConfig SnapshotGrid()
    {
        var cfg = new PronunciationsConfig { Enabled = _masterEnabled.Checked };
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            // A row with an empty Match is treated as a placeholder and dropped
            // on save — saves the user from leaving a half-typed rule behind.
            string match = (row.Cells["Match"].Value as string ?? "").Trim();
            if (match.Length == 0) continue;
            cfg.Rules.Add(new PronunciationRule
            {
                Enabled       = AsBool(row.Cells["Enabled"].Value,       true),
                Match         = match,
                Replace       = row.Cells["Replace"].Value as string ?? "",
                WholeWord     = AsBool(row.Cells["WholeWord"].Value,     true),
                CaseSensitive = AsBool(row.Cells["CaseSensitive"].Value, true),
                Notes         = row.Cells["Notes"].Value as string ?? "",
            });
        }
        return cfg;
    }

    private static bool AsBool(object? v, bool fallback) =>
        v is bool b ? b : fallback;

    private void SaveFromGrid()
    {
        try
        {
            var cfg = SnapshotGrid();
            PronunciationsRegistry.Save(cfg);
            LoadIntoGrid(); // re-load so dropped empty rows disappear from the view
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RunTest()
    {
        // Commit any in-progress cell edit so the test reflects what the user
        // has typed, not just what they've tabbed away from.
        _grid.EndEdit();
        var cfg = SnapshotGrid();
        _testOut.Text = cfg.Apply(_testIn.Text ?? "");
    }
}
