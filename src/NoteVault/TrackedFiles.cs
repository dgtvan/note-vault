using System.Text;
using System.Text.Json;

namespace NoteVault;

/// <summary>One tracked-file pattern that resolved to a real file in a live worktree.</summary>
public sealed record TrackedFileMatch(NoteRoot Root, string RelPath, string AbsPath);

/// <summary>
/// User-declared relative file paths (e.g. "src/api/.env"), checked against every
/// discovered worktree rather than one fixed location. One entry follows a file that
/// recurs by convention across many worktrees of a repo (or several repos) — such as
/// the same .env path appearing under every worktree of a `repo.worktrees\branch` set.
///
/// Stored outside config.yaml because, unlike it, this list is written by the app
/// itself (from the tray dialog) rather than hand-edited.
/// </summary>
public static class TrackedFiles
{
    public static List<string> Load(Config cfg)
    {
        try
        {
            if (!File.Exists(cfg.TrackedFilesJson)) return new List<string>();
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cfg.TrackedFilesJson));
            return list?.Select(Normalize).Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read tracked-files.json", ex);
            return new List<string>();
        }
    }

    public static void Save(Config cfg, IEnumerable<string> patterns)
    {
        try
        {
            var ordered = patterns.Select(Normalize)
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(cfg.VaultDir);
            File.WriteAllText(cfg.TrackedFilesJson, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write tracked-files.json", ex);
        }
    }

    /// <summary>
    /// Forward slashes, no leading slash, no drive letter, no ".." segment — a tracked
    /// file is always resolved *under* a worktree, never outside it.
    /// </summary>
    public static bool TryNormalize(string input, out string normalized, out string error)
    {
        normalized = "";
        error = "";

        var s = (input ?? "").Trim().Replace('\\', '/');
        if (s.Length == 0) { error = "Path is empty."; return false; }
        if (Path.IsPathRooted(s) || s.Contains(':')) { error = "Path must be relative, not absolute."; return false; }

        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { error = "Path is empty."; return false; }
        if (parts.Any(p => p == "..")) { error = "Path must not contain '..'."; return false; }

        normalized = string.Join('/', parts);
        return true;
    }

    private static string Normalize(string s) => TryNormalize(s, out var n, out _) ? n : "";

    /// <summary>Every (pattern, live worktree) pair whose file currently exists on disk.</summary>
    public static List<TrackedFileMatch> Resolve(IReadOnlyList<NoteRoot> roots, IReadOnlyList<string> patterns)
    {
        var matches = new List<TrackedFileMatch>();
        if (patterns.Count == 0) return matches;

        foreach (var root in roots)
        {
            if (root.State == RootState.Retired) continue;
            if (string.IsNullOrEmpty(root.WorktreePath) || !Directory.Exists(root.WorktreePath)) continue;

            var wtFull = Path.GetFullPath(root.WorktreePath).TrimEnd('\\', '/');

            foreach (var pattern in patterns)
            {
                string abs;
                try
                {
                    abs = Path.GetFullPath(Path.Combine(wtFull, pattern.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch
                {
                    continue;
                }

                // Belt and braces against a pattern that somehow escaped TryNormalize.
                if (!abs.StartsWith(wtFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(abs))
                    matches.Add(new TrackedFileMatch(root, pattern, abs));
            }
        }

        return matches;
    }
}
