using System.Diagnostics;

namespace NoteVault;

/// <summary>
/// Auto-refreshing. The single place that answers "is this actually working?". Its one
/// control is Pause, which releases every watched folder so repos can be renamed or moved.
/// Closing it does not quit the app.
/// </summary>
public sealed class StatusForm : Form
{
    private static readonly Color Ink = Color.FromArgb(0x1F, 0x29, 0x37);
    private static readonly Color Dim = Color.FromArgb(0x6B, 0x72, 0x80);
    private static readonly Color Red = Color.FromArgb(0x99, 0x1B, 0x1B);
    private static readonly Color Green = Color.FromArgb(0x15, 0x7F, 0x3C);
    private static readonly Color Amber = Color.FromArgb(0xB4, 0x53, 0x09);
    private static readonly Color Hair = Color.FromArgb(0xE5, 0xE7, 0xEB);
    private static readonly Color Wash = Color.FromArgb(0xF9, 0xFA, 0xFB);

    private readonly AppState _state;
    private readonly Config _cfg;
    private readonly Engine _engine;
    // Initialised here rather than in the constructor: setting Width/Height there raises
    // OnResize, which reads the timer before the constructor body would have created it.
    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly Label _headline = new();
    private readonly Button _pause = new();
    private readonly TableLayoutPanel _stats = new();
    private readonly Dictionary<string, Label> _values = new(StringComparer.Ordinal);
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 350, ReshowDelay = 100 };

    /// <summary>
    /// Four nested loops, each looking for a different kind of change. Spelling them out
    /// here because "refresh / discovery / repo scan" means nothing without the nesting.
    /// </summary>
    private static readonly Dictionary<string, string> Explain = new(StringComparer.Ordinal)
    {
        ["VAULT"] = "The git repository holding every captured note.\nOpen it with any git client — nothing here is a custom format.",
        ["COMMITS"] = "Commits in the vault. One commit per save cluster,\nnot per file.",
        ["SIZE"] = "On-disk size of the vault, including git history.",
        ["QUEUE"] = "Captures waiting to be written to the vault.\nAnything above 0 for long means the writer is stuck.",
        ["LAST CAPTURE"] = "When a note was last committed to the vault.",

        ["NEXT REFRESH"] = "Looks for: a .notes folder appearing or vanishing in a worktree it already knows about.\n\n"
                         + "One Directory.Exists per known root — it never looks inside a folder.\n"
                         + "A backstop only: folder watchers normally catch this within a second or two.",

        ["NEXT DISCOVERY"] = "Looks for: new or removed WORKTREES inside repositories it already knows about.\n\n"
                           + "Runs 'git worktree list' once per repo, so it is the expensive one.\n"
                           + "Nothing else finds a newly added worktree, which is why it still runs.",

        ["NEXT REPO SCAN"] = "Looks for: newly cloned REPOSITORIES under your scan roots.\n\n"
                           + "Lists directory names down to maxDepth and stops at every .git,\n"
                           + "so repository contents are never walked. Typically ~18 listings, ~10 ms.\n"
                           + "A backstop only: folder watchers catch a new clone within a second or two.",
    };

    private readonly ListView _roots = new();
    private readonly ListView _errors = new();
    private readonly Label _errorsHeader = new();

    private static readonly string[] RowOne =
        { "VAULT", "COMMITS", "SIZE", "QUEUE" };

    private static readonly string[] RowTwo =
        { "LAST CAPTURE", "NEXT REFRESH", "NEXT DISCOVERY", "NEXT REPO SCAN" };

    public StatusForm(AppState state, Config cfg, Engine engine)
    {
        _state = state;
        _cfg = cfg;
        _engine = engine;

        Text = "note-vault";
        Width = 980;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIcons.Normal;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        MinimumSize = new Size(760, 480);
        DoubleBuffered = true;

        Controls.Add(BuildRootsPanel());
        Controls.Add(BuildErrorsPanel());
        Controls.Add(BuildHeader());

        _timer.Interval = Math.Max(1, _cfg.Tray.StatusRefreshSeconds) * 1000;
        _timer.Tick += (_, _) => Refresh_();

        // Not started here: OnVisibleChanged owns the timer. Closing this window only
        // hides it, so a timer started in the constructor would keep refreshing an
        // invisible window every few seconds for the rest of the session.
        Refresh_();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateTimer();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateTimer();   // minimising counts as not looking
    }

    private void UpdateTimer()
    {
        var looking = Visible && WindowState != FormWindowState.Minimized;
        if (looking == _timer.Enabled) return;

        if (looking)
        {
            Refresh_();   // show current state immediately, not after the first interval
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    // ---------------------------------------------------------------- layout

    private Control BuildHeader()
    {
        var host = new Panel { Dock = DockStyle.Top, Height = 138, BackColor = Wash };

        _stats.Dock = DockStyle.Fill;
        _stats.Padding = new Padding(14, 4, 14, 0);
        _stats.ColumnCount = 4;
        _stats.RowCount = 4;
        for (var i = 0; i < 4; i++)
            _stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        foreach (var h in new[] { 18F, 26F, 18F, 26F })
            _stats.RowStyles.Add(new RowStyle(SizeType.Absolute, h));

        AddStatRow(0, RowOne);
        AddStatRow(2, RowTwo);

        _headline.Dock = DockStyle.Fill;
        _headline.Padding = new Padding(18, 12, 18, 0);
        _headline.Font = new Font("Segoe UI", 12F, FontStyle.Regular);

        _pause.Dock = DockStyle.Top;
        _pause.Height = 28;
        _pause.FlatStyle = FlatStyle.System;
        _pause.Click += (_, _) => TogglePause();
        _tips.SetToolTip(_pause,
            "Pause closes every folder note-vault watches. Windows will not rename or move a\n"
            + "folder while something inside it is open, so pause before renaming a repo.\n\n"
            + "Nothing is captured while paused. Resume rescans for moved repos and\n"
            + "catches up on every edit made in the meantime. Restarting the app resumes.");

        var pauseHost = new Panel { Dock = DockStyle.Right, Width = 126, Padding = new Padding(0, 9, 16, 0) };
        pauseHost.Controls.Add(_pause);

        var headlineRow = new Panel { Dock = DockStyle.Top, Height = 40 };
        headlineRow.Controls.Add(_headline);
        headlineRow.Controls.Add(pauseHost);

        host.Controls.Add(_stats);
        host.Controls.Add(headlineRow);

        var rule = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Hair };
        var wrapper = new Panel { Dock = DockStyle.Top, Height = 139 };
        wrapper.Controls.Add(host);
        wrapper.Controls.Add(rule);
        return wrapper;
    }

    private void AddStatRow(int row, IReadOnlyList<string> captions)
    {
        for (var col = 0; col < captions.Count; col++)
        {
            var caption = new Label
            {
                Text = captions[col],
                Dock = DockStyle.Fill,
                ForeColor = Dim,
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                Margin = new Padding(4, 0, 4, 0),
                TextAlign = ContentAlignment.BottomLeft,
            };

            var value = new Label
            {
                Text = "—",
                Dock = DockStyle.Fill,
                ForeColor = Ink,
                Font = new Font("Segoe UI", 11F),
                Margin = new Padding(4, 0, 4, 0),
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true,
            };

            _stats.Controls.Add(caption, col, row);
            _stats.Controls.Add(value, col, row + 1);
            _values[captions[col]] = value;

            if (Explain.TryGetValue(captions[col], out var tip))
            {
                _tips.SetToolTip(caption, tip);
                _tips.SetToolTip(value, tip);
            }
        }
    }

    private Control BuildRootsPanel()
    {
        ConfigureList(_roots);
        _roots.Columns.Add("repo / worktree", 430);
        _roots.Columns.Add("files", 70, HorizontalAlignment.Right);
        _roots.Columns.Add("last capture", 190);
        _roots.Columns.Add("state", 130);
        _roots.Dock = DockStyle.Fill;
        _roots.DoubleClick += OpenSelectedRootFolder;

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        panel.Controls.Add(_roots);
        return panel;
    }

    private Control BuildErrorsPanel()
    {
        _errorsHeader.Dock = DockStyle.Top;
        _errorsHeader.Height = 26;
        _errorsHeader.Padding = new Padding(10, 6, 10, 0);
        _errorsHeader.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);

        ConfigureList(_errors);
        _errors.Columns.Add("when", 130);
        _errors.Columns.Add("scope", 240);
        _errors.Columns.Add("what happened", 560);
        _errors.Dock = DockStyle.Fill;

        var panel = new Panel { Dock = DockStyle.Bottom, Height = 165, Padding = new Padding(8, 0, 8, 8) };
        panel.Controls.Add(_errors);
        panel.Controls.Add(_errorsHeader);

        var rule = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Hair };
        var wrapper = new Panel { Dock = DockStyle.Bottom, Height = 166 };
        wrapper.Controls.Add(panel);
        wrapper.Controls.Add(rule);
        return wrapper;
    }

    private static void ConfigureList(ListView lv)
    {
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.GridLines = false;
        lv.HideSelection = false;
        lv.BorderStyle = BorderStyle.None;
        lv.Font = new Font("Segoe UI", 9F);
        lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
    }

    // --------------------------------------------------------------- refresh

    private void Refresh_()
    {
        var roots = _state.Roots;
        var errors = _state.Errors.Snapshot();
        var hasError = _state.Errors.HasAny;

        var paused = _state.Paused;
        var watching = roots.Count(r => r.State == RootState.Watching);
        var repos = roots.Select(r => r.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Paused outranks errors: it is the one state the user chose, and forgetting it
        // means silently capturing nothing.
        _headline.Text = paused
            ? "●  Paused — no folders are held open; nothing is being captured"
            : hasError
                ? $"●  {errors.Count} thing(s) need attention"
                : $"●  Watching {watching} notes folder(s) across {repos} repo(s)";
        _headline.ForeColor = paused ? Amber : hasError ? Red : Green;

        var buttonText = paused ? "Resume" : "Pause";
        if (_pause.Text != buttonText) _pause.Text = buttonText;

        Set("VAULT", _state.VaultPath);
        Set("COMMITS", _state.CommitCount.ToString("N0"));
        Set("SIZE", VaultPaths.HumanBytes(_state.VaultBytes));
        Set("QUEUE", Volatile.Read(ref _state.QueueDepth) + " pending");

        Set("LAST CAPTURE", VaultPaths.Ago(_state.LastCapture));
        Set("NEXT REFRESH", paused ? "paused" : VaultPaths.Until(_state.NextTick));
        Set("NEXT DISCOVERY", paused ? "paused" : VaultPaths.Until(_state.NextDiscovery));
        Set("NEXT REPO SCAN", paused ? "paused" : VaultPaths.Until(_state.NextScan));

        SyncRoots(roots);
        SyncErrors(errors);
    }

    private void Set(string key, string value)
    {
        if (!_values.TryGetValue(key, out var label)) return;
        if (label.Text != value) label.Text = value;   // avoid needless repaints
    }

    /// <summary>
    /// Updates rows in place whenever the set of rows is unchanged, which is almost
    /// always. Clearing and rebuilding a ListView resets its scroll position and
    /// selection, which makes a 3-second auto-refresh unusable.
    /// </summary>
    private void SyncRoots(IReadOnlyList<NoteRoot> roots)
    {
        var ordered = roots
            .OrderBy(r => r.State == RootState.Retired ? 1 : 0)
            .ThenBy(r => r.Alias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.WorktreeName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (SameRows(_roots, ordered))
        {
            for (var i = 0; i < ordered.Count; i++) ApplyRoot(_roots.Items[i], ordered[i]);
            return;
        }

        RebuildPreservingView(_roots, () =>
        {
            foreach (var r in ordered)
            {
                var item = new ListViewItem(new[] { r.Key, "", "", "" }) { Name = r.Key, Tag = r };
                ApplyRoot(item, r);
                _roots.Items.Add(item);
            }
        });
    }

    private void ApplyRoot(ListViewItem item, NoteRoot r)
    {
        item.Tag = r;

        var paused = _state.Paused && r.State == RootState.Watching;

        var state = r.State switch
        {
            RootState.Watching => paused ? "paused" : "watching",
            RootState.NoNotes => "no " + _cfg.NotesDirName,
            RootState.Retired => "retired",
            _ => "error",
        };

        SetSub(item, 0, r.Key);
        SetSub(item, 1, r.FileCount.ToString());
        SetSub(item, 2, r.LastCapture is null ? "—" : VaultPaths.Ago(r.LastCapture));
        SetSub(item, 3, state);

        var colour = r.State switch
        {
            RootState.Retired => Dim,
            RootState.NoNotes => Dim,
            RootState.Error => Red,
            _ => paused ? Dim : Ink,
        };
        if (item.ForeColor != colour) item.ForeColor = colour;
    }

    private void SyncErrors(IReadOnlyList<ErrorEntry> errors)
    {
        var header = errors.Count == 0
            ? "errors — none (the expected steady state)"
            : $"errors — {errors.Count}";

        if (_errorsHeader.Text != header) _errorsHeader.Text = header;
        _errorsHeader.ForeColor = errors.Count == 0 ? Dim : Red;

        var keys = errors.Select(e => e.Kind + "|" + e.Scope).ToList();
        if (SameKeys(_errors, keys))
        {
            for (var i = 0; i < errors.Count; i++) ApplyError(_errors.Items[i], errors[i]);
            return;
        }

        RebuildPreservingView(_errors, () =>
        {
            foreach (var e in errors)
            {
                var item = new ListViewItem(new[] { "", "", "" }) { Name = e.Kind + "|" + e.Scope };
                ApplyError(item, e);
                _errors.Items.Add(item);
            }
        });
    }

    private void ApplyError(ListViewItem item, ErrorEntry e)
    {
        SetSub(item, 0, e.When.ToString("HH:mm:ss"));
        SetSub(item, 1, e.Scope);
        SetSub(item, 2, e.Message);
        if (item.ForeColor != Red) item.ForeColor = Red;
    }

    private static void SetSub(ListViewItem item, int index, string text)
    {
        while (item.SubItems.Count <= index) item.SubItems.Add("");
        if (item.SubItems[index].Text != text) item.SubItems[index].Text = text;
    }

    private static bool SameRows(ListView lv, IReadOnlyList<NoteRoot> roots)
    {
        if (lv.Items.Count != roots.Count) return false;
        for (var i = 0; i < roots.Count; i++)
            if (!string.Equals(lv.Items[i].Name, roots[i].Key, StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool SameKeys(ListView lv, IReadOnlyList<string> keys)
    {
        if (lv.Items.Count != keys.Count) return false;
        for (var i = 0; i < keys.Count; i++)
            if (!string.Equals(lv.Items[i].Name, keys[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// The row set genuinely changed, so a rebuild is unavoidable — but the scroll
    /// offset and selection are restored afterwards so the view does not jump.
    /// </summary>
    private static void RebuildPreservingView(ListView lv, Action rebuild)
    {
        var topIndex = 0;
        try { topIndex = lv.TopItem?.Index ?? 0; } catch { }

        var selected = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Name : null;

        lv.BeginUpdate();
        try
        {
            lv.Items.Clear();
            rebuild();
        }
        finally
        {
            lv.EndUpdate();
        }

        if (selected is not null)
        {
            var match = lv.Items[selected];
            if (match is not null) { match.Selected = true; match.Focused = true; }
        }

        if (topIndex > 0 && lv.Items.Count > 0)
        {
            try { lv.TopItem = lv.Items[Math.Min(topIndex, lv.Items.Count - 1)]; } catch { }
        }
    }

    // ----------------------------------------------------------------- misc

    private void TogglePause()
    {
        if (_engine.Paused) _engine.Resume();
        else _engine.Pause();
        Refresh_();
    }

    private void OpenSelectedRootFolder(object? sender, EventArgs e)
    {
        if (_roots.SelectedItems.Count == 0) return;
        if (_roots.SelectedItems[0].Tag is not NoteRoot root) return;

        var target = Directory.Exists(root.VaultAbsPath) ? root.VaultAbsPath : _cfg.VaultTree;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open " + target, ex);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window must never take the app down.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
