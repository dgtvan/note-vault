namespace NoteVault;

internal static class Program
{
    private const string SingletonName = @"Local\note-vault-singleton";
    public const string ShutdownEventName = @"Local\note-vault-shutdown";

    [STAThread]
    private static void Main(string[] args)
    {
        using var singleton = new Mutex(true, SingletonName, out var isFirst);
        if (!isFirst)
        {
            // Already running. Silent no-op keeps install.ps1 re-runs harmless.
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var vaultDir = Config.ResolveVaultPath(args);

        Config cfg;
        try
        {
            cfg = Config.Load(vaultDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not read config.yaml in {vaultDir}\n\n{ex.Message}",
                "note-vault", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Log.Init(cfg.LogDir);

        if (!GitRunner.IsGitAvailable())
        {
            MessageBox.Show(
                "git was not found on PATH.\n\nnote-vault stores everything in a git repository, " +
                "so nothing works without it. Install git and start note-vault again.",
                "note-vault", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log.Error("git.exe not found on PATH — exiting");
            return;
        }

        var state = new AppState();
        var engine = new Engine(cfg, state);

        Application.ThreadException += (_, e) =>
            Log.Error("Unhandled UI exception", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Unhandled exception", ex);
        };

        try
        {
            engine.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Engine failed to start", ex);
            MessageBox.Show("note-vault could not start:\n\n" + ex.Message,
                "note-vault", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var tray = new TrayApp(cfg, state, engine);

        // install.ps1 / uninstall.ps1 signal this instead of killing the process,
        // so the pending debounce window is flushed rather than discarded.
        StartShutdownListener(tray);

        Application.Run(tray);
        GC.KeepAlive(singleton);
    }

    private static void StartShutdownListener(TrayApp tray)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var handle = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName);
                handle.WaitOne();
                Log.Info("Shutdown signal received");

                // Marshals onto the UI thread itself; safe whether or not Status was opened.
                tray.RequestQuit();
            }
            catch (Exception ex)
            {
                Log.Error("Shutdown listener failed", ex);
            }
        })
        {
            IsBackground = true,
            Name = "note-vault-shutdown-listener",
        };

        thread.Start();
    }
}
