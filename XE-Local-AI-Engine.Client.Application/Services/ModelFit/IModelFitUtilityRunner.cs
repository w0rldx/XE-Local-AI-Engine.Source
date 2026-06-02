namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-side abstraction over the narrow HostAgent model-fit utility runner (plan Marker 2). Callers pass
///     INTENT only — an operation, validated params, and an already-resolved pinned image reference — never a command,
///     argv, or arbitrary image name. The HostAgent builds the actual <c>llmfit</c> argv from a fixed server-side
///     command profile and re-validates the image reference. Implementations:
///     <c>GrpcModelFitUtilityRunner</c> (a thin gRPC client to HostAgent's <c>ModelFitUtilityControl</c> service) and
///     <c>FakeModelFitUtilityRunner</c> (a production-resident, config-selected scriptable double).
/// </summary>
public interface IModelFitUtilityRunner
{
    /// <summary>Runs a single model-fit utility operation and returns the captured terminal result.</summary>
    Task<ModelFitUtilityRunResult> RunAsync(ModelFitUtilityRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
///     Intent-level request for a single model-fit utility run. Carries no command/argv/image-name — only the resolved
///     pinned <paramref name="ImageReference" /> and the validated operation params.
/// </summary>
public sealed record ModelFitUtilityRunRequest(
    string ImageReference,
    ModelFitOperation Operation,
    string? UseCase,
    int Limit,
    string? ModelName,
    string ProviderName,
    string? ProviderUrl,
    bool AttachRuntimeNetwork,
    int? CpuCoresOverride,
    int? RamOverrideGb,
    int? VramOverrideGb,
    int? TimeoutSeconds);

/// <summary>
///     Captured outcome of a model-fit utility run. <see cref="StandardOutput" />/<see cref="StandardError" /> carry the
///     raw text the node treats as sensitive (stored encrypted, sanitized for the API); <see cref="SanitizedError" />
///     is an operator-safe one-liner that never carries secrets.
/// </summary>
public sealed record ModelFitUtilityRunResult(
    ModelFitRunStatus Status,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool Completed,
    long DurationMs,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    string? SanitizedError);
