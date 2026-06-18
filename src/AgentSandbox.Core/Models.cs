namespace AgentSandbox.Core;

// Records cross the API / workflow / activity boundaries. The Temporal default (JSON) data
// converter serializes them; positional records bind back by constructor parameter name.

public record InputFile(string Name, long SizeBytes, string Preview = "");

public record RunInput(string RunId, string Tenant, string Task, IReadOnlyList<InputFile> Files);

public record GenCodeInput(
    string RunId,
    string Tenant,
    string Task,
    string Plan,
    IReadOnlyList<InputFile> Files,
    int Iteration,
    string? PreviousCode,
    string? PreviousError);

public record ExecInput(string RunId, string Tenant, int Attempt, string Code);

public record AnalyzeInput(string RunId, string Tenant, string Task, string Code, string ProgramOutput);

public record ExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    int DurationMs,
    bool TimedOut,
    string Runtime,
    IReadOnlyList<string> Artifacts);

public record RunResult(
    string RunId,
    string Status,
    string Task,
    int Iterations,
    string FinalCode = "",
    string ProgramOutput = "",
    string Explanation = "",
    string Error = "");

// Mutable status object owned by the workflow and returned by the status query.
public class StepStatus
{
    public string Name { get; set; } = "";
    public int Iteration { get; set; }
    public string Status { get; set; } = "running";
    public string At { get; set; } = "";
    public string? EndedAt { get; set; }
    public string Detail { get; set; } = "";
}

public class RunStatus
{
    public string RunId { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Task { get; set; } = "";
    public string State { get; set; } = "pending";
    public string? CurrentStep { get; set; }
    public int Iterations { get; set; }
    public List<StepStatus> Steps { get; set; } = new();
    public RunResult? Result { get; set; }
}
