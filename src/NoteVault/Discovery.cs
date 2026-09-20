using System.Text;
using System.Text.Json;

namespace NoteVault;

public sealed class RootRecord
{
    public string Alias { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string WorktreePath { get; set; } = "";
    public string WorktreeName { get; set; } = "";
    public string VaultRelPath { get; set; } = "";
    public string FirstSeenUtc { get; set; } = "";
    public string? LastCaptureUtc { get; set; }
    public bool Retired { get; set; }
}

public sealed class Discovery
{
    private readonly Config _cfg;
    private readonly ErrorRegistry _errors;
    private readonly Dictionary<string, RootRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public Discovery(Config cfg, ErrorRegistry errors)
    {
        _cfg = cfg;
        _errors = errors;
        LoadRecords();
    }

    /// <summary>
    /// Asks git for every worktree of every configured repo. Never enumerates a disk:
    /// discovery is the one place a naive design would be tempted to scan D:\Src.
    /// </summary>
    public List<NoteRoot> Discover(IReadOnlyList<RepoConfig> repos)
    {
        var roots = new List<NoteRoot>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveRepoScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repo in repos)
        {
            if (string.IsNullOrWhiteSpace(repo.Path)) continue;
            var scope = repo.Alias;
            liveRepoScopes.Add(scope);

            if (!Directory.Exists(repo.Path))
            {
                _errors.Set(ErrKind.Discovery, scope, "path does not exist: " + repo.Path);
                continue;
            }

            var res = GitRunner.Run(repo.Path, TimeSpan.FromSeconds(30),
                "worktree", "list", "--porcelain");

            if (!res.Ok)
            {
                _errors.Set(ErrKind.Discovery, scope,
                    "git worktree list failed — " + FirstLine(res.Message));
                continue;
            }

            _errors.Clear(ErrKind.Discovery, scope);

            foreach (var wt in ParseWorktrees(res.StdOut))
            {
                var root = BuildRoot(repo.Alias, repo.Path, wt);
                roots.Add(root);
                live.Add(root.Key);
            }
        }

        foreach (var extra in _cfg.ExtraNotesDirs)
        {
            if (string.IsNullOrWhiteSpace(extra)) continue;
            var full = Path.GetFullPath(extra);
            var alias = Config.SanitizeSegment(new DirectoryInfo(full).Name);
            var root = BuildRoot(alias, full, full, isExtra: true);
            roots.Add(root);
            live.Add(root.Key);
        }

        // Errors for repos removed from config must not keep the icon red forever.
        _errors.ClearScopesNotIn(ErrKind.Discovery, liveRepoScopes);

        AddRetiredRoots(roots, live);
        SaveRecords(roots);
        return roots;
    }

    private NoteRoot BuildRoot(string alias, string repoPath, string worktreePath, bool isExtra = false)
    {
        var wtName = Config.SanitizeSegment(
            new DirectoryInfo(worktreePath.TrimEnd('\\', '/')).Name);

        var notesPath = isExtra
            ? worktreePath
            : Path.Combine(worktreePath, _cfg.NotesDirName);

        var relPath = Path.Combine("vault", Config.SanitizeSegment(alias), wtName);
        var absPath = Path.Combine(_cfg.VaultDir, relPath);

        var key = alias + "/" + wtName;
        _records.TryGetValue(key, out var rec);

        return new NoteRoot
        {
            Alias = alias,
            RepoPath = repoPath,
            WorktreePath = worktreePath,
            WorktreeName = wtName,
            NotesPath = notesPath,
            VaultRelPath = relPath,
            VaultAbsPath = absPath,
            State = Directory.Exists(notesPath) ? RootState.Watching : RootState.NoNotes,
            FirstSeenUtc = rec is not null && DateTime.TryParse(rec.FirstSeenUtc, out var fs)
                ? fs
                : DateTime.UtcNow,
            LastCapture = rec?.LastCaptureUtc is not null && DateTime.TryParse(rec.LastCaptureUtc, out var lc)
                ? lc.ToLocalTime()
                : null,
        };
    }

    /// <summary>
    /// A worktree that vanished is retired, never deleted. Its notes stay in the vault
    /// exactly where they are — retirement is a bookkeeping event, not a data event.
    /// </summary>
    private void AddRetiredRoots(List<NoteRoot> roots, HashSet<string> live)
    {
        foreach (var rec in _records.Values)
        {
            if (live.Contains(rec.Alias + "/" + rec.WorktreeName)) continue;

            roots.Add(new NoteRoot
            {
                Alias = rec.Alias,
                RepoPath = rec.RepoPath,
                WorktreePath = rec.WorktreePath,
                WorktreeName = rec.WorktreeName,
                NotesPath = Path.Combine(rec.WorktreePath, _cfg.NotesDirName),
                VaultRelPath = rec.VaultRelPath,
                VaultAbsPath = Path.Combine(_cfg.VaultDir, rec.VaultRelPath),
                State = RootState.Retired,
                FirstSeenUtc = DateTime.TryParse(rec.FirstSeenUtc, out var fs) ? fs : DateTime.UtcNow,
                LastCapture = rec.LastCaptureUtc is not null && DateTime.TryParse(rec.LastCaptureUtc, out var lc)
                    ? lc.ToLocalTime()
                    : null,
            });
        }
    }

    public static IEnumerable<string> ParseWorktrees(string porcelain)
    {
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                var p = line["worktree ".Length..].Trim();
                if (p.Length > 0) yield return Path.GetFullPath(p);
            }
        }
    }

    private void LoadRecords()
    {
        try
        {
            if (!File.Exists(_cfg.RootsJson)) return;
            var json = File.ReadAllText(_cfg.RootsJson);
            var list = JsonSerializer.Deserialize<List<RootRecord>>(json);
            if (list is null) return;
            foreach (var r in list)
                _records[r.Alias + "/" + r.WorktreeName] = r;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read roots.json", ex);
        }
    }

    public void SaveRecords(IEnumerable<NoteRoot> roots)
    {
        try
        {
            foreach (var r in roots)
            {
                _records[r.Key] = new RootRecord
                {
                    Alias = r.Alias,
                    RepoPath = r.RepoPath,
                    WorktreePath = r.WorktreePath,
                    WorktreeName = r.WorktreeName,
                    VaultRelPath = r.VaultRelPath,
                    FirstSeenUtc = r.FirstSeenUtc.ToString("o"),
                    LastCaptureUtc = r.LastCapture?.ToUniversalTime().ToString("o"),
                    Retired = r.State == RootState.Retired,
                };
            }

            var ordered = _records.Values
                .OrderBy(r => r.Alias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.WorktreeName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var json = JsonSerializer.Serialize(ordered,
                new JsonSerializerOptions { WriteIndented = true });

            if (File.Exists(_cfg.RootsJson) && File.ReadAllText(_cfg.RootsJson) == json) return;
            File.WriteAllText(_cfg.RootsJson, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write roots.json", ex);
        }
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s[..i];
    }
}
