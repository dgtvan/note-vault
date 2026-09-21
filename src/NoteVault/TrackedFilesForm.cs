namespace NoteVault;

/// <summary>
/// Lets the user add individual files to track by relative path — e.g. "src/api/.env" —
/// independent of any .notes folder. Each pattern is checked against every discovered
/// worktree, so one entry follows the same relative path across every worktree of a repo
/// (or several repos) that happens to have it, such as a `repo.worktrees\branch` layout.
/// </summary>
public sealed class TrackedFilesForm : Form
{
    private readonly AppState _state;
    private readonly Engine _engine;

    private readonly ListBox _list = new();
    private readonly TextBox _input = new();
    private readonly Button _add = new();
    private readonly Button _remove = new();
    private readonly Label _hint = new();
    private readonly Label _error = new();

    public TrackedFilesForm(AppState state, Engine engine)
    {
        _state = state;
        _engine = engine;

        Text = "note-vault — Tracked files";
        Width = 560;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIcons.Normal;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(420, 320);

        _hint.Text = "Relative path from a worktree's root, e.g. src\\api\\.env\n"
                   + "Checked against every discovered worktree, so one entry can follow the\n"
                   + "same file across many worktrees (or repos) that happen to have it.";
        _hint.Dock = DockStyle.Top;
        _hint.Height = 56;
        _hint.Padding = new Padding(10, 10, 10, 0);
        _hint.ForeColor = Color.FromArgb(0x6B, 0x72, 0x80);

        _list.Dock = DockStyle.Fill;
        _list.Font = new Font("Consolas", 9.5F);
        _list.IntegralHeight = false;

        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
        listPanel.Controls.Add(_list);

        _input.Dock = DockStyle.Fill;
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddCurrentInput();
        };

        _add.Text = "Add";
        _add.AutoSize = true;
        _add.Click += (_, _) => AddCurrentInput();

        _remove.Text = "Remove selected";
        _remove.AutoSize = true;
        _remove.Click += (_, _) => RemoveSelected();

        var inputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            ColumnCount = 2,
            Padding = new Padding(10, 6, 10, 6),
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.Controls.Add(_input, 0, 0);
        inputRow.Controls.Add(_add, 1, 0);

        _error.Dock = DockStyle.Top;
        _error.Height = 20;
        _error.Padding = new Padding(10, 0, 10, 0);
        _error.ForeColor = Color.FromArgb(0x99, 0x1B, 0x1B);

        var removeRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 6, 10, 6),
        };
        removeRow.Controls.Add(_remove);

        Controls.Add(listPanel);
        Controls.Add(removeRow);
        Controls.Add(_error);
        Controls.Add(inputRow);
        Controls.Add(_hint);

        Load += (_, _) => Reload();
    }

    private void Reload()
    {
        _list.Items.Clear();
        foreach (var p in _state.TrackedFilePatterns) _list.Items.Add(p);
    }

    private void AddCurrentInput()
    {
        var text = _input.Text;
        if (!TrackedFiles.TryNormalize(text, out var normalized, out var error))
        {
            _error.Text = error;
            return;
        }

        var current = _state.TrackedFilePatterns.ToList();
        if (!current.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            current.Add(normalized);

        _engine.UpdateTrackedFilePatterns(current);
        _input.Clear();
        _error.Text = "";
        Reload();
    }

    private void RemoveSelected()
    {
        if (_list.SelectedItem is not string selected) return;

        var current = _state.TrackedFilePatterns
            .Where(p => !string.Equals(p, selected, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _engine.UpdateTrackedFilePatterns(current);
        Reload();
    }
}
