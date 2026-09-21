namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Re-measures one run's quant fidelity. A new immutable attempt, never an overwrite of the last one.</summary>
public sealed class StartBenchmarkRunFidelityEndpoint : Endpoint<StartRunFidelityRequest>
{
    private readonly IBenchmarkQueueSignal _signal;
    private readonly BenchmarkRecordService _records;

    public StartBenchmarkRunFidelityEndpoint(BenchmarkRecordService records, IBenchmarkQueueSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(records);
        _signal = signal;
        _records = records;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunFidelity);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartRunFidelityRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var run = await _records.GetRunAsync(req.RunId, ct);
        if (run is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found.")));
            return;
        }

        var project = await _records.GetProjectAsync(run.ProjectId, ct);
        _ = await _records.EnqueueFidelityAsync(req.RunId, project?.FidelityKldEnabled == true ? "kld" : "ppl", ct);
        _signal.Wake();
        await Send.ResultAsync(Results.Accepted());
    }
}
