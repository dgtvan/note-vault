using System.Text.RegularExpressions;

namespace NoteVault;

/// <summary>
/// Filename-only glob matching. Deliberately never matches against directory
/// segments, so a skip pattern can never take out a whole folder by accident.
/// </summary>
public sealed class SkipList
{
    private readonly List<Regex> _patterns = new();

    public SkipList(IEnumerable<string> globs)
    {
        foreach (var g in globs)
        {
            if (string.IsNullOrWhiteSpace(g)) continue;
            _patterns.Add(new Regex(GlobToRegex(g.Trim()),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
    }

    public bool ShouldSkip(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var re in _patterns)
            if (re.IsMatch(name)) return true;
        return false;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in glob)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return sb.Append('$').ToString();
    }
}

public static class VaultPaths
{
    /// <summary>
    /// A nested ".git" would make git store a gitlink and silently drop the folder's
    /// contents, so the segment is renamed on the way in. The vault README documents
    /// the reverse operation, because there is no restore UI to do it for you.
    /// </summary>
    public const string MangledGit = "_nv_git_";

    /// <summary>
    /// No longer written — worktree identity lives in roots.json at the vault root instead,
    /// so vault/ holds nothing but real captured content. Kept only so a file count doesn't
    /// include one left behind in a worktree folder captured by an older version of the app.
    /// </summary>
    public static readonly string[] LegacyRootMarkerNames = { ".note-vault-root", ".note-vault-root.json" };

    public static string MangleRelative(string relativePath)
    {
        var parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            if (string.Equals(parts[i], ".git", StringComparison.OrdinalIgnoreCase))
                parts[i] = MangledGit;
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Git wants forward slashes in pathspecs regardless of platform.</summary>
    public static string ToGitPath(string path) => path.Replace('\\', '/');

    public static string RelativeTo(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel;
    }

    public static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    public static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    /// <summary>
    /// Countdown to a scheduled moment. These are always upper bounds — a watcher can
    /// wake the loop long before the interval expires.
    /// </summary>
    public static string Until(DateTime? when)
    {
        if (when is null) return "—";
        var span = when.Value - DateTime.Now;
        if (span.TotalSeconds <= 0) return "due now";
        if (span.TotalSeconds < 60) return $"in {(int)span.TotalSeconds} s";
        if (span.TotalMinutes < 60) return $"in {(int)Math.Ceiling(span.TotalMinutes)} min";
        return $"in {span.TotalHours:0.#} h";
    }

    public static string Ago(DateTime? when)
    {
        if (when is null) return "—";
        var span = DateTime.Now - when.Value;
        if (span.TotalSeconds < 60) return $"{(int)Math.Max(0, span.TotalSeconds)} s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
        return when.Value.ToString("yyyy-MM-dd HH:mm");
    }
}
