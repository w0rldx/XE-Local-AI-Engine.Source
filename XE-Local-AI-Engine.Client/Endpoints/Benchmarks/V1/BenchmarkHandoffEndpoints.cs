namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;

/// <summary>
///     Training comparison → benchmark project. The existing deep link
///     (<c>/benchmarks?baseModelName=…&amp;tunedModelName=…</c>) only SELECTS runs that already exist in a project the
///     operator already built; this creates the project and starts the paired runs in one action.
///     <para>
///         No local catch: every refusal the service raises is a benchmark exception family member and is mapped by the
///         global <c>BenchmarkExceptionHandler</c>, including the "model is not installed" case the service translates
///         out of the freeze's bare <see cref="KeyNotFoundException" />.
///     </para>
/// </summary>
public sealed class CreateBenchmarkFromComparisonEndpoint(IComparisonBenchmarkHandoffService handoff)
    : Endpoint<CreateBenchmarkFromComparisonRequest, CreateBenchmarkFromComparisonResponse>
{
    private readonly IComparisonBenchmarkHandoffService _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ComparisonBenchmark);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<CreateBenchmarkFromComparisonResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(CreateBenchmarkFromComparisonRequest req, CancellationToken ct)
    {
        var created = await _handoff.CreateAsync(new CreateBenchmarkFromComparisonCommand(req.ComparisonId,
                                         req.CoreTask,
                                         req.ContextTokens,
                                         req.AgentDefinitionId,
                                         req.Name,
                                         req.KvCacheType,
                                         req.RepeatCount,
                                         req.Warmup),
                                     ct)
                                 .ConfigureAwait(false);

        // 202, like every other run start: the runs are queued, not finished.
        await Send.ResultAsync(Results.Accepted(value: new CreateBenchmarkFromComparisonResponse
        {
            ProjectId = created.ProjectId,
            BaseModelName = created.BaseModelName,
            TunedModelName = created.TunedModelName,
            RunIds = created.RunIds
        })).ConfigureAwait(false);
    }
}
