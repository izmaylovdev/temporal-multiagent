using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSandbox.Core;
using Temporalio.Client;
using Temporalio.Exceptions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Serve the single-page web UI from wwwroot (index.html at "/").
app.UseDefaultFiles();
app.UseStaticFiles();

// Connect the Temporal client at startup (compose waits for Temporal to be healthy).
var client = await TemporalClient.ConnectAsync(new(Settings.TemporalAddress)
{
    Namespace = Settings.TemporalNamespace,
});
Console.WriteLine($"[api] connected to Temporal at {Settings.TemporalAddress}");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/healthz", () => Results.Json(new { ok = true, sandbox_runtime = Settings.SandboxRuntime }));

// Start a run. multipart/form-data: task (required), tenant (optional), files (0..N).
app.MapPost("/runs", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "expected multipart/form-data" });

    var form = await request.ReadFormAsync();
    var task = form["task"].ToString();
    if (string.IsNullOrWhiteSpace(task))
        return Results.BadRequest(new { error = "task is required" });

    var tenant = string.IsNullOrWhiteSpace(form["tenant"]) ? "default" : form["tenant"].ToString();
    var runId = $"run-{Guid.NewGuid():N}"[..20];

    List<InputFile> saved;
    try
    {
        saved = await SaveUploads(runId, form.Files);
    }
    catch (UploadException ue)
    {
        return Results.BadRequest(new { error = ue.Message });
    }

    var ri = new RunInput(runId, tenant, task, saved);
    await client.StartWorkflowAsync(
        (AgentRunWorkflow wf) => wf.RunAsync(ri),
        new WorkflowOptions(id: runId, taskQueue: Settings.TaskQueue)
        {
            ExecutionTimeout = Settings.WorkflowTimeoutSeconds <= 0
                ? null
                : TimeSpan.FromSeconds(Settings.WorkflowTimeoutSeconds),
        });

    return Results.Json(new { run_id = runId, tenant, files = saved.Select(f => f.Name) }, statusCode: 202);
});

// Status snapshot, read from the workflow query.
app.MapGet("/runs/{id}", async (string id) =>
{
    try
    {
        var status = await client.GetWorkflowHandle(id).QueryAsync((AgentRunWorkflow wf) => wf.GetStatus());
        return Results.Json(status, json);
    }
    catch (RpcException)
    {
        return Results.NotFound(new { error = $"run not found: {id}" });
    }
});

// Server-Sent Events stream of status until terminal.
app.MapGet("/runs/{id}/stream", async (string id, HttpContext ctx) =>
{
    ctx.Response.Headers.Append("Content-Type", "text/event-stream");
    ctx.Response.Headers.Append("Cache-Control", "no-cache");
    ctx.Response.Headers.Append("Connection", "keep-alive");

    var handle = client.GetWorkflowHandle(id);
    string? last = null;

    while (!ctx.RequestAborted.IsCancellationRequested)
    {
        RunStatus status;
        try
        {
            status = await handle.QueryAsync((AgentRunWorkflow wf) => wf.GetStatus());
        }
        catch (RpcException)
        {
            await WriteEvent(ctx, "error", "{\"error\":\"run not found\"}");
            return;
        }

        var payload = JsonSerializer.Serialize(status, json);
        if (payload != last)
        {
            await WriteEvent(ctx, "status", payload);
            last = payload;
        }

        if (status.State is "succeeded" or "failed")
        {
            await WriteEvent(ctx, "done", payload);
            return;
        }

        try { await Task.Delay(1000, ctx.RequestAborted); }
        catch (TaskCanceledException) { return; }
    }
});

await app.RunAsync();


static async Task WriteEvent(HttpContext ctx, string ev, string data)
{
    await ctx.Response.WriteAsync($"event: {ev}\ndata: {data}\n\n");
    await ctx.Response.Body.FlushAsync();
}

static async Task<List<InputFile>> SaveUploads(string runId, IFormFileCollection files)
{
    var inputsDir = Path.Combine(Settings.RunsDir, runId, "inputs");
    Directory.CreateDirectory(inputsDir);

    var saved = new List<InputFile>();
    long total = 0;

    foreach (var up in files)
    {
        if (up.Length == 0 || string.IsNullOrEmpty(up.FileName)) continue;

        var name = Sanitize(up.FileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (Array.IndexOf(Settings.AllowedExtensions, ext) < 0)
            throw new UploadException($"file type not allowed: {ext}");
        if (up.Length > Settings.MaxFileBytes)
            throw new UploadException($"file too large: {name}");

        total += up.Length;
        if (total > Settings.MaxTotalUploadBytes)
            throw new UploadException("total upload size exceeded");

        var dest = Path.Combine(inputsDir, name);
        await using (var fs = File.Create(dest))
            await up.CopyToAsync(fs);

        saved.Add(new InputFile(name, up.Length, Preview(dest, ext)));
    }
    return saved;
}

static string Sanitize(string name)
{
    var bn = Path.GetFileName(name).Trim();
    bn = Regex.Replace(bn, "[^A-Za-z0-9._-]", "_");
    return string.IsNullOrEmpty(bn) ? "file" : bn;
}

static string Preview(string path, string ext)
{
    if (ext is not (".csv" or ".tsv" or ".txt" or ".json")) return "";
    try
    {
        var s = string.Join("\n", File.ReadLines(path).Take(10));
        return s.Length > 1000 ? s[..1000] : s;
    }
    catch { return ""; }
}

public class UploadException : Exception
{
    public UploadException(string message) : base(message) { }
}
