using System.Text;
using System.Text.RegularExpressions;

namespace NoteVault;

public static class VaultSetup
{
    public static void EnsureVault(Config cfg, ErrorRegistry errors)
    {
        Directory.CreateDirectory(cfg.VaultDir);
        Directory.CreateDirectory(cfg.VaultTree);
        Directory.CreateDirectory(cfg.LogDir);

        var gitDir = Path.Combine(cfg.VaultDir, ".git");
        if (!Directory.Exists(gitDir))
        {
            var init = GitRunner.Run(cfg.VaultDir, "init", "-b", "main");
            if (!init.Ok)
            {
                errors.Set(ErrKind.GitCommand, "vault", "git init failed: " + init.Message);
                return;
            }
            Log.Info("Initialised vault repo at " + cfg.VaultDir);
        }

        // Re-asserted every start: these are what make a hand-run `git show`
        // return bytes identical to what was captured.
        SetLocal(cfg, "core.autocrlf", "false");
        SetLocal(cfg, "core.safecrlf", "false");
        SetLocal(cfg, "core.fileMode", "false");
        SetLocal(cfg, "core.longpaths", "true");
        SetLocal(cfg, "gc.auto", "0");
        SetLocal(cfg, "user.name", "note-vault");
        SetLocal(cfg, "user.email", "note-vault@localhost");

        WriteIfChanged(Path.Combine(cfg.VaultDir, ".gitattributes"),
            "* -text\n");

        WriteIfChanged(Path.Combine(cfg.VaultDir, ".gitignore"),
            "logs/\n");

        WriteIfChanged(Path.Combine(cfg.VaultDir, "README.md"), ReadmeText(cfg));

        // Never replaced once it exists: config.yaml belongs to the user.
        if (!File.Exists(cfg.ConfigPath))
            WriteIfChanged(cfg.ConfigPath, DefaultConfigYaml(cfg));

        var add = GitRunner.Run(cfg.VaultDir, "add", "-f", "--ignore-removal", "--",
            ".gitattributes", ".gitignore", "README.md", "config.yaml");
        if (!add.Ok)
        {
            errors.Set(ErrKind.GitCommand, "vault", "git add (setup) failed: " + add.Message);
            return;
        }

        var dirty = GitRunner.Run(cfg.VaultDir, "diff", "--cached", "--quiet");
        if (dirty.ExitCode == 1)
        {
            var commit = GitRunner.Run(cfg.VaultDir, "commit", "-m",
                "note-vault: vault setup\n\nsource: setup");
            if (!commit.Ok)
                errors.Set(ErrKind.GitCommand, "vault", "git commit (setup) failed: " + commit.Message);
            else
                Log.Info("Committed vault setup files");
        }

        errors.Clear(ErrKind.GitCommand, "vault");
    }

    private static void SetLocal(Config cfg, string key, string value)
    {
        var current = GitRunner.Run(cfg.VaultDir, "config", "--local", "--get", key);
        if (current.Ok && current.StdOut.Trim() == value) return;
        GitRunner.Run(cfg.VaultDir, "config", "--local", key, value);
    }

