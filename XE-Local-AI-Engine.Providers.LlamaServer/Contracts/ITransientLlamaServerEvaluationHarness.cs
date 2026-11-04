namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Content-addressed identity of the base GGUF and optional adapter actually held stable through an evaluation load.</summary>
public sealed record TransientLlamaServerModelProvenance
{
    public required string ModelId { get; init; }

    public required long ModelSizeBytes { get; init; }

    public required string ModelSha256 { get; init; }

    public required string? AdapterId { get; init; }

    public required long? AdapterSizeBytes { get; init; }

    public required string? AdapterSha256 { get; init; }
}

/// <summary>Proof that the evaluation harness requested and completed ownership teardown for its child process.</summary>
public sealed class TransientLlamaServerTeardownEvidence
{
    public required int ProcessId { get; init; }

    public required bool TreeKillRequested { get; init; }

    public required bool ProcessExitObserved { get; init; }

    public required bool ExitObservationTimedOut { get; init; }

    public required bool HandleDisposed { get; init; }
}

/// <summary>Validated model and launch identity that must be bound before evaluation writes its first sample.</summary>
public sealed record TransientLlamaServerEvaluationProvenance
{
    public required TransientLlamaServerModelProvenance Model { get; init; }

    public required LlamaServerLaunchReceipt Launch { get; init; }
}

/// <summary>A ready evaluation endpoint plus the exact model and launch evidence captured before scoring begins.</summary>
public sealed class TransientLlamaServerEvaluationSession
{
    public required Uri BaseAddress { get; init; }

    public required string ModelId { get; init; }

    public required TransientLlamaServerModelProvenance Model { get; init; }

    public required LlamaServerLaunchReceipt Launch { get; init; }

    /// <summary>The vector this launch applied, or <see langword="null" /> for an unsteered one.</summary>
    public TransientLlamaServerControlVector? ControlVector { get; init; }

    public TransientLlamaServerEvaluationProvenance Provenance =>
        new()
        {
            Model = Model,
            Launch = Launch
        };
}

/// <summary>The caller result paired with immutable launch/model provenance and post-body teardown evidence.</summary>
public sealed class TransientLlamaServerEvaluationResult<T>
{
    public required T Value { get; init; }

    public required TransientLlamaServerModelProvenance Model { get; init; }

    public required LlamaServerLaunchReceipt Launch { get; init; }

    public required TransientLlamaServerTeardownEvidence Teardown { get; init; }

    /// <summary>The vector this launch applied, or <see langword="null" /> for an unsteered one.</summary>
    public TransientLlamaServerControlVector? ControlVector { get; init; }

    public TransientLlamaServerEvaluationProvenance Provenance =>
        new()
        {
            Model = Model,
            Launch = Launch
        };
}

/// <summary>
///     Runs one path-addressed training evaluation under the supervisor's exclusive runtime-mutation lease.
/// </summary>
/// <remarks>
///     The harness refuses to start beside any warm or in-flight supervised model, owns GPU-load admission, pins a
///     frozen benchmark launch policy, content-addresses the model inputs, and returns teardown evidence after the
///     body completes.
/// </remarks>
public interface ITransientLlamaServerEvaluationHarness
{
    Task<TransientLlamaServerEvaluationResult<T>> RunAsync<T>(TransientLlamaServerEvaluationRequest request,
        Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
        Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct);

    /// <summary>
    ///     Runs every request in order, each on its own process and load ticket, under one runtime-mutation lease, so nothing else can
    ///     load between arms. Each distinct GGUF is hashed once.
    /// </summary>
    /// <remarks>
    ///     Every control vector is checked before the first load. A failing arm or cancellation stops the rest and releases the lease;
    ///     results of finished arms are lost with the exception, so a body persists what it needs. Arms take no adapter. An arm whose
    ///     process was not seen to exit stops the run the same way, but the last arm returns normally, so a caller must check its
    ///     <see cref="TransientLlamaServerTeardownEvidence.ProcessExitObserved" />.
    /// </remarks>
    Task<IReadOnlyList<TransientLlamaServerEvaluationResult<T>>> RunArmsAsync<T>(IReadOnlyList<TransientLlamaServerEvaluationRequest> requests,
        Func<int, TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct);

    /// <summary>The same, for a job that already holds the runtime across an earlier phase of its own, so nothing can load in the gap.</summary>
    /// <remarks>
    ///     The other overload would wait on the caller's own lease forever. The lease stays the caller's: it is not released here, on any path.
    /// </remarks>
    Task<IReadOnlyList<TransientLlamaServerEvaluationResult<T>>> RunArmsAsync<T>(ILlamaServerRuntimeMutationLease heldLease,
        IReadOnlyList<TransientLlamaServerEvaluationRequest> requests,
        Func<int, TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
        CancellationToken ct);
}

public sealed record TransientLlamaServerEvaluationRequest
{
    /// <summary>Absolute path to the base or merged GGUF.</summary>
    public required string ModelFilePath { get; init; }

    /// <summary>Optional staged LoRA GGUF applied to the base.</summary>
    public required string? AdapterFilePath { get; init; }

    /// <summary>Frozen context window used for both baseline and tuned evaluation.</summary>
    public required int ContextTokens { get; init; }

    /// <summary>Maximum model-load readiness wait.</summary>
    public required TimeSpan ReadinessTimeout { get; init; }

    /// <summary>Frozen cache/speculative policy; currently only <see cref="LlamaServerBenchmarkLaunchPolicy.DeterministicV1" /> is supported.</summary>
    public required LlamaServerBenchmarkLaunchPolicy LaunchPolicy { get; init; }

    /// <summary>Maximum post-tree-kill wait before teardown returns explicit timeout evidence.</summary>
    public TimeSpan TeardownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Absolute path to a control-vector GGUF. The harness refuses one that does not match the model before anything starts.</summary>
    public string? ControlVectorPath { get; init; }

    /// <summary>The multiplier for <see cref="ControlVectorPath" />; ignored without one.</summary>
    public double ControlVectorScale { get; init; } = 1;
}
