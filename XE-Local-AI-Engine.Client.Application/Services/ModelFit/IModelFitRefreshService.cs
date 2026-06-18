namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Orchestrates a single model-fit refresh: it profiles the node hardware (Lane C1), discovers candidate GGUF files
///     from Hugging Face (Lane B), estimates each file's memory fit, drops the non-fitting / insufficient-metadata ones,
///     ranks the survivors, tolerantly serializes them to recommendation rows and replaces the cached recommendation
///     snapshot — all node-local except the HF discovery egress. No Docker, no approved image, no provider name.
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
///     Intent-level request for one model-fit refresh. Carries no command/argv/image-name and no provider — the local
///     advisor runs box-aware GGUF recommendation entirely in-process (the only egress is the Lane B HF discovery call).
///     <see cref="QuantOverride" /> replaces the default <c>Q4_K_M</c> quant when supplied; <see cref="CtxTarget" />
///     overrides the context window the KV-cache fit is sized against.
/// </summary>
public sealed record ModelFitRefreshRequest(
    ModelFitOperation Operation,
    string? UseCase,
    int Limit,
    string? QuantOverride = null,
    int? CtxTarget = null);

/// <summary>
///     Outcome of a model-fit refresh. <see cref="SanitizedError" /> is an operator-safe one-liner that never carries
///     secrets or raw utility output; <see cref="SnapshotId" /> is <c>null</c> only when the refresh failed pre-run
///     validation (validator rejection) before any snapshot row was created.
/// </summary>
public sealed record ModelFitRefreshResult(
    Guid? SnapshotId,
    ModelFitRunStatus Status,
    int RecommendationCount,
    string? SanitizedError);
