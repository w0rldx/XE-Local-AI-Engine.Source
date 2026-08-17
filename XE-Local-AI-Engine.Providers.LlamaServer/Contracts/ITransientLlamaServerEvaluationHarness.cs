namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Content-addressed identity of the base GGUF and optional adapter actually held stable through an evaluation load.</summary>
public sealed record TransientLlamaServerModelProvenance(
    string ModelId,
    long ModelSizeBytes,
    string ModelSha256,
    string? AdapterId,
    long? AdapterSizeBytes,
    string? AdapterSha256);

/// <summary>Proof that the evaluation harness requested and completed ownership teardown for its child process.</summary>
public sealed record TransientLlamaServerTeardownEvidence(
    int ProcessId,
    bool TreeKillRequested,
    bool ProcessExitObserved,
    bool ExitObservationTimedOut,
    bool HandleDisposed);

/// <summary>Validated model and launch identity that must be bound before evaluation writes its first sample.</summary>
public sealed record TransientLlamaServerEvaluationProvenance(
    TransientLlamaServerModelProvenance Model,
    LlamaServerLaunchReceipt Launch);

/// <summary>A ready evaluation endpoint plus the exact model and launch evidence captured before scoring begins.</summary>
public sealed record TransientLlamaServerEvaluationSession(
    Uri BaseAddress,
    string ModelId,
    TransientLlamaServerModelProvenance Model,
    LlamaServerLaunchReceipt Launch)
{
    public TransientLlamaServerEvaluationProvenance Provenance => new(Model, Launch);
}

/// <summary>The caller result paired with immutable launch/model provenance and post-body teardown evidence.</summary>
public sealed record TransientLlamaServerEvaluationResult<T>(
    T Value,
    TransientLlamaServerModelProvenance Model,
    LlamaServerLaunchReceipt Launch,
    TransientLlamaServerTeardownEvidence Teardown)
{
    public TransientLlamaServerEvaluationProvenance Provenance => new(Model, Launch);
}

/// <summary>
///     Runs one path-addressed training evaluation under the supervisor's exclusive runtime-mutation lease. The harness
///     refuses to start beside any warm or in-flight supervised model, owns GPU-load admission, pins a frozen benchmark
///     launch policy, content-addresses the model inputs, and returns teardown evidence after the body completes.
/// </summary>
public interface ITransientLlamaServerEvaluationHarness
{
    Task<TransientLlamaServerEvaluationResult<T>> RunAsync<T>(TransientLlamaServerEvaluationRequest request,
        Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
        Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct);
}

/// <param name="ModelFilePath">Absolute path to the base or merged GGUF.</param>
/// <param name="AdapterFilePath">Optional staged LoRA GGUF applied to the base.</param>
/// <param name="ContextTokens">Frozen context window used for both baseline and tuned evaluation.</param>
/// <param name="ReadinessTimeout">Maximum model-load readiness wait.</param>
/// <param name="LaunchPolicy">Frozen cache/speculative policy; currently only <see cref="LlamaServerBenchmarkLaunchPolicy.DeterministicV1" /> is supported.</param>
public sealed record TransientLlamaServerEvaluationRequest(
    string ModelFilePath,
    string? AdapterFilePath,
    int ContextTokens,
    TimeSpan ReadinessTimeout,
    LlamaServerBenchmarkLaunchPolicy LaunchPolicy)
{
    /// <summary>Maximum post-tree-kill wait before teardown returns explicit timeout evidence.</summary>
    public TimeSpan TeardownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
