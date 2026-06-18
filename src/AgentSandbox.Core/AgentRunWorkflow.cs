using Temporalio.Common;
using Temporalio.Workflows;

namespace AgentSandbox.Core;

/// <summary>
/// Durable orchestration of planner -> coder-executor (error-driven self-correction) -> analyst.
/// Deterministic: only schedules activities, manipulates plain data, and maintains a queryable
/// status object. A worker crash mid-run resumes from the last completed activity.
/// </summary>
[Workflow]
public class AgentRunWorkflow
{
    private readonly RunStatus _status = new();

    private static readonly RetryPolicy LlmRetry = new()
    {
        InitialInterval = TimeSpan.FromSeconds(2),
        BackoffCoefficient = 2.0f,
        MaximumInterval = TimeSpan.FromSeconds(30),
        MaximumAttempts = 4,
        NonRetryableErrorTypes = new[] { "AnthropicNonRetryableException" },
    };

    private static readonly RetryPolicy ExecRetry = new()
    {
        InitialInterval = TimeSpan.FromSeconds(1),
        BackoffCoefficient = 2.0f,
        MaximumAttempts = 2,
    };

    [WorkflowQuery]
    public RunStatus GetStatus() => _status;

    [WorkflowRun]
    public async Task<RunResult> RunAsync(RunInput ri)
    {
        _status.RunId = ri.RunId;
        _status.Tenant = ri.Tenant;
        _status.Task = ri.Task;

        // 1) plan
        Begin("planner");
        var plan = await Workflow.ExecuteActivityAsync(
            (Activities a) => a.Plan(ri),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(120), RetryPolicy = LlmRetry });
        Finish("ok");

        // 2) coder-executor loop with error-driven self-correction
        string? previousCode = null, previousError = null;
        var success = false;
        var finalCode = "";
        var programOutput = "";

        for (var i = 1; i <= Settings.MaxIterations; i++)
        {
            _status.Iterations = i;

            Begin("coder", i);
            var code = await Workflow.ExecuteActivityAsync(
                (Activities a) => a.GenerateCode(new GenCodeInput(
                    ri.RunId, ri.Tenant, ri.Task, plan, ri.Files, i, previousCode, previousError)),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(120), RetryPolicy = LlmRetry });
            Finish("ok");

            Begin("sandbox-exec", i);
            var result = await Workflow.ExecuteActivityAsync(
                (Activities a) => a.ExecuteInSandbox(new ExecInput(ri.RunId, ri.Tenant, i, code)),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(Settings.SandboxTimeoutSeconds + 30),
                    HeartbeatTimeout = TimeSpan.FromSeconds(15),
                    RetryPolicy = ExecRetry,
                });

            finalCode = code;
            if (result.ExitCode == 0)
            {
                programOutput = result.Stdout;
                Finish("ok", $"runtime={result.Runtime}");
                success = true;
                break;
            }

            var err = (string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr).Trim();
            Finish("error", err.Length > 500 ? err[..500] : err);
            previousCode = code;
            previousError = err;
        }

        if (!success)
        {
            _status.State = "failed";
            var failed = new RunResult(ri.RunId, "failed", ri.Task, _status.Iterations, finalCode,
                Error: "Coder-executor did not produce a successful run within the iteration budget.");
            _status.Result = failed;
            return failed;
        }

        // 3) analyst
        Begin("analyst");
        var explanation = await Workflow.ExecuteActivityAsync(
            (Activities a) => a.Analyze(new AnalyzeInput(ri.RunId, ri.Tenant, ri.Task, finalCode, programOutput)),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(120), RetryPolicy = LlmRetry });
        Finish("ok");

        _status.State = "succeeded";
        _status.CurrentStep = null;
        var res = new RunResult(ri.RunId, "succeeded", ri.Task, _status.Iterations,
            finalCode, programOutput, explanation);
        _status.Result = res;
        return res;
    }

    private void Begin(string name, int iteration = 0)
    {
        _status.CurrentStep = name;
        _status.State = "running";
        _status.Steps.Add(new StepStatus
        {
            Name = name,
            Iteration = iteration,
            Status = "running",
            At = Workflow.UtcNow.ToString("o"),
        });
    }

    private void Finish(string status, string detail = "")
    {
        if (_status.Steps.Count == 0) return;
        var s = _status.Steps[^1];
        s.Status = status;
        s.Detail = detail;
        s.EndedAt = Workflow.UtcNow.ToString("o");
    }
}
