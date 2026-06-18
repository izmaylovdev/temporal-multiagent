using System.Text;
using System.Text.Json;

namespace AgentSandbox.Core;

/// <summary>
/// Langfuse-style tracing. One trace per run (keyed by run id), one observation per step. Every
/// observation is ALWAYS written to a per-run trace.jsonl and to stderr, and additionally shipped
/// to Langfuse best-effort. Failures here never break a run (fail-open).
/// </summary>
public static class Tracing
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly object FileLock = new();

    public static async Task RecordStepAsync(
        string runId,
        string tenant,
        string name,
        string kind,                       // "span" | "generation"
        object? input,
        object? output,
        IDictionary<string, object?>? metadata,
        DateTime startTime,
        DateTime endTime,
        string? model = null,
        AnthropicClient.Usage? usage = null,
        string level = "DEFAULT")          // DEFAULT | ERROR
    {
        var durationMs = (int)(endTime - startTime).TotalMilliseconds;
        var usageObj = usage is null ? null : new { input = usage.Input, output = usage.Output, total = usage.Total };

        var ev = new Dictionary<string, object?>
        {
            ["trace_id"] = runId,
            ["tenant"] = tenant,
            ["name"] = name,
            ["kind"] = kind,
            ["level"] = level,
            ["model"] = model,
            ["usage"] = usageObj,
            ["metadata"] = metadata,
            ["input"] = Short(input),
            ["output"] = Short(output),
            ["start_time"] = startTime.ToString("o"),
            ["end_time"] = endTime.ToString("o"),
            ["duration_ms"] = durationMs,
        };

        // 1) Always: JSONL + stderr
        try
        {
            var dir = Path.Combine(Settings.RunsDir, runId);
            Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(ev);
            lock (FileLock)
            {
                File.AppendAllText(Path.Combine(dir, "trace.jsonl"), line + "\n");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tracing] file write failed: {e.Message}");
        }
        Console.Error.WriteLine($"[trace] {runId} {name} ({durationMs}ms) level={level}");

        // 2) Best-effort: Langfuse ingestion API
        if (!Settings.LangfuseEnabled) return;
        try
        {
            await SendLangfuseAsync(runId, tenant, name, kind, Short(input), Short(output),
                metadata, startTime, endTime, model, usageObj, level);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tracing] Langfuse send failed: {e.Message}");
        }
    }

    private static object? Short(object? value, int limit = 4000)
    {
        if (value is string s && s.Length > limit)
            return s[..limit] + $"...[truncated {s.Length} chars]";
        return value;
    }

    private static async Task SendLangfuseAsync(
        string runId, string tenant, string name, string kind,
        object? input, object? output, IDictionary<string, object?>? metadata,
        DateTime startTime, DateTime endTime, string? model, object? usage, string level)
    {
        var obsType = kind == "generation" ? "GENERATION" : "SPAN";
        var now = DateTime.UtcNow.ToString("o");

        var payload = new
        {
            batch = new object[]
            {
                new
                {
                    id = Guid.NewGuid().ToString(),
                    type = "trace-create",
                    timestamp = now,
                    body = new { id = runId, name = "agent-run", userId = tenant, tags = new[] { $"tenant:{tenant}" } },
                },
                new
                {
                    id = Guid.NewGuid().ToString(),
                    type = "observation-create",
                    timestamp = now,
                    body = new Dictionary<string, object?>
                    {
                        ["id"] = Guid.NewGuid().ToString(),
                        ["traceId"] = runId,
                        ["type"] = obsType,
                        ["name"] = name,
                        ["startTime"] = startTime.ToString("o"),
                        ["endTime"] = endTime.ToString("o"),
                        ["input"] = input,
                        ["output"] = output,
                        ["model"] = model,
                        ["usage"] = usage,
                        ["level"] = level,
                        ["metadata"] = metadata,
                    },
                },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Settings.LangfuseHost}/api/public/ingestion");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Settings.LangfusePublicKey}:{Settings.LangfuseSecretKey}"));
        req.Headers.Add("Authorization", $"Basic {auth}");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
    }
}
