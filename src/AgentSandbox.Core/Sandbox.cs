using System.Diagnostics;
using System.Text;

namespace AgentSandbox.Core;

/// <summary>
/// Untrusted-code sandbox executor. Runs a single Python script inside an ephemeral, no-network
/// container using gVisor (runsc) when available, otherwise a hardened runc container with the same
/// restrictions. Runs HOST-SIDE inside a Temporal activity; shells out to the Docker CLI, so the
/// worker container mounts the Docker socket. Bind-mount sources must be HOST paths, so per-run dirs
/// are translated from RunsDir (in-container) to RunsDirHost (on the host).
/// </summary>
public static class Sandbox
{
    private static string? _runtimeCache;
    private static readonly object RuntimeLock = new();

    public static string ResolveRuntime()
    {
        lock (RuntimeLock)
        {
            if (_runtimeCache != null) return _runtimeCache;
            var choice = (Settings.SandboxRuntime ?? "auto").ToLowerInvariant();
            _runtimeCache = choice switch
            {
                "runsc" => "runsc",
                "runc" => "runc",
                _ => DockerHasRunsc() ? "runsc" : "runc",
            };
            return _runtimeCache;
        }
    }

    private static bool DockerHasRunsc()
    {
        try
        {
            var (code, outp, _, _) = RunProcess("docker", new[] { "info", "--format", "{{json .Runtimes}}" }, 15000, null);
            return code == 0 && outp.Contains("runsc");
        }
        catch { return false; }
    }

    public static string HostPath(string containerPath)
    {
        var cp = Path.GetFullPath(containerPath);
        var baseDir = Path.GetFullPath(Settings.RunsDir);
        if (string.Equals(cp, baseDir)) return Path.GetFullPath(Settings.RunsDirHost);

        var prefix = baseDir + Path.DirectorySeparatorChar;
        if (cp.StartsWith(prefix))
        {
            var rel = Path.GetRelativePath(baseDir, cp);
            return Path.Combine(Path.GetFullPath(Settings.RunsDirHost), rel);
        }
        return cp; // not under RunsDir; pass through
    }

    public static ExecResult RunScript(string runId, int attempt, string code, string? inputsDir)
    {
        var runtime = ResolveRuntime();

        var attemptDir = Path.Combine(Settings.RunsDir, runId, $"attempt_{attempt}");
        var outputDir = Path.Combine(attemptDir, "output");
        Directory.CreateDirectory(outputDir);
        var scriptPath = Path.Combine(attemptDir, "script.py");
        File.WriteAllText(scriptPath, code);

        var shortId = runId.Length > 8 ? runId[..8] : runId;
        var rand = Guid.NewGuid().ToString("N")[..6];
        var containerName = $"sbx-{shortId}-{attempt}-{rand}";

        var args = new List<string>
        {
            "run", "--rm", "--name", containerName,
            "--runtime", runtime,
            "--network", "none",
            "--read-only",
            "--user", "65534:65534",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", Settings.SandboxPids.ToString(),
            "--memory", Settings.SandboxMemory,
            "--memory-swap", Settings.SandboxMemory, // disable swap
            "--cpus", Settings.SandboxCpus,
            "--workdir", "/work",
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=32m",
            "-v", $"{HostPath(scriptPath)}:/work/script.py:ro",
            "-v", $"{HostPath(outputDir)}:/work/output:rw",
        };
        if (!string.IsNullOrEmpty(inputsDir) && Directory.Exists(inputsDir))
        {
            args.Add("-v");
            args.Add($"{HostPath(inputsDir)}:/work/inputs:ro");
        }
        args.Add(Settings.SandboxImage);
        args.Add("python");
        args.Add("/work/script.py");

        var sw = Stopwatch.StartNew();
        var (exitCode, stdout, stderr, timedOut) =
            RunProcess("docker", args.ToArray(), Settings.SandboxTimeoutSeconds * 1000, containerName);
        sw.Stop();

        var artifacts = new List<string>();
        try
        {
            foreach (var f in Directory.GetFiles(outputDir))
                artifacts.Add(Path.GetFileName(f));
            artifacts.Sort(StringComparer.Ordinal);
        }
        catch { /* ignore */ }

        var limit = Settings.SandboxOutputMaxBytes;
        return new ExecResult(
            exitCode,
            Truncate(stdout, limit),
            Truncate(stderr, limit),
            (int)sw.ElapsedMilliseconds,
            timedOut,
            runtime,
            artifacts);
    }

    public static string Truncate(string text, int limitBytes)
    {
        text ??= "";
        var data = Encoding.UTF8.GetBytes(text);
        if (data.Length <= limitBytes) return text;
        return Encoding.UTF8.GetString(data, 0, limitBytes) + $"\n...[truncated, {data.Length} bytes]";
    }

    private static (int Code, string Stdout, string Stderr, bool TimedOut) RunProcess(
        string fileName, string[] args, int timeoutMs, string? killContainer)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit(timeoutMs))
        {
            if (killContainer != null) ForceKill(killContainer);
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            p.WaitForExit(5000);
            se.AppendLine($"[sandbox] wall-clock timeout after {timeoutMs / 1000}s");
            return (124, so.ToString(), se.ToString(), true);
        }

        p.WaitForExit(); // flush async output buffers
        return (p.ExitCode, so.ToString(), se.ToString(), false);
    }

    private static void ForceKill(string containerName)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "docker", UseShellExecute = false };
            psi.ArgumentList.Add("kill");
            psi.ArgumentList.Add(containerName);
            using var p = Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch { /* ignore */ }
    }
}
