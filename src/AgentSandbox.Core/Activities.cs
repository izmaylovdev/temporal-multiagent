using Temporalio.Activities;

namespace AgentSandbox.Core;

/// <summary>
/// Temporal activities — the only place with side effects (Anthropic calls, sandbox launch). They
/// run on the worker, host-side, with network access.
/// </summary>
public class Activities
{
    [Activity]
    public Task<string> Plan(RunInput ri) =>
        Agents.PlanAsync(ri.RunId, ri.Tenant, ri.Task, ri.Files);

    [Activity]
    public Task<string> GenerateCode(GenCodeInput gi) =>
        Agents.GenerateCodeAsync(gi);

    [Activity]
    public async Task<ExecResult> ExecuteInSandbox(ExecInput ei)
    {
        var inputsDir = Path.Combine(Settings.RunsDir, ei.RunId, "inputs");
        var hasInputs = Directory.Exists(inputsDir);
        var start = DateTime.UtcNow;

        // Run the blocking sandbox on a thread; heartbeat while it runs.
        var runTask = Task.Run(() => Sandbox.RunScript(ei.RunId, ei.Attempt, ei.Code, hasInputs ? inputsDir : null));
        while (!runTask.IsCompleted)
        {
            ActivityExecutionContext.Current.Heartbeat($"sandbox attempt {ei.Attempt} running");
            await Task.WhenAny(runTask, Task.Delay(5000));
        }
        var result = await runTask;

        await Tracing.RecordStepAsync(
            ei.RunId, ei.Tenant, $"sandbox-exec (attempt {ei.Attempt})", "span",
            input: new { code_bytes = ei.Code.Length },
            output: new
            {
                exit_code = result.ExitCode,
                timed_out = result.TimedOut,
                stdout = result.Stdout,
                stderr = result.Stderr,
                artifacts = result.Artifacts,
            },
            metadata: new Dictionary<string, object?>
            {
                ["runtime"] = result.Runtime,
                ["attempt"] = ei.Attempt,
                ["duration_ms"] = result.DurationMs,
                ["network"] = "none",
            },
            startTime: start,
            endTime: DateTime.UtcNow,
            level: result.ExitCode == 0 ? "DEFAULT" : "ERROR");

        return result;
    }

    [Activity]
    public Task<string> Analyze(AnalyzeInput ai) =>
        Agents.AnalyzeAsync(ai);
}
