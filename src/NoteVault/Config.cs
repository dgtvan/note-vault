using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NoteVault;

public sealed class RepoConfig
{
    public string Path { get; set; } = "";
    public string Alias { get; set; } = "";
}

public sealed class SetupConfig
{
    public bool EnsureGlobalGitignore { get; set; } = true;
}

public sealed class DiscoveryConfig
{
    /// <summary>
    /// How often to re-enumerate worktrees through git — one process per repo, so this
    /// is the expensive one. New repos and new notes folders arrive via watchers, so
    /// this interval only bounds how long a *new worktree* can go unnoticed.
    /// </summary>
    public int PollSeconds { get; set; } = 3600;

    /// <summary>
    /// Cheap upkeep tick: one Directory.Exists per known root, to notice a notes folder
    /// appearing or disappearing. Purely a backstop — folder watchers already catch that
    /// in about a second, and file changes are never detected here at all.
    /// </summary>
    public int TickSeconds { get; set; } = 900;
}

/// <summary>
/// Auto-discovery of repositories under a root folder. This never walks the tree:
/// it enumerates directory names to a bounded depth and stops descending the moment
/// it sees a .git, so the millions of files inside repos are never touched.
/// </summary>
public sealed class ScanConfig
{
    public List<string> Roots { get; set; } = new();

    /// <summary>Depth 3 covers "org/repo" plus the "repo.worktrees/branch" convention.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Watch intermediate folders so a new repo is picked up without polling.</summary>
    public bool WatchForNewRepos { get; set; } = true;

    /// <summary>Slow safety net for anything the watchers miss. 0 disables.</summary>
    public int RescanMinutes { get; set; } = 30;

    /// <summary>Belt and braces: these are pruned by name even above a repo root.</summary>
    public List<string> SkipDirNames { get; set; } = new()
    {
        "node_modules", ".vs", ".vscode", "bin", "obj", "dist", "out",
        "target", "__pycache__", "packages", ".next", "venv", ".venv",
    };
}

public sealed class ReconcileConfig
{
    public bool OnStartup { get; set; } = true;
    public bool OnResume { get; set; } = true;
    public bool OnWatcherError { get; set; } = true;
}

public sealed class CaptureConfig
{
    // Documentation only: append-only is structural, not switchable.
    public bool AppendOnly { get; set; } = true;

    public List<string> SkipTempFiles { get; set; } = new()
    {
        "~$*", "*.tmp", "*.partial", "*.crdownload",
        "*.swp", "*.swo", "*~", ".#*",
        "Thumbs.db", "desktop.ini", ".DS_Store",
    };

    public long LogLargeCaptureBytes { get; set; } = 26214400;
}

public sealed class TrayConfig
{
    public int StatusRefreshSeconds { get; set; } = 3;
    public bool NotifyOnError { get; set; }
}

public sealed class MaintenanceConfig
{
    public bool GcWeekly { get; set; } = true;
}

public sealed class Config
{
    public string Store { get; set; } = @"C:\NoteVault";
    public string NotesDirName { get; set; } = ".notes";
    public int DebounceMs { get; set; } = 3000;

    public SetupConfig Setup { get; set; } = new();
    public List<RepoConfig> Repos { get; set; } = new();
    public ScanConfig Scan { get; set; } = new();
    public List<string> ExtraNotesDirs { get; set; } = new();
    public DiscoveryConfig Discovery { get; set; } = new();
    public ReconcileConfig Reconcile { get; set; } = new();
    public CaptureConfig Capture { get; set; } = new();
    public TrayConfig Tray { get; set; } = new();
    public MaintenanceConfig Maintenance { get; set; } = new();

    public string VaultDir => Store;
    public string VaultTree => System.IO.Path.Combine(Store, "vault");
    public string LogDir => System.IO.Path.Combine(Store, "logs");
    public string RootsJson => System.IO.Path.Combine(Store, "roots.json");
    public string ReposJson => System.IO.Path.Combine(Store, "repos.json");
    public string ConfigPath => System.IO.Path.Combine(Store, "config.yaml");

    /// <summary>
    /// Resolves where the vault lives before the config itself can be read.
    /// Order: --vault argument, the pointer file install.ps1 writes, then the default.
    /// </summary>
    public static string ResolveVaultPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--vault", StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFullPath(args[i + 1]);
        }

        var pointer = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "note-vault", "vault.path");

        if (File.Exists(pointer))
        {
            var text = File.ReadAllText(pointer).Trim();
            if (text.Length > 0) return System.IO.Path.GetFullPath(text);
        }

        return @"C:\NoteVault";
    }

    public static Config Load(string vaultDir)
    {
        var path = System.IO.Path.Combine(vaultDir, "config.yaml");
        Config cfg;

        if (File.Exists(path))
        {
            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            cfg = deserializer.Deserialize<Config>(yaml) ?? new Config();
        }
        else
        {
            cfg = new Config();
        }

        // The vault location is decided outside the file it contains.
        cfg.Store = vaultDir;

        if (cfg.DebounceMs < 250) cfg.DebounceMs = 250;
        if (cfg.Discovery.PollSeconds < 5) cfg.Discovery.PollSeconds = 5;
        if (cfg.Discovery.TickSeconds < 5) cfg.Discovery.TickSeconds = 5;
        if (cfg.Tray.StatusRefreshSeconds < 1) cfg.Tray.StatusRefreshSeconds = 1;
        if (cfg.Scan.MaxDepth < 1) cfg.Scan.MaxDepth = 1;
        if (cfg.Scan.MaxDepth > 8) cfg.Scan.MaxDepth = 8;   // a runaway depth is how this turns into a tree walk
        if (string.IsNullOrWhiteSpace(cfg.NotesDirName)) cfg.NotesDirName = ".notes";

        foreach (var repo in cfg.Repos)
        {
            if (string.IsNullOrWhiteSpace(repo.Alias) && !string.IsNullOrWhiteSpace(repo.Path))
                repo.Alias = SanitizeSegment(new DirectoryInfo(repo.Path.TrimEnd('\\', '/')).Name);
        }

        return cfg;
    }

    public static string SanitizeSegment(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "_" : s;
    }
}
