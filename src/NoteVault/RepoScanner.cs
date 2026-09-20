using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NoteVault;

public sealed class DiscoveredRepo
{
    public string Path { get; set; } = "";
    public string Alias { get; set; } = "";
}

public sealed record ScanOutcome(List<RepoConfig> Repos, List<string> IntermediateDirs, int Listings, long Millis);

/// <summary>
/// Finds git repositories under a root folder without ever walking the tree.
///
/// Two rules do all the work:
///   1. Stop descending the moment a directory contains .git. Everything expensive
///      (node_modules, bin, obj) lives *inside* repos, so pruning there never sees it.
///   2. Cap the depth, so a misconfigured root cannot turn into a full scan.
///
/// On a real machine this is ~18 directory listings and a few milliseconds, against
/// 40,000 directories and ~27 seconds for the naive recursive version.
/// </summary>
public sealed class RepoScanner
{
    private readonly Config _cfg;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public RepoScanner(Config cfg)
    {
        _cfg = cfg;
        LoadAliases();
    }

    public ScanOutcome Scan()
    {
        var sw = Stopwatch.StartNew();
        var listings = 0;

        var skip = new HashSet<string>(_cfg.Scan.SkipDirNames, StringComparer.OrdinalIgnoreCase);
        var mainWorktrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var intermediates = new List<string>();

        foreach (var rawRoot in _cfg.Scan.Roots)
        {
            if (string.IsNullOrWhiteSpace(rawRoot)) continue;

            var root = Path.GetFullPath(rawRoot.Trim());
            if (!Directory.Exists(root))
            {
                Log.Warn("Scan root does not exist: " + root);
                continue;
            }

            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                if (depth >= _cfg.Scan.MaxDepth) continue;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(dir);
                    listings++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Cannot list {dir}: {ex.Message}");
                    continue;
                }

                // This directory was descended into rather than being a repo, so it is
                // where a new repo could appear. Watch it instead of re-scanning later.
                intermediates.Add(dir);

                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (skip.Contains(name) || name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var main = ResolveMainWorktree(child);
                    if (main is not null)
                    {
                        mainWorktrees.Add(main);
                        continue;   // prune: never descend into a repo or a worktree
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        sw.Stop();

        var repos = mainWorktrees
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new RepoConfig { Path = p, Alias = AliasFor(p, mainWorktrees) })
            .ToList();

        SaveAliases();
        return new ScanOutcome(repos, intermediates, listings, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Returns the repository's main worktree if <paramref name="dir"/> is a repo or a
    /// linked worktree, else null. Resolving a linked worktree back to its main one
    /// costs a small file read — no git process is spawned anywhere in the scan.
    /// </summary>
    private static string? ResolveMainWorktree(string dir)
    {
        var dotGit = Path.Combine(dir, ".git");

        if (Directory.Exists(dotGit))
            return Path.GetFullPath(dir);   // ordinary repository

        if (!File.Exists(dotGit)) return null;

        // A linked worktree: ".git" is a file reading "gitdir: <main>/.git/worktrees/<name>".
        // Register the main worktree instead; `git worktree list` enumerates the rest,
        // which keeps one repo from being registered several times over.
        try
        {
            var text = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(dir);

            var gitDir = text[prefix.Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(gitDir))
                gitDir = Path.GetFullPath(Path.Combine(dir, gitDir));

            var marker = Path.DirectorySeparatorChar + "worktrees" + Path.DirectorySeparatorChar;
            var idx = gitDir.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return Path.GetFullPath(dir);

            var commonDir = gitDir[..idx];                      // <main>\.git
            var main = Path.GetDirectoryName(commonDir);        // <main>
            return string.IsNullOrEmpty(main) ? Path.GetFullPath(dir) : Path.GetFullPath(main);
        }
        catch
        {
            return Path.GetFullPath(dir);
        }
    }

    /// <summary>
    /// Aliases decide the vault path, so they must never drift: a changed alias would
    /// strand a repo's existing history under the old folder. Once assigned, an alias
    /// is persisted and reused forever.
    /// </summary>
    private string AliasFor(string repoPath, HashSet<string> allRepos)
    {
        if (_aliases.TryGetValue(repoPath, out var existing)) return existing;

        // An explicit repos: entry always wins, so hand-set aliases stay authoritative.
        var configured = _cfg.Repos.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.Path) &&
            string.Equals(Path.GetFullPath(r.Path.TrimEnd('\\', '/')), repoPath, StringComparison.OrdinalIgnoreCase));

        if (configured is not null && !string.IsNullOrWhiteSpace(configured.Alias))
        {
            _aliases[repoPath] = configured.Alias;
            return configured.Alias;
        }

        var taken = new HashSet<string>(_aliases.Values, StringComparer.OrdinalIgnoreCase);
        var leaf = Config.SanitizeSegment(new DirectoryInfo(repoPath.TrimEnd('\\', '/')).Name);

        var candidate = leaf;
        if (taken.Contains(candidate))
        {
            var parent = Path.GetDirectoryName(repoPath.TrimEnd('\\', '/'));
            var parentName = string.IsNullOrEmpty(parent)
                ? "repo"
                : Config.SanitizeSegment(new DirectoryInfo(parent).Name);

            candidate = parentName + "-" + leaf;

            var n = 2;
            while (taken.Contains(candidate)) candidate = parentName + "-" + leaf + "-" + n++;
        }

        _aliases[repoPath] = candidate;
        return candidate;
    }

    private void LoadAliases()
    {
        try
        {
            if (!File.Exists(_cfg.ReposJson)) return;
            var list = JsonSerializer.Deserialize<List<DiscoveredRepo>>(File.ReadAllText(_cfg.ReposJson));
            if (list is null) return;
            foreach (var r in list)
                if (!string.IsNullOrWhiteSpace(r.Path)) _aliases[r.Path] = r.Alias;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read repos.json", ex);
        }
    }

    private void SaveAliases()
    {
        try
        {
            var list = _aliases
                .Select(kv => new DiscoveredRepo { Path = kv.Key, Alias = kv.Value })
                .OrderBy(r => r.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            if (File.Exists(_cfg.ReposJson) && File.ReadAllText(_cfg.ReposJson) == json) return;
            File.WriteAllText(_cfg.ReposJson, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write repos.json", ex);
        }
    }
}

/// <summary>
/// Non-recursive watchers on the folders the scan descended into. A new repo folder
/// raises a DirectoryName event, so discovery is event-driven and the periodic rescan
/// is only a safety net rather than the mechanism.
/// </summary>
public sealed class RepoFolderWatcher : IDisposable
{
    private const int MaxWatchers = 256;

    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    private const int DebounceMs = 1500;

    private readonly Action _onChange;
    private readonly System.Threading.Timer _debounce;

    public RepoFolderWatcher(Action onChange)
    {
        _onChange = onChange;
        _debounce = new System.Threading.Timer(
            _ => { try { _onChange(); } catch (Exception ex) { Log.Error("Repo rescan trigger failed", ex); } },
            null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Count => _watchers.Count;

    /// <param name="targets">
    /// Folders to watch, each with an optional name filter. Scan folders take no filter,
    /// since any new directory there could be a new repo. Repo roots take the notes
    /// folder's name: without it, every npm install, bin/obj or dist created at a repo's
    /// top level would wake the loop and trigger a rescan for nothing.
    /// </param>
    public void Sync(IReadOnlyList<(string Dir, string? NameFilter)> targets)
    {
        var want = targets
            .Take(MaxWatchers)
            .GroupBy(t => t.Dir, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().NameFilter, StringComparer.OrdinalIgnoreCase);

        if (targets.Count > MaxWatchers)
            Log.Warn($"{targets.Count} folders to watch; watching the first {MaxWatchers}. " +
                     "Lower scan.maxDepth or point scan.roots somewhere narrower.");

        foreach (var key in _watchers.Keys.ToList())
        {
            // Re-create a watcher whose filter changed (a root gaining or losing notes).
            if (want.TryGetValue(key, out var filter) && _watchers[key].Filter == (filter ?? "*")) continue;
            try { _watchers[key].Dispose(); } catch { }
            _watchers.Remove(key);
        }

        foreach (var (dir, filter) in want)
        {
            if (_watchers.ContainsKey(dir)) continue;
            if (!Directory.Exists(dir)) continue;

            try
            {
                var w = new FileSystemWatcher(dir, filter ?? "*")
                {
                    IncludeSubdirectories = false,           // names at this level only
                    NotifyFilter = NotifyFilters.DirectoryName,

                    // The documented minimum. These see a handful of directory events a
                    // day, and the buffer is non-paged kernel memory that cannot swap.
                    InternalBufferSize = 4 * 1024,
                };
                w.Created += (_, _) => Fire();
                w.Deleted += (_, _) => Fire();
                w.Renamed += (_, _) => Fire();
                w.Error += (_, _) => Fire();   // overflow: rescan rather than trust the gap
                w.EnableRaisingEvents = true;
                _watchers[dir] = w;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not watch {dir}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Debounce by resetting the timer, never by dropping events. Creating a repo is a
    /// burst — mkdir, then .git, then the notes folder — and a throttle would fire on the
    /// mkdir (when there is still no .git to find) and discard the event that mattered.
    /// </summary>
    private void Fire()
    {
        try { _debounce.Change(DebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Closes every watcher, and with it the folder handle. Sync reopens them.</summary>
    public void ReleaseAll()
    {
        foreach (var w in _watchers.Values)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        try { _debounce.Dispose(); } catch { }
        ReleaseAll();
    }
}