    private static void WriteIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write " + path, ex);
        }
    }

    /// <summary>
    /// Appends "&lt;notesDir&gt;/" to the user's global gitignore. Deliberately narrow:
    /// one appended line, never overwriting an existing core.excludesFile value.
    /// </summary>
    public static void EnsureGlobalGitignore(Config cfg, ErrorRegistry errors)
    {
        if (!cfg.Setup.EnsureGlobalGitignore) return;

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var get = GitRunner.Run(home, "config", "--global", "--get", "core.excludesFile");
            var excludes = get.Ok ? get.StdOut.Trim() : "";

            if (string.IsNullOrWhiteSpace(excludes))
            {
                excludes = Path.Combine(home, ".gitignore_global");
                var set = GitRunner.Run(home, "config", "--global", "core.excludesFile",
                    excludes.Replace('\\', '/'));
                if (!set.Ok)
                {
                    errors.Set(ErrKind.GlobalGitignore, "global",
                        "could not set core.excludesFile: " + set.Message);
                    return;
                }
                Log.Info("Set core.excludesFile to " + excludes);
            }

            // git accepts ~ in this config value; expand it for our own file IO.
            if (excludes.StartsWith("~"))
                excludes = Path.Combine(home, excludes.TrimStart('~').TrimStart('/', '\\'));

            var entry = cfg.NotesDirName.TrimEnd('/', '\\') + "/";

            Directory.CreateDirectory(Path.GetDirectoryName(excludes)!);
            var lines = File.Exists(excludes)
                ? File.ReadAllLines(excludes).ToList()
                : new List<string>();

            var already = lines.Any(l =>
            {
                var t = l.Trim();
                return t == entry || t == cfg.NotesDirName;
            });

            if (!already)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                    lines.Add("");
                lines.Add(entry);
                File.WriteAllLines(excludes, lines, new UTF8Encoding(false));
                Log.Info($"Appended '{entry}' to {excludes}");
            }

            errors.Clear(ErrKind.GlobalGitignore, "global");
        }
        catch (Exception ex)
        {
            errors.Set(ErrKind.GlobalGitignore, "global", ex.Message);
        }
    }

    private static string ReadmeText(Config cfg)
    {
        var notes = cfg.NotesDirName;
        return $"""
# note-vault

Automatic history of every `{notes}` folder across your repos and worktrees, captured by
note-vault. This is an ordinary git repository — there is no special tooling to install and no
application required to read it. Use whatever git client you already have.

## This repo is APPEND-ONLY

Files are added and updated here. **Nothing is ever deleted.** A file present in this tree may
have been removed from its source folder long ago, and no commit will ever record that removal.

*Absence of a deletion is not evidence the file still exists at the source.*

A consequence worth knowing: renaming a note at the source leaves **both** names here — the old
one frozen at its last content — and `git log --follow` will not connect them, because from
git's point of view nothing was ever deleted.

## `{VaultPaths.MangledGit}` path segments

A folder literally named `.git` inside a notes folder is stored here as `{VaultPaths.MangledGit}`.
Without that rename git would record a submodule pointer and silently drop the folder's entire
contents. **Rename it back when restoring by hand.**

## Do not change these settings

    core.autocrlf = false
    core.safecrlf = false
    * -text          (in .gitattributes)

They exist so that a hand-run `git show` or `git checkout` returns bytes *identical* to what was
captured. Changing them silently corrupts restores of files with mixed or non-native line
endings. note-vault re-asserts them on every start.

## Getting things back

    git log --follow -- vault/<alias>/<worktree>/<path>
    git show <rev>:vault/<alias>/<worktree>/<path> > recovered.txt

## This repository contains unfiltered secrets

Everything under a `{notes}` folder is captured verbatim, with no exclusions except editor
scratch files — including `.env` files, tokens, and keys. Git history is immutable and this
tree is append-only, so a credential captured here stays recoverable after you rotate it.

**Do not add a remote. Do not push this anywhere.** Keep it on an encrypted volume.

## Layout

    vault/<repo-alias>/<worktree-name>/...   mirrored notes
    roots.json                               alias -> source path, active/retired
    config.yaml                              note-vault settings
    logs/                                    not tracked

""";
    }

    /// <summary>
    /// scripts\config.default.yaml, embedded at build time, so the app and install.ps1
    /// seed from the same file. Only used when the vault has no config.yaml yet.
    /// </summary>
    private static string DefaultConfigYaml(Config cfg)
    {
        using var stream = typeof(VaultSetup).Assembly.GetManifestResourceStream("config.default.yaml")
            ?? throw new InvalidOperationException("config.default.yaml was not embedded in the build");
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();

        // Single-quoted like install.ps1 writes it; an evaluator, so a '$' in the path stays literal.
        var store = "store: '" + cfg.VaultDir.Replace("'", "''") + "'";
        return Regex.Replace(yaml, @"^store:[ \t]*'[^'\r\n]*'", _ => store, RegexOptions.Multiline);
    }
}
