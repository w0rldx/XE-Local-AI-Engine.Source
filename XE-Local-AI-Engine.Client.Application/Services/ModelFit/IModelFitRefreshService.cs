namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Orchestrates a single model-fit refresh: profiles the node hardware, discovers candidate GGUF files from
///     Hugging Face, estimates each file's memory fit, ranks the survivors and replaces the cached snapshot.
/// </summary>
/// <remarks>
///     Non-fitting and metadata-poor files are dropped and the rows serialized tolerantly. Everything is node-local
///     except the Hugging Face discovery egress: no Docker, no approved image, no provider name. The scheduler handler
///     is the only caller — there is no bypass execution path — and this service never touches scheduler run rows or
///     publishes SignalR (the dispatcher owns those). It re-throws <see cref="OperationCanceledException" /> so the
///     dispatcher can mark the run cancelled, and returns a failed result (or throws) for any other non-success outcome.
/// </remarks>
public interface IModelFitRefreshService
{
    /// <summary>Runs one refresh for <paramref name="request" /> and returns the terminal snapshot outcome.</summary>
    /// <param name="reportProgress">
    ///     The scheduler's progress callback, possibly <c>null</c> — implementations must null-check before invoking
    ///     it.
    /// </param>
    /// <exception cref="OperationCanceledException">
    ///     The node token was cancelled mid-run, after a Cancelled snapshot was recorded.
    /// </exception>
    Task<ModelFitRefreshResult> RefreshAsync(ModelFitRefreshRequest request,
        Func<string, int?, CancellationToken, Task>? reportProgress,
        CancellationToken cancellationToken);
}

/// <summary>
///     Intent-level request for one model-fit refresh. It carries no command, argv, image name or provider: the local
///     advisor runs box-aware GGUF recommendation entirely in-process, its only egress the Hugging Face discovery
///     call.
/// </summary>
/// <remarks>
///     <see cref="QuantOverride" /> replaces the default <c>Q4_K_M</c> quant when supplied; <see cref="CtxTarget" />
///     overrides the context window the KV-cache fit is sized against.
/// </remarks>
public sealed class ModelFitRefreshRequest
{
    public required ModelFitOperation Operation { get; init; }

    public required string? UseCase { get; init; }

    public required int Limit { get; init; }

    public string? QuantOverride { get; init; }

    public int? CtxTarget { get; init; }
}

/// <summary>
///     Outcome of a model-fit refresh. <see cref="SanitizedError" /> is an operator-safe one-liner that never carries
///     secrets or raw utility output; <see cref="SnapshotId" /> is <c>null</c> only when the refresh failed pre-run
///     validation (validator rejection) before any snapshot row was created.
/// </summary>
public sealed class ModelFitRefreshResult
{
    public required Guid? SnapshotId { get; init; }

    public required ModelFitRunStatus Status { get; init; }

    public required int RecommendationCount { get; init; }

    public required string? SanitizedError { get; init; }
}
