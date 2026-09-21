using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NoteVault;

public enum CaptureSource { Watch, Reconcile, Discover }

public enum RequestKind { Capture, Metadata }

public sealed class CaptureRequest
{
    public RequestKind Kind { get; init; } = RequestKind.Capture;
    public NoteRoot? Root { get; init; }
    public CaptureSource Source { get; init; } = CaptureSource.Watch;

    /// <summary>Explicit paths to capture, or null meaning "the whole root" (reconcile).</summary>
    public HashSet<string>? Paths { get; init; }

    public int Attempt { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// The single writer. Every git command that touches the vault index runs here and
/// nowhere else — git allows exactly one index.lock holder, and contention is a
/// corruption-shaped bug rather than a slowdown.
/// </summary>
public sealed class Committer
{
    private readonly Config _cfg;
    private readonly AppState _state;
    private readonly SkipList _skip;
    private readonly Channel<CaptureRequest> _channel;
    private readonly CancellationToken _ct;

    public Committer(Config cfg, AppState state, SkipList skip, CancellationToken ct)
    {
        _cfg = cfg;
        _state = state;
        _skip = skip;
        _ct = ct;
        _channel = Channel.CreateUnbounded<CaptureRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Post(CaptureRequest request)
    {
        if (_channel.Writer.TryWrite(request))
            Interlocked.Increment(ref _state.QueueDepth);
    }

    public async Task RunAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            CaptureRequest req;
            try
            {
                req = await _channel.Reader.ReadAsync(_ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (req.Kind == RequestKind.Metadata) CommitMetadata(req.Note);
                else Process(req);
            }
            catch (Exception ex)
            {
                Log.Error("Capture failed for " + (req.Root?.Key ?? "vault"), ex);
            }
            finally
            {
                Interlocked.Decrement(ref _state.QueueDepth);
            }
        }
    }

    /// <summary>Drains whatever is queued at shutdown, so a pending debounce is not lost.</summary>
    public void FlushRemaining(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && _channel.Reader.TryRead(out var req))
        {
            try
            {
                if (req.Kind == RequestKind.Metadata) CommitMetadata(req.Note);
                else Process(req);
            }
            catch (Exception ex)
            {
                Log.Error("Flush capture failed", ex);
            }
            finally
            {
                Interlocked.Decrement(ref _state.QueueDepth);
            }
        }
    }

    private void Process(CaptureRequest req)
    {
        var root = req.Root!;
        if (root.State == RootState.Retired) return;
        if (!Directory.Exists(root.NotesPath) && !HasAnyTrackedFile(root)) return;

        Directory.CreateDirectory(root.VaultAbsPath);

        var sources = req.Paths is null
            ? EnumerateRoot(root)
            : req.Paths.Where(p => File.Exists(p) && !_skip.ShouldSkip(p)).ToList();

        var staged = new List<string>();
        var retryLater = new List<string>();

        foreach (var src in sources)
        {
            // Same coordinate system for both: relative to the worktree root. That means
            // a notes file lands at its real on-disk path (e.g. ".notes\foo.md") and a
            // tracked file lands at its own real path (e.g. "src\api\.env") — the vault
            // mirrors the worktree's actual layout, so nothing needs a special-cased
            // destination or a namespace to avoid colliding with the other.
            if (!IsUnder(root.NotesPath, src) && !IsTrackedFile(root, src)) continue;

            var rel = VaultPaths.MangleRelative(VaultPaths.RelativeTo(root.WorktreePath, src));
            var dst = Path.Combine(root.VaultAbsPath, rel);

            if (TryCopy(src, dst, out var error))
            {
                staged.Add(Path.Combine(root.VaultRelPath, rel));
                _state.Errors.Clear(ErrKind.FileRead, root.Key + ":" + rel);
            }
            else
            {
                retryLater.Add(src);
                if (req.Attempt >= 1)
                {
                    _state.Errors.Set(ErrKind.FileRead, root.Key + ":" + rel,
                        Path.GetFileName(src) + " unreadable after retries — " + error);
                }
            }
        }

        // One requeue, exactly as specified: beyond that it becomes a visible error.
        if (retryLater.Count > 0 && req.Attempt == 0)
        {
            Post(new CaptureRequest
            {
                Root = root,
                Source = req.Source,
                Paths = retryLater.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Attempt = 1,
            });
        }

        if (staged.Count == 0) return;

        if (!StagePaths(root, staged)) return;

        var status = GitRunner.Run(_cfg.VaultDir, "diff", "--cached", "--name-status");
        if (!status.Ok)
        {
            _state.Errors.Set(ErrKind.GitCommand, root.Key,
                "git diff --cached failed: " + status.Message);
            return;
        }

        var changes = ParseNameStatus(status.StdOut, root.VaultRelPath);
        if (changes.Count == 0) return;   // "saved with no changes" — nothing to record

        var message = BuildMessage(root, changes, req.Source);
        var commit = GitRunner.Run(_cfg.VaultDir, "commit", "-m", message);
        if (!commit.Ok)
        {
            _state.Errors.Set(ErrKind.GitCommand, root.Key, "git commit failed: " + commit.Message);
            return;
        }

        _state.Errors.Clear(ErrKind.GitCommand, root.Key);

        root.LastCapture = DateTime.Now;
        root.FileCount = CountFiles(root.VaultAbsPath);
        _state.LastCapture = root.LastCapture;

        // Keep the Status window live rather than waiting for the next upkeep tick to
        // re-count. The periodic refresh is still the authority and corrects any drift.
        Interlocked.Increment(ref _state.CommitCount);

        Log.Info($"{root.Key}: committed {changes.Count} file(s) [{req.Source.ToString().ToLowerInvariant()}]");
    }

    private bool StagePaths(NoteRoot root, List<string> staged)
    {
        var all = staged.Select(VaultPaths.ToGitPath).Distinct().ToList();

        // Bookkeeping rides along so retirement and alias assignments are versioned too.
        // This is also where a worktree's identity lives now — vault/ holds nothing but
        // real captured content, no per-folder marker file.
        all.Add("roots.json");
        all.Add("repos.json");   // absent when auto-scan is off, hence the filter below
        all.Add("tracked-files.json");   // absent until a tracked file is ever added

        // git add fails the whole invocation on a missing pathspec, which would drop
        // a perfectly good capture. Only stage what is actually on disk.
        all = all
            .Where(p => File.Exists(Path.Combine(_cfg.VaultDir, p.Replace('/', Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (all.Count == 0) return true;

        const int chunk = 50;
        for (var i = 0; i < all.Count; i += chunk)
        {
            var slice = all.Skip(i).Take(chunk).ToList();
            var args = new List<string> { "add", "-f", "--ignore-removal", "--" };
            args.AddRange(slice);

            var add = GitRunner.Run(_cfg.VaultDir, args.ToArray());
            if (!add.Ok)
            {
                _state.Errors.Set(ErrKind.GitCommand, root.Key, "git add failed: " + add.Message);
                return false;
            }
        }
        return true;
    }

    private void CommitMetadata(string? note)
    {
        var meta = new[] { "roots.json", "repos.json", "tracked-files.json" }
            .Where(f => File.Exists(Path.Combine(_cfg.VaultDir, f)))
            .ToArray();
        if (meta.Length == 0) return;

        var add = GitRunner.Run(_cfg.VaultDir,
            new[] { "add", "-f", "--ignore-removal", "--" }.Concat(meta).ToArray());
        if (!add.Ok) return;

        var dirty = GitRunner.Run(_cfg.VaultDir, "diff", "--cached", "--quiet");
        if (dirty.ExitCode != 1) return;

        var msg = (note ?? "roots updated") + "\n\ncaptured: " +
                  DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\nsource: discover";

        var commit = GitRunner.Run(_cfg.VaultDir, "commit", "-m", msg);
        if (commit.Ok)
        {
            Interlocked.Increment(ref _state.CommitCount);
            Log.Info("Committed roots metadata: " + (note ?? "roots updated"));
        }
    }

    private List<string> EnumerateRoot(NoteRoot root)
    {
        var results = new List<string>();
        try
        {
            if (Directory.Exists(root.NotesPath))
            {
                foreach (var f in Directory.EnumerateFiles(root.NotesPath, "*", SearchOption.AllDirectories))
                {
                    if (_skip.ShouldSkip(f)) continue;
                    results.Add(f);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Enumerate failed for " + root.NotesPath, ex);
        }

        // A "whole root" capture (reconcile, or a newly discovered root) must sweep in
        // tracked files too — they live outside the notes folder EnumerateRoot just walked.
        foreach (var pattern in _state.TrackedFilePatterns)
        {
            string abs;
            try
            {
                abs = Path.GetFullPath(Path.Combine(root.WorktreePath, pattern.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                continue;
            }

            if (IsUnder(root.WorktreePath, abs) && File.Exists(abs) && !_skip.ShouldSkip(abs))
                results.Add(abs);
        }

        return results;
    }

    private bool TryCopy(string src, string dst, out string error)
    {
        error = "";
        int[] backoff = { 50, 200, 500 };

        for (var attempt = 0; attempt <= backoff.Length; attempt++)
        {
            try
            {
                var info = new FileInfo(src);
                if (!info.Exists) { error = "vanished before copy"; return false; }

                if (info.Length > _cfg.Capture.LogLargeCaptureBytes)
                {
                    Log.Warn($"Large capture: {src} ({VaultPaths.HumanBytes(info.Length)}) — " +
                             "append-only means this cannot be removed later");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                // Share Delete too: editors rename over their target mid-save.
                using var input = new FileStream(src, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                return true;
            }
            catch (IOException ex) when (attempt < backoff.Length)
            {
                error = ex.Message;
                Thread.Sleep(backoff[attempt]);
            }
            catch (UnauthorizedAccessException ex) when (attempt < backoff.Length)
            {
                error = ex.Message;
                Thread.Sleep(backoff[attempt]);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        return false;
    }

    private static List<(char Status, string Path)> ParseNameStatus(string output, string rootRel)
    {
        var rootPrefix = VaultPaths.ToGitPath(rootRel) + "/";
        var list = new List<(char, string)>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3) continue;

            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            var status = line[0];
            var path = line[(tab + 1)..].Trim();
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            list.Add((status, path[rootPrefix.Length..]));
        }
        return list;
    }

    private static string BuildMessage(NoteRoot root, List<(char Status, string Path)> changes, CaptureSource source)
    {
        var sb = new StringBuilder();
        sb.Append(root.Key).Append(": ").Append(changes.Count)
          .Append(changes.Count == 1 ? " file" : " files").Append('\n').Append('\n');

        foreach (var (status, path) in changes.Take(25))
        {
            // Only '+' and '~' can ever appear: append-only means no deletion marker exists.
            var marker = status == 'A' ? '+' : '~';
            sb.Append(marker).Append(' ').Append(path).Append('\n');
        }

        if (changes.Count > 25)
            sb.Append("... and ").Append(changes.Count - 25).Append(" more\n");

        sb.Append('\n');
        sb.Append("captured: ").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append('\n');
        sb.Append("source: ").Append(source.ToString().ToLowerInvariant()).Append('\n');
        return sb.ToString();
    }

    private static int CountFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Count(f => !VaultPaths.LegacyRootMarkerNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsUnder(string root, string candidate)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasAnyTrackedFile(NoteRoot root)
    {
        if (string.IsNullOrEmpty(root.WorktreePath)) return false;

        foreach (var pattern in _state.TrackedFilePatterns)
        {
            try
            {
                var abs = Path.GetFullPath(Path.Combine(root.WorktreePath, pattern.Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnder(root.WorktreePath, abs) && File.Exists(abs)) return true;
            }
            catch { /* a malformed pattern just never matches */ }
        }
        return false;
    }

    /// <summary>
    /// True only if <paramref name="src"/> is under the worktree AND its relative path is
    /// currently a declared tracked-file pattern — an event on some unrelated file under
    /// the worktree root must never be captured just because it happens to pass by here.
    /// </summary>
    private bool IsTrackedFile(NoteRoot root, string src)
    {
        if (string.IsNullOrEmpty(root.WorktreePath) || !IsUnder(root.WorktreePath, src)) return false;

        var rel = VaultPaths.RelativeTo(root.WorktreePath, src).Replace('\\', '/');
        return _state.TrackedFilePatterns.Any(p => string.Equals(p, rel, StringComparison.OrdinalIgnoreCase));
    }
}
