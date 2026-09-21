namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListBenchmarkProjectsEndpoint : EndpointWithoutRequest<ListBenchmarkProjectsResponse>
{
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkProjectsEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projects = await _records.ListProjectsAsync(ct);

        // One grouped count for the whole listing. Counting per project read the same table once per row, so a picker
        // with N projects cost N+1 round trips to answer a question one GROUP BY answers.
        var runCounts = await _records.CountRunsByProjectAsync(ct);

        await Send.OkAsync(new ListBenchmarkProjectsResponse
        {
            Items = projects.Select(project => project.ToSummary(runCounts.GetValueOrDefault(project.Id))).ToArray()
        }, ct);
    }
}
