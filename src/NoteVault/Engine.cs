using Microsoft.Win32;

namespace NoteVault;

public sealed class Engine : IDisposable
{
    private readonly Config _cfg;
    private readonly AppState _state;
    private readonly SkipList _skip;
    private readonly Discovery _discovery;
    private readonly Committer _committer;
    private readonly Coalescer _coalescer;
    private readonly WatcherSet _watchers;
    private readonly RepoScanner _scanner;
    private readonly RepoFolderWatcher _repoFolders;
    private readonly CancellationTokenSource _cts = new();

    private Task? _committerTask;
    private Task? _loopTask;
    private DateTime _lastGcCheck = DateTime.MinValue;
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _lastFullDiscovery = DateTime.MinValue;
    private DateTime _lastSizeCalc = DateTime.MinValue;
    private volatile bool _rescanRequested = true;
    private volatile bool _paused;
    private volatile bool _catchUpAfterPause;
    private bool _repoSetChanged;
    private List<RepoConfig> _effectiveRepos = new();
    private List<string> _intermediateDirs = new();

    /// <summary>
    /// Lets a folder watcher interrupt the poll delay. Without this the watchers would
    /// only save the cost of scanning, not the latency — a new clone would still wait
    /// out the full discovery interval.
    /// </summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>
    /// Serialises opening watchers (the loop thread) against releasing them (Pause, on
    /// the UI thread), so a loop pass already under way cannot reopen what Pause closed.
    /// </summary>
    private readonly object _watchGate = new();

    public Engine(Config cfg, AppState state)
    {
        _cfg = cfg;
        _state = state;
        _state.VaultPath = cfg.VaultDir;

        _skip = new SkipList(cfg.Capture.SkipTempFiles);
        _discovery = new Discovery(cfg, state.Errors);
        _committer = new Committer(cfg, state, _skip, _cts.Token);
        _coalescer = new Coalescer(cfg.DebounceMs, Dispatch);
        _watchers = new WatcherSet(_coalescer, state, _skip, OnWatcherOverflow);
        _scanner = new RepoScanner(cfg);
        _repoFolders = new RepoFolderWatcher(RequestRescan);
    }

    public bool Paused => _paused;

    /// <summary>
    /// Closes every folder watcher. A watcher holds its folder open, and Windows refuses
    /// to rename or move a folder while anything inside it is open — so while running,
    /// every watched repo (and every scan folder above one) is locked in place.
    /// </summary>
    public void Pause()
    {
        lock (_watchGate)
        {
            if (_paused) return;
            _paused = true;
            _watchers.ReleaseAll();
            _repoFolders.ReleaseAll();
            _state.WatchedFolderCount = 0;
        }

        // Edits saved just before pausing still get captured.
        _coalescer.FlushAll();

        _state.Paused = true;
        Log.Info("Paused — all folder watchers released");
    }

    /// <summary>
    /// Nothing was watched while paused, so rescan (a repo may have moved) and reconcile
    /// every root rather than trust that nothing changed.
    /// </summary>
    public void Resume()
    {
        lock (_watchGate)
        {
            if (!_paused) return;
            _paused = false;
        }

        _state.Paused = false;
        _catchUpAfterPause = true;
        Log.Info("Resumed — rescanning and reconciling");
        RequestRescan();
    }

    public void Start()
    {
        VaultSetup.EnsureVault(_cfg, _state.Errors);
        VaultSetup.EnsureGlobalGitignore(_cfg, _state.Errors);

        _committerTask = Task.Run(() => _committer.RunAsync());
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _loopTask = Task.Run(LoopAsync);
        Log.Info("note-vault started; vault at " + _cfg.VaultDir);
    }

