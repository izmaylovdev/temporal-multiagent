using System.Text;
using System.Text.RegularExpressions;

namespace AgentSandbox.Core;

/// <summary>
/// The multi-agent system: planner, coder-executor, analyst. All three are real Anthropic calls,
/// host-side only. Each call emits a Langfuse-style generation observation. The model never has
/// access to the sandbox or any secret.
/// </summary>
public static class Agents
{
    private const string PlannerSystem =
        "You are the PLANNER in a multi-agent system that solves math and data-analysis tasks by " +
        "writing and running Python. Produce a short, concrete plan: what to compute, the approach, " +
        "which standard libraries to use (numpy/pandas are available; there is NO network access), " +
        "and how to read any input files. Do not write code. Keep it under 150 words.";

    private const string CoderSystem =
        "You are the CODER-EXECUTOR in a multi-agent system. Write a SINGLE, self-contained Python 3 " +
        "script that solves the task. Constraints:\n" +
        "- Standard library plus numpy and pandas only. NO network access (it will fail).\n" +
        "- Read any input files from /work/inputs/ (read-only).\n" +
        "- You may write artifacts (e.g. CSVs) to /work/output/ if useful.\n" +
        "- PRINT the final answer(s) clearly to stdout.\n" +
        "- The script must exit 0 on success and raise on failure.\n" +
        "Respond with ONLY a single ```python fenced code block, nothing else.";

    private const string AnalystSystem =
        "You are the ANALYST in a multi-agent system. Given the user's task and the program's stdout, " +
        "explain the result in clear, plain language for a non-programmer. Be concise and accurate; do " +
        "not invent numbers that are not in the output. 120 words max.";

    public static async Task<string> PlanAsync(
        string runId, string tenant, string task, IReadOnlyList<InputFile> files)
    {
        var start = DateTime.UtcNow;
        var user = $"Task:\n{task}\n\n{FilesBlock(files)}";
        var (text, usage) = await AnthropicClient.CompleteAsync(PlannerSystem, user, 500);
        await Tracing.RecordStepAsync(runId, tenant, "planner", "generation",
            user, text, null, start, DateTime.UtcNow, Settings.AnthropicModel, usage);
        return text;
    }

    public static async Task<string> GenerateCodeAsync(GenCodeInput gi)
    {
        var start = DateTime.UtcNow;
        var parts = new List<string>
        {
            $"Task:\n{gi.Task}",
            $"Plan:\n{gi.Plan}",
            FilesBlock(gi.Files),
        };
        if (!string.IsNullOrEmpty(gi.PreviousError))
        {
            parts.Add(
                "Your previous attempt FAILED. Fix the script based on this real execution output.\n" +
                $"--- previous script ---\n{gi.PreviousCode}\n" +
                $"--- stderr/stdout ---\n{gi.PreviousError}");
        }
        var user = string.Join("\n\n", parts);
        var (text, usage) = await AnthropicClient.CompleteAsync(CoderSystem, user, 2000);
        var code = ExtractCode(text);
        await Tracing.RecordStepAsync(gi.RunId, gi.Tenant, $"coder (iter {gi.Iteration})", "generation",
            user, code,
            new Dictionary<string, object?> { ["iteration"] = gi.Iteration, ["is_retry"] = gi.PreviousError != null },
            start, DateTime.UtcNow, Settings.AnthropicModel, usage);
        return code;
    }

    public static async Task<string> AnalyzeAsync(AnalyzeInput ai)
    {
        var start = DateTime.UtcNow;
        var user = $"Task:\n{ai.Task}\n\nProgram output:\n{ai.ProgramOutput}";
        var (text, usage) = await AnthropicClient.CompleteAsync(AnalystSystem, user, 500);
        await Tracing.RecordStepAsync(ai.RunId, ai.Tenant, "analyst", "generation",
            user, text, null, start, DateTime.UtcNow, Settings.AnthropicModel, usage);
        return text;
    }

    public static string FilesBlock(IReadOnlyList<InputFile> files)
    {
        if (files.Count == 0) return "No input files were provided.";
        var sb = new StringBuilder("The following files are mounted read-only at /work/inputs/:\n");
        foreach (var f in files)
        {
            sb.Append($"- /work/inputs/{f.Name} ({f.SizeBytes} bytes)\n");
            if (!string.IsNullOrEmpty(f.Preview))
                sb.Append("  preview:\n").Append(Indent(f.Preview)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Indent(string text, int n = 4)
    {
        var pad = new string(' ', n);
        return string.Join("\n", text.Split('\n').Select(l => pad + l));
    }

    public static string ExtractCode(string text)
    {
        var m = Regex.Match(text, "```(?:python)?\\s*\\n(.*?)```", RegexOptions.Singleline);
        return (m.Success ? m.Groups[1].Value : text).Trim();
    }
}
