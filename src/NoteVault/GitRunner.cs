using System.Diagnostics;
using System.Text;

namespace NoteVault;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string Message =>
        string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

public static class GitRunner
{
    public static bool IsGitAvailable()
    {
        try
        {
            return Run(Environment.CurrentDirectory, TimeSpan.FromSeconds(10), "--version").Ok;
        }
        catch
        {
            return false;
        }
    }

    public static GitResult Run(string workingDir, params string[] args) =>
        Run(workingDir, TimeSpan.FromSeconds(120), args);

    public static GitResult Run(string workingDir, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        // Keep git from ever blocking on an interactive prompt.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new GitResult(-1, stdout.ToString(), "git timed out after " + timeout);
        }

        p.WaitForExit();
        return new GitResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