    private async Task LoopAsync()
    {
        var first = true;

        while (!_cts.IsCancellationRequested)
        {
            // Paused means hands off the repos entirely: no scan, no git worktree list,
            // no watchers. Resume wakes the loop.
            if (!_paused)
            {
                try
                {
                    MaybeScanForRepos();

                    var catchUp = _catchUpAfterPause;
                    _catchUpAfterPause = false;

                    var fullDue = first
                                  || catchUp
                                  || _repoSetChanged
                                  || (DateTime.UtcNow - _lastFullDiscovery).TotalSeconds >= _cfg.Discovery.PollSeconds;

                    if (fullDue)
                    {
                        RunDiscovery((first && _cfg.Reconcile.OnStartup) || catchUp);
                        _lastFullDiscovery = DateTime.UtcNow;
                        _repoSetChanged = false;
                    }
                    else
                    {
                        // Cheap upkeep: catches a .notes folder appearing in a worktree we
                        // already know about, without re-enumerating every repo through git.
                        RefreshRootStates();
                    }

                    RefreshVaultStats(authoritative: fullDue);
                    MaybeGc();
                }
                catch (Exception ex)
                {
                    Log.Error("Discovery loop iteration failed", ex);
                }

                first = false;
            }

            try
            {
                // Wakes early when a watcher spots a new folder; otherwise this is the
                // cheap upkeep tick, not the discovery interval. Never longer than the
                // discovery interval, or a short pollSeconds could not be honoured.
                var tick = Math.Min(_cfg.Discovery.TickSeconds, Math.Max(5, _cfg.Discovery.PollSeconds));
                PublishSchedule(tick);
                await _wake.WaitAsync(TimeSpan.FromSeconds(tick), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Publishes when each loop is next due, for the Status window.</summary>
    private void PublishSchedule(int tickSeconds)
    {
        _state.NextTick = DateTime.Now.AddSeconds(tickSeconds);

        _state.NextDiscovery = _lastFullDiscovery == DateTime.MinValue
            ? null
            : _lastFullDiscovery.ToLocalTime().AddSeconds(_cfg.Discovery.PollSeconds);

        _state.NextScan = _cfg.Scan.Roots.Count == 0
                          || _cfg.Scan.RescanMinutes <= 0
                          || _lastScan == DateTime.MinValue
            ? null
            : _lastScan.ToLocalTime().AddMinutes(_cfg.Scan.RescanMinutes);
    }

    /// <summary>Flags a rescan and wakes the loop immediately. Safe from any thread.</summary>
    private void RequestRescan()
    {
        _rescanRequested = true;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending; one is enough */ }
    }

    /// <summary>
    /// Bounded, prune-at-.git scan of the configured scan roots. Runs on startup, when a
    /// watched folder reports a new directory, and on the slow rescan interval as a
    /// safety net. It never walks a repository's contents.
    /// </summary>
    private void MaybeScanForRepos()
    {
        var explicitRepos = _cfg.Repos
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .ToList();

        if (_cfg.Scan.Roots.Count == 0)
        {
            _effectiveRepos = explicitRepos;
            return;
        }

        var due = _rescanRequested
                  || (_cfg.Scan.RescanMinutes > 0
                      && (DateTime.UtcNow - _lastScan).TotalMinutes >= _cfg.Scan.RescanMinutes);

        if (!due && _effectiveRepos.Count > 0) return;

        _rescanRequested = false;
        _lastScan = DateTime.UtcNow;

        var outcome = _scanner.Scan();

        _state.LastScan = DateTime.Now;
        _state.ScanMillis = outcome.Millis;
        _state.ScanListings = outcome.Listings;
        _state.ScannedRepoCount = outcome.Repos.Count;

        // Explicit entries win, so a hand-set alias is never overridden by a scan.
        var merged = new List<RepoConfig>(explicitRepos);
        var seen = new HashSet<string>(
            explicitRepos.Select(r => Path.GetFullPath(r.Path.TrimEnd('\\', '/'))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var repo in outcome.Repos)
        {
            if (seen.Add(Path.GetFullPath(repo.Path.TrimEnd('\\', '/'))))
                merged.Add(repo);
        }

        // A changed repo set forces a full discovery even if the hour is not up yet.
        var before = _effectiveRepos.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = merged.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!before.SetEquals(after)) _repoSetChanged = true;

        _effectiveRepos = merged;
        _intermediateDirs = outcome.IntermediateDirs;

        Log.Info($"Repo scan: {outcome.Repos.Count} repo(s) from {outcome.Listings} directory " +
                 $"listing(s) in {outcome.Millis} ms; {_effectiveRepos.Count} total after merge");
    }

    /// <summary>
    /// Re-evaluates only what is cheap to check: whether each known root's notes folder
    /// exists yet. This is what keeps an hourly discovery interval safe — creating a
    /// .notes folder is picked up on the next tick, or immediately via a folder watcher,
    /// rather than waiting out the full interval.
    /// </summary>
    private void RefreshRootStates()
    {
        var roots = _state.Roots;
        if (roots.Count == 0) return;

        var appeared = new List<NoteRoot>();

        foreach (var root in roots)
        {
            if (root.State == RootState.Retired) continue;

            var exists = Directory.Exists(root.NotesPath);

            if (exists && root.State != RootState.Watching)
            {
                root.State = RootState.Watching;
                appeared.Add(root);
            }
            else if (!exists && root.State == RootState.Watching)
            {
                root.State = RootState.NoNotes;
            }
        }

        SyncAllWatchers(roots);

        foreach (var root in appeared)
        {
            Log.Info($"{root.Key}: notes folder appeared — capturing");
            _committer.Post(new CaptureRequest { Root = root, Source = CaptureSource.Discover, Paths = null });
        }
    }

    private void SyncAllWatchers(IReadOnlyList<NoteRoot> roots)
    {
        lock (_watchGate)
        {
            if (_paused) return;   // Pause released them mid-pass; leave them closed
            _watchers.Sync(roots);
            SyncFolderWatchers(roots);
        }
    }

    /// <summary>
    /// Watches the folders where something we care about could appear: the directories
    /// the repo scan descended into (a new repo), and worktrees that have no notes
    /// folder yet (a new .notes). Both wake the loop immediately.
    /// </summary>
    private void SyncFolderWatchers(IReadOnlyList<NoteRoot> roots)
    {
        var targets = new List<(string Dir, string? NameFilter)>();

        // Scan folders: any new directory could be a new repo, so no filter.
        if (_cfg.Scan.WatchForNewRepos)
            targets.AddRange(_intermediateDirs.Select(d => (d, (string?)null)));

        // Roots with no notes folder yet: watch its parent, filtered to exactly the notes
        // folder's name. Holds for extraNotesDirs too, whose parent is not a worktree.
        foreach (var root in roots)
        {
            if (root.State != RootState.NoNotes) continue;

            var parent = Path.GetDirectoryName(root.NotesPath.TrimEnd('\\', '/'));
            var name = Path.GetFileName(root.NotesPath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) continue;
            if (Directory.Exists(parent)) targets.Add((parent, name));
        }

        _repoFolders.Sync(targets);

        if (_repoFolders.Count != _state.WatchedFolderCount)
        {
            var filtered = targets.Count(t => t.NameFilter is not null);
            Log.Info($"Folder watchers: {_repoFolders.Count} " +
                     $"({targets.Count - filtered} for new repos, {filtered} for new notes folders)");
        }
        _state.WatchedFolderCount = _repoFolders.Count;
    }

    private void RunDiscovery(bool reconcileAll)
    {
        var before = _state.Roots.ToDictionary(r => r.Key, r => r.State, StringComparer.OrdinalIgnoreCase);

        var roots = _discovery.Discover(_effectiveRepos);
        _state.SetRoots(roots);
        _state.LastDiscovery = DateTime.Now;

        SyncAllWatchers(roots);

        var newlyRetired = new List<string>();
        var newlyDiscovered = new List<NoteRoot>();

        foreach (var root in roots)
        {
            var known = before.TryGetValue(root.Key, out var priorState);

            if (root.State == RootState.Retired)
            {
                if (!known || priorState != RootState.Retired) newlyRetired.Add(root.Key);
                continue;
            }

            if (root.State != RootState.Watching) continue;

            // A newly discovered notes folder is walked in full immediately, so nothing
            // created during the discovery poll gap is missed.
            if (!known || priorState != RootState.Watching)
                newlyDiscovered.Add(root);
        }

        foreach (var root in newlyDiscovered)
            _committer.Post(new CaptureRequest { Root = root, Source = CaptureSource.Discover, Paths = null });

        if (reconcileAll)
        {
            foreach (var root in roots.Where(r => r.State == RootState.Watching))
            {
                if (newlyDiscovered.Any(n => n.Key == root.Key)) continue;
                _committer.Post(new CaptureRequest { Root = root, Source = CaptureSource.Reconcile, Paths = null });
            }
            _state.LastReconcile = DateTime.Now;
        }

        if (newlyRetired.Count > 0)
        {
            _discovery.SaveRecords(roots);
            _committer.Post(new CaptureRequest
            {
                Kind = RequestKind.Metadata,
                Note = "roots: retired " + string.Join(", ", newlyRetired),
            });
            Log.Info("Retired: " + string.Join(", ", newlyRetired));
        }
        else
        {
            _discovery.SaveRecords(roots);
        }

        foreach (var root in roots)
            root.FileCount = CountVaultFiles(root.VaultAbsPath);

        // roots.json remembers per-root capture times, so "last capture" survives a
        // restart instead of reading as "never" next to a vault full of commits.
        var newest = roots
            .Where(r => r.LastCapture.HasValue)
            .Select(r => r.LastCapture!.Value)
            .DefaultIfEmpty(default)
            .Max();

        if (newest != default && (_state.LastCapture is null || newest > _state.LastCapture))
            _state.LastCapture = newest;
    }

    public void ReconcileNow()
    {
        foreach (var root in _state.Roots.Where(r => r.State == RootState.Watching))
            _committer.Post(new CaptureRequest { Root = root, Source = CaptureSource.Reconcile, Paths = null });

        _state.LastReconcile = DateTime.Now;
    }

    private void OnWatcherOverflow(NoteRoot root)
    {
        if (!_cfg.Reconcile.OnWatcherError) return;
        Log.Warn("Watcher error on " + root.Key + " — reconciling that root");
        _committer.Post(new CaptureRequest { Root = root, Source = CaptureSource.Reconcile, Paths = null });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        if (!_cfg.Reconcile.OnResume) return;
        if (_paused) return;   // Resume() reconciles everything anyway
        Log.Info("Resumed from sleep — reconciling all roots");
        ReconcileNow();
    }

    private void Dispatch(NoteRoot root, HashSet<string>? paths, CaptureSource source)
    {
        _committer.Post(new CaptureRequest { Root = root, Paths = paths, Source = source });
    }

    private void RefreshVaultStats(bool authoritative)
    {
        // A git process costs ~85 ms. The committer keeps the count live, so the real
        // recount only needs to ride along with full discovery rather than every wake.
        if (authoritative)
        {
            var count = GitRunner.Run(_cfg.VaultDir, TimeSpan.FromSeconds(30), "rev-list", "--count", "HEAD");
            if (count.Ok && int.TryParse(count.StdOut.Trim(), out var n)) _state.CommitCount = n;
        }

        // Sizing the vault means walking it, which is the one genuinely growing cost here.
        // Every five minutes is plenty for a number displayed in a status window.
        if ((DateTime.UtcNow - _lastSizeCalc).TotalMinutes < 5) return;
        _lastSizeCalc = DateTime.UtcNow;

        _state.VaultBytes = VaultPaths.DirectorySize(Path.Combine(_cfg.VaultDir, ".git"))
                          + VaultPaths.DirectorySize(_cfg.VaultTree);
    }

    private void MaybeGc()
    {
        if (!_cfg.Maintenance.GcWeekly) return;
        if ((DateTime.UtcNow - _lastGcCheck).TotalHours < 6) return;
        _lastGcCheck = DateTime.UtcNow;

        var stamp = Path.Combine(_cfg.VaultDir, ".git", "note-vault-last-gc");
        DateTime last = File.Exists(stamp) && DateTime.TryParse(File.ReadAllText(stamp).Trim(), out var t)
            ? t
            : DateTime.MinValue;

        if (last != DateTime.MinValue) _state.LastGc = last.ToLocalTime();
        if ((DateTime.UtcNow - last).TotalDays < 7) return;

        Log.Info("Running weekly git gc");
        var gc = GitRunner.Run(_cfg.VaultDir, TimeSpan.FromMinutes(10), "gc", "--quiet");
        if (gc.Ok)
        {
            File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
            _state.LastGc = DateTime.Now;
        }
    }

    private static int CountVaultFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Count(f => !Path.GetFileName(f).Equals(".note-vault-root", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Flushes the debounce buffer before exit. Anything still in the window when the
    /// process dies is lost, so this is the mitigation that actually matters.
    /// </summary>
    public void Stop()
    {
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }

        try { _watchers.Dispose(); } catch { }
        try { _repoFolders.Dispose(); } catch { }
        try { _coalescer.FlushAll(); } catch { }
        try { _committer.FlushRemaining(TimeSpan.FromSeconds(8)); } catch { }

        _cts.Cancel();

        try { _committerTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        Log.Info("note-vault stopped");
    }

    public void Dispose()
    {
        try { _coalescer.Dispose(); } catch { }
        try { _wake.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
