using System.Collections.Concurrent;

namespace NoteVault;

/// <summary>
/// Per-root debounce. The window is long (3 s by default) on purpose: AI tooling
/// streams output into notes files, and a short window would commit half-written
/// responses and fill the history with fragments.
/// </summary>
public sealed class Coalescer : IDisposable
{
    private sealed class Pending
    {
        public readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);
        public DateTime LastEvent = DateTime.UtcNow;
        public bool WholeRoot;

        /// <summary>
        /// Set under the lock at the moment this batch is handed off. A writer that
        /// finds it set must start a fresh batch — otherwise an event arriving mid-flush
        /// would be added to an entry nobody will ever read again.
        /// </summary>
        public bool Flushed;
    }

    private readonly ConcurrentDictionary<string, (NoteRoot Root, Pending Pending)> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Threading.Timer _timer;
    private readonly int _debounceMs;
    private readonly Action<NoteRoot, HashSet<string>?, CaptureSource> _dispatch;

    // One-shot timer, armed only while something is pending. A periodic tick would wake
    // the CPU four times a second forever just to inspect an empty dictionary — the
    // classic way a "background" app keeps a laptop out of its deep idle states.
    private readonly object _armGate = new();
    private DateTime _armedForUtc = DateTime.MaxValue;   // MaxValue = disarmed

    public Coalescer(int debounceMs, Action<NoteRoot, HashSet<string>?, CaptureSource> dispatch)
    {
        _debounceMs = debounceMs;
        _dispatch = dispatch;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Makes sure the timer fires no later than <paramref name="dueUtc"/>.</summary>
    private void ArmAt(DateTime dueUtc)
    {
        lock (_armGate)
        {
            if (dueUtc >= _armedForUtc) return;   // already due to fire sooner
            _armedForUtc = dueUtc;
            var ms = Math.Max(10, (long)(dueUtc - DateTime.UtcNow).TotalMilliseconds);
            try { _timer.Change(ms, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
    }

    public void Touch(NoteRoot root, string path) =>
        Record(root, p => p.Paths.Add(path));

    public void TouchWholeRoot(NoteRoot root) =>
        Record(root, p => p.WholeRoot = true);

    private void Record(NoteRoot root, Action<Pending> apply)
    {
        while (true)
        {
            var entry = _pending.GetOrAdd(root.Key, _ => (root, new Pending()));
            lock (entry.Pending)
            {
                if (!entry.Pending.Flushed)
                {
                    apply(entry.Pending);
                    entry.Pending.LastEvent = DateTime.UtcNow;
                    break;
                }
            }

            // Lost the race with a flush: that batch is already on its way. Drop the
            // stale entry (only if it is still that exact entry) and start a new one.
            _pending.TryRemove(new KeyValuePair<string, (NoteRoot, Pending)>(root.Key, entry));
        }

        ArmAt(DateTime.UtcNow.AddMilliseconds(_debounceMs));
    }

    private void Tick()
    {
        // Disarm first, then scan: a Touch landing mid-scan either is seen by the scan
        // below or re-arms the timer itself — either way nothing is stranded.
        lock (_armGate) _armedForUtc = DateTime.MaxValue;

        var nextDue = DateTime.MaxValue;

        foreach (var key in _pending.Keys)
        {
            if (!_pending.TryGetValue(key, out var entry)) continue;

            HashSet<string>? paths;
            lock (entry.Pending)
            {
                if (entry.Pending.Flushed) continue;

                var due = entry.Pending.LastEvent.AddMilliseconds(_debounceMs);
                if (due > DateTime.UtcNow)
                {
                    if (due < nextDue) nextDue = due;   // still settling; come back for it
                    continue;
                }

                // Sealed under the same lock writers take, so no event can slip in
                // after the copy below.
                entry.Pending.Flushed = true;
                paths = TakePaths(entry.Pending);
            }

            _pending.TryRemove(new KeyValuePair<string, (NoteRoot, Pending)>(key, entry));
            _dispatch(entry.Root, paths, CaptureSource.Watch);
        }

        if (nextDue != DateTime.MaxValue) ArmAt(nextDue);
    }

    private static HashSet<string>? TakePaths(Pending p) =>
        p.WholeRoot ? null : new HashSet<string>(p.Paths, StringComparer.OrdinalIgnoreCase);

    /// <summary>Dispatches everything pending immediately — used on shutdown.</summary>
    public void FlushAll()
    {
        foreach (var key in _pending.Keys.ToList())
        {
            if (!_pending.TryRemove(key, out var entry)) continue;

            HashSet<string>? paths;
            lock (entry.Pending)
            {
                if (entry.Pending.Flushed) continue;
                entry.Pending.Flushed = true;
                paths = TakePaths(entry.Pending);
            }

            _dispatch(entry.Root, paths, CaptureSource.Watch);
        }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>One FileSystemWatcher per notes folder. Callbacks enqueue and return.</summary>
public sealed class WatcherSet : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Coalescer _coalescer;
    private readonly AppState _state;
    private readonly SkipList _skip;
    private readonly Action<NoteRoot> _onOverflow;

    public WatcherSet(Coalescer coalescer, AppState state, SkipList skip, Action<NoteRoot> onOverflow)
    {
        _coalescer = coalescer;
        _state = state;
        _skip = skip;
        _onOverflow = onOverflow;
    }

    public void Sync(IReadOnlyList<NoteRoot> roots)
    {
        var want = roots
            .Where(r => r.State == RootState.Watching && Directory.Exists(r.NotesPath))
            .ToDictionary(r => r.Key, r => r, StringComparer.OrdinalIgnoreCase);

        foreach (var key in _watchers.Keys.ToList())
        {
            if (want.ContainsKey(key)) continue;
            try { _watchers[key].Dispose(); } catch { }
            _watchers.Remove(key);
            _state.Errors.Clear(ErrKind.Watcher, key);
        }

        foreach (var (key, root) in want)
        {
            if (_watchers.ContainsKey(key)) continue;
            Attach(root);
        }
    }

    private void Attach(NoteRoot root)
    {
        try
        {
            var w = new FileSystemWatcher(root.NotesPath)
            {
                IncludeSubdirectories = true,

                // Non-paged kernel memory — it cannot be swapped, so smaller is kinder.
                // 16 KB holds ~120 events; a gitignored notes folder never sees the
                // checkout or npm storms the old 64 KB was sized for, and an overflow
                // still just triggers a (cheap) reconcile of this one folder.
                InternalBufferSize = 16 * 1024,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            };

            w.Created += (_, e) => OnChange(root, e.FullPath);
            w.Changed += (_, e) => OnChange(root, e.FullPath);
            w.Renamed += (_, e) => OnChange(root, e.FullPath);   // destination is what matters

            // Deleted is discarded on arrival. The vault is append-only, so there is
            // nothing to record — and this erases the spurious-deletion bug that
            // write-temp-then-rename-over-original save patterns otherwise cause.

            w.Error += (_, e) =>
            {
                _state.Errors.Set(ErrKind.Watcher, root.Key,
                    "watcher error: " + e.GetException().Message);
                _onOverflow(root);
            };

            w.EnableRaisingEvents = true;
            _watchers[root.Key] = w;
            _state.Errors.Clear(ErrKind.Watcher, root.Key);
            Log.Info("Watching " + root.NotesPath);
        }
        catch (Exception ex)
        {
            _state.Errors.Set(ErrKind.Watcher, root.Key, "could not attach watcher: " + ex.Message);
        }
    }

    private void OnChange(NoteRoot root, string fullPath)
    {
        try
        {
            // A directory appearing or being renamed is one event covering many files,
            // so re-walk the root. At notes scale that is roughly a millisecond.
            if (Directory.Exists(fullPath))
            {
                _coalescer.TouchWholeRoot(root);
                return;
            }

            if (_skip.ShouldSkip(fullPath)) return;
            _coalescer.Touch(root, fullPath);
        }
        catch
        {
            // Never let a watcher callback throw: it runs on a threadpool thread
            // that must return immediately or the kernel buffer backs up.
        }
    }

    /// <summary>Closes every watcher, and with it the folder handle. Sync reopens them.</summary>
    public void ReleaseAll()
    {
        foreach (var (key, w) in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            _state.Errors.Clear(ErrKind.Watcher, key);
        }
        _watchers.Clear();
    }

    public void Dispose() => ReleaseAll();
}
