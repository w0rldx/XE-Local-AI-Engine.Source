namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Changes a project's quant-fidelity settings. Unlike every other project write this one is allowed on a FROZEN
///     project: the settings decide what gets measured next, not what the existing runs were measured against.
/// </summary>
/// <remarks>
///     A base-model or chunk-count change mints a new expected comparability digest, so figures measured under the old
///     one start reading as <c>kld-stale</c>. Nothing is deleted and no attempt is rewritten — the stale reading IS
///     the honest answer, and the operator re-measures the runs they care about.
/// </remarks>
public sealed class UpdateBenchmarkProjectFidelityEndpoint : Endpoint<UpdateBenchmarkProjectFidelityRequest, BenchmarkProjectFidelityChangeResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public UpdateBenchmarkProjectFidelityEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

    public override void Configure()
    {
        Patch(LocalApiRoutes.Benchmarks.ProjectFidelity);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateBenchmarkProjectFidelityRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var change = await _projects.UpdateFidelityAsync(req.ProjectId,
                                        req.ExpectedVersion,
                                        new BenchmarkProjectFidelitySettings
                                        {
                                            Enabled = req.FidelityEnabled,
                                            KldEnabled = req.FidelityKldEnabled,
                                            Chunks = req.FidelityChunks,
                                            KldBaseModelName = req.FidelityKldBaseModelName
                                        },
                                        req.MeasureExisting,
                                        ct);
        var runCount = await _records.CountRunsAsync(req.ProjectId, ct);
        await Send.OkAsync(new BenchmarkProjectFidelityChangeResponse
                  {
                      Project = await BenchmarkProjectDetailProjection.ReadAsync(_records, change.Project, runCount, ct),
                      EnqueuedRunIds = change.EnqueuedRunIds,
                      EnqueuedCount = change.EnqueuedRunIds.Count
                  }, ct);
    }
}
