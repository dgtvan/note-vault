using System.Diagnostics;

namespace NoteVault;

/// <summary>
/// Three menu items and two icon states. Retrieval is plain git — the vault is an
/// ordinary repo, so a browse or restore window here would only re-implement
/// `git log` and `git show`, less well.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly Config _cfg;
    private readonly AppState _state;
    private readonly Engine _engine;
    private readonly NotifyIcon _icon;

    /// <summary>
    /// A handle owned by the UI thread, purely so background threads have something
    /// safe to marshal through. NotifyIcon is not thread-safe and errors are raised
    /// from the discovery loop and the committer.
    /// </summary>
    private readonly Control _marshal;

    private StatusForm? _status;
    private bool _wasError;
    private bool _shuttingDown;

    public TrayApp(Config cfg, AppState state, Engine engine)
    {
        _cfg = cfg;
        _state = state;
        _engine = engine;

        _marshal = new Control();
        _ = _marshal.Handle;   // force handle creation on the UI thread

        var menu = new ContextMenuStrip();
        menu.Items.Add("Status…", null, (_, _) => ShowStatus());
        menu.Items.Add("Open vault folder", null, (_, _) => OpenVault());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitApp());

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Normal,
            Text = "note-vault",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowStatus();

        // Event-driven, not polled. The icon colour only changes when the error set
        // changes, so repaint exactly then — marshalled through _marshal, because the
        // event fires on background threads and NotifyIcon is not thread-safe.
        _state.Errors.Changed += OnErrorsChanged;

        // The tooltip is only ever read while the pointer is over the icon, so compute
        // it then instead of on a timer.
        _icon.MouseMove += (_, _) => UpdateTooltip();

        UpdateIcon();
        UpdateTooltip();
    }

    private void OnErrorsChanged()
    {
        if (_shuttingDown) return;
        try
        {
            if (_marshal.IsHandleCreated) _marshal.BeginInvoke(new Action(UpdateIcon));
        }
        catch (InvalidOperationException) { }   // handle torn down during shutdown (incl. ObjectDisposedException)
    }

    /// <summary>Safe to call from any thread: marshals onto the UI thread first.</summary>
    public void RequestQuit()
    {
        try
        {
            if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
                _marshal.BeginInvoke(new Action(QuitApp));
            else
                QuitApp();
        }
        catch
        {
            QuitApp();
        }
    }

    private void UpdateTooltip()
    {
        if (_shuttingDown) return;

        var tip = _state.Paused
            ? "note-vault — paused; resume from Status"
            : _state.Errors.HasAny
                ? $"note-vault — {_state.Errors.Snapshot().Count} error(s); open Status"
                : $"note-vault — watching {_state.Roots.Count(r => r.State == RootState.Watching)} folder(s)";

        // NotifyIcon truncates past 63 characters.
        tip = tip.Length > 62 ? tip[..62] : tip;
        if (_icon.Text != tip) _icon.Text = tip;
    }

    private void UpdateIcon()
    {
        if (_shuttingDown) return;

        var hasError = _state.Errors.HasAny;
        var want = hasError ? TrayIcons.Error : TrayIcons.Normal;
        if (!ReferenceEquals(_icon.Icon, want)) _icon.Icon = want;

        UpdateTooltip();

        if (hasError && !_wasError && _cfg.Tray.NotifyOnError)
        {
            _icon.BalloonTipTitle = "note-vault";
            _icon.BalloonTipText = "Something needs attention. Open Status for details.";
            _icon.ShowBalloonTip(5000);
        }

        _wasError = hasError;
    }

    private void ShowStatus()
    {
        if (_status is null || _status.IsDisposed)
            _status = new StatusForm(_state, _cfg, _engine);

        _status.Show();
        if (_status.WindowState == FormWindowState.Minimized)
            _status.WindowState = FormWindowState.Normal;

        _status.BringToFront();
        _status.Activate();
    }

    private void OpenVault()
    {
        var target = Directory.Exists(_cfg.VaultTree) ? _cfg.VaultTree : _cfg.VaultDir;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open vault folder", ex);
            MessageBox.Show("Could not open " + target + "\n\n" + ex.Message, "note-vault",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void QuitApp()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        try { _state.Errors.Changed -= OnErrorsChanged; } catch { }

        // Flush the debounce buffer before the process goes away.
        try { _engine.Stop(); } catch (Exception ex) { Log.Error("Stop failed", ex); }
        try { _engine.Dispose(); } catch { }

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { }

        try { _status?.Dispose(); } catch { }
        try { _marshal.Dispose(); } catch { }

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _marshal.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
