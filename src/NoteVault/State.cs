using System.Collections.Concurrent;

namespace NoteVault;

public enum RootState
{
    Watching,
    NoNotes,
    Retired,
    Error,
}

public sealed class NoteRoot
{
    public string Alias { get; init; } = "";
    public string RepoPath { get; init; } = "";
    public string WorktreePath { get; init; } = "";
    public string WorktreeName { get; init; } = "";
    public string NotesDirName { get; init; } = "";
    public string NotesPath { get; init; } = "";
    public string VaultRelPath { get; init; } = "";
    public string VaultAbsPath { get; init; } = "";

    /// <summary>Disambiguates one worktree's several notes folders; empty when only one is configured.</summary>
    public string KeySuffix { get; init; } = "";

    public RootState State { get; set; } = RootState.NoNotes;
    public DateTime? LastCapture { get; set; }
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Stable identity for a root: one worktree in one repo (plus which notes folder, if more than one is configured).</summary>
    public string Key => Alias + "/" + WorktreeName + KeySuffix;

    public override string ToString() => Key;
}

/// <summary>
/// The five error conditions that colour the tray icon red. Nothing else does:
/// an icon that goes red for things you would not act on is one you stop reading.
/// </summary>
public enum ErrKind
{
    Watcher,
    Discovery,
    GitCommand,
    FileRead,
    GlobalGitignore,
}

public sealed record ErrorEntry(ErrKind Kind, string Scope, string Message, DateTime When);

/// <summary>
/// Sticky errors, keyed by (kind, scope). They survive until the same operation
/// succeeds again, so a transient failure at 03:00 is still visible at 09:00.
/// </summary>
public sealed class ErrorRegistry
{
    private readonly ConcurrentDictionary<string, ErrorEntry> _errors = new();

    public event Action? Changed;

    private static string KeyOf(ErrKind kind, string scope) => kind + "|" + scope;

    public void Set(ErrKind kind, string scope, string message)
    {
        var key = KeyOf(kind, scope);
        var entry = new ErrorEntry(kind, scope, Trim(message), DateTime.Now);

        var existed = _errors.TryGetValue(key, out var prior);
        _errors[key] = entry;

        if (!existed || prior!.Message != entry.Message)
        {
            Log.Error($"[{kind}] {scope}: {entry.Message}");
            Changed?.Invoke();
        }
    }

    public void Clear(ErrKind kind, string scope)
    {
        if (_errors.TryRemove(KeyOf(kind, scope), out _))
        {
            Log.Info($"[{kind}] {scope}: recovered");
            Changed?.Invoke();
        }
    }

    /// <summary>Drops errors for roots that no longer exist, so red cannot get stuck.</summary>
    public void ClearScopesNotIn(ErrKind kind, ISet<string> liveScopes)
    {
        foreach (var key in _errors.Keys.ToList())
        {
            var entry = _errors[key];
            if (entry.Kind == kind && !liveScopes.Contains(entry.Scope))
                Clear(kind, entry.Scope);
        }
    }

    public bool HasAny => !_errors.IsEmpty;

    public IReadOnlyList<ErrorEntry> Snapshot() =>
        _errors.Values.OrderByDescending(e => e.When).ToList();

    private static string Trim(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > 240 ? s[..240] + "..." : s;
    }
}

/// <summary>Everything the Status window reads. Written by the engine, read by the UI.</summary>
public sealed class AppState
{
    public ErrorRegistry Errors { get; } = new();

    private readonly object _gate = new();
    private List<NoteRoot> _roots = new();

    public IReadOnlyList<NoteRoot> Roots
    {
        get { lock (_gate) return _roots.ToList(); }
    }

    public void SetRoots(List<NoteRoot> roots)
    {
        lock (_gate) _roots = roots;
    }

    private List<string> _trackedFilePatterns = new();

    public IReadOnlyList<string> TrackedFilePatterns
    {
        get { lock (_gate) return _trackedFilePatterns.ToList(); }
    }

    public void SetTrackedFilePatterns(List<string> patterns)
    {
        lock (_gate) _trackedFilePatterns = patterns;
    }

    private List<TrackedFileMatch> _trackedFileMatches = new();

    /// <summary>Every currently-resolved (pattern, worktree) match, for the Status window.</summary>
    public IReadOnlyList<TrackedFileMatch> TrackedFileMatches
    {
        get { lock (_gate) return _trackedFileMatches.ToList(); }
    }

    public void SetTrackedFileMatches(List<TrackedFileMatch> matches)
    {
        lock (_gate) _trackedFileMatches = matches;
    }

    public int QueueDepth;

    // Repo auto-scan telemetry, surfaced in the Status window so the cost of scanning
    // is visible rather than assumed.
    public DateTime? LastScan;
    public long ScanMillis;
    public int ScanListings;
    public int ScannedRepoCount;
    public int WatchedFolderCount;

    // Upper bounds on the next scheduled run of each loop. A folder watcher can wake
    // the loop earlier, so these are ceilings rather than promises.
    public DateTime? NextTick;
    public DateTime? NextDiscovery;
    public DateTime? NextScan;

    public DateTime? LastDiscovery;
    public DateTime? LastReconcile;
    public DateTime? LastCapture;
    public DateTime? LastGc;
    public int CommitCount;
    public long VaultBytes;
    public string VaultPath = "";
    public bool Paused;
}
