namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Orchestrates a single model-fit refresh: it resolves the approved image, validates the intent
///     params, computes node hardware overrides, opens a snapshot run, invokes the narrow HostAgent utility runner,
///     tolerantly parses the recommendation JSON and replaces the cached normalized recommendation snapshot.
///     <para>
///         This service is invoked solely by the scheduler handler — there is no bypass execution path. It NEVER touches
///         scheduler run rows or publishes SignalR (the dispatcher owns those). It re-throws
///         <see cref="OperationCanceledException" /> so the dispatcher can mark the run cancelled, and returns a failed
///         result (or throws for an unsupported operation) for every other non-success outcome.
///     </para>
/// </summary>
public interface IModelFitRefreshService
{
    /// <summary>
    ///     Runs one refresh for <paramref name="request" />. <paramref name="reportProgress" /> is the scheduler's
    ///     (possibly <c>null</c>) progress callback — implementations must null-check before invoking it. Returns the
    ///     terminal snapshot outcome. Throws <see cref="OperationCanceledException" /> when the node token is cancelled
    ///     mid-run (after recording a Cancelled snapshot).
    /// </summary>
    Task<ModelFitRefreshResult> RefreshAsync(ModelFitRefreshRequest request,
        Func<string, int?, CancellationToken, Task>? reportProgress,
        CancellationToken cancellationToken);
}

/// <summary>
///     Intent-level request for one model-fit refresh. Carries no command/argv/image-name — the approved image id is
///     resolved to a pinned reference server-side.
/// </summary>
public sealed record ModelFitRefreshRequest(
    string ApprovedImageId,
    ModelFitOperation Operation,
    string? UseCase,
    int Limit,
    string ProviderName,
    string? ModelName);

/// <summary>
///     Outcome of a model-fit refresh. <see cref="SanitizedError" /> is an operator-safe one-liner that never carries
///     secrets or raw utility output; <see cref="SnapshotId" /> is <c>null</c> only when the refresh failed pre-run
///     validation (resolver/validator rejection) before any snapshot row was created.
/// </summary>
public sealed record ModelFitRefreshResult(
    Guid? SnapshotId,
    ModelFitRunStatus Status,
    int RecommendationCount,
    string? SanitizedError);
