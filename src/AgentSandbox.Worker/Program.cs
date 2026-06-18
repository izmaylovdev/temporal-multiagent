using AgentSandbox.Core;
using Temporalio.Client;
using Temporalio.Worker;

// Temporal worker: hosts the workflow and all activities.
var client = await TemporalClient.ConnectAsync(new(Settings.TemporalAddress)
{
    Namespace = Settings.TemporalNamespace,
});

using var worker = new TemporalWorker(
    client,
    new TemporalWorkerOptions(Settings.TaskQueue)
        .AddAllActivities(new Activities())
        .AddWorkflow<AgentRunWorkflow>());

Console.WriteLine(
    $"[worker] connected to {Settings.TemporalAddress} ns={Settings.TemporalNamespace} " +
    $"queue={Settings.TaskQueue}; sandbox runtime={Settings.SandboxRuntime}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await worker.ExecuteAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[worker] shutting down");
}
