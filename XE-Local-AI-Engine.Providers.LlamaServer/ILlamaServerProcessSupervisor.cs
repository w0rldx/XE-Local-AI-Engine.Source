namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Owns the lifecycle of every <c>llama-server</c> child process: reuse-or-spawn per <c>(model, role)</c>, health
///     aggregation, idle-TTL + loaded-cap eviction + reaper (shared policy, decision #15), restart-backoff, port
///     allocation, and tree-kill teardown. All processes are same-user, unprivileged, localhost-bound (decision #17).
/// </summary>
/// <remarks>
///     <para>
///         <strong>Seam only (this is the Lane A contract; T2 implements the body).</strong> Implementations are
///         singleton — the supervisor owns all processes and disposes them on shutdown. Every async member flows a
///         <see cref="CancellationToken" />.
///     </para>
///     <para>
///         Mandatory launch flags: a chat process is launched with <c>--jinja</c>; an embedding process is launched
///         with a non-<c>none</c> pooling type. Each distinct <c>(model, role)</c> is a distinct process and counts
///         against the loaded-cap.
///     </para>
/// </remarks>
public interface ILlamaServerProcessSupervisor
{
    /// <summary>
    ///     Reuses the running <c>(model, role)</c> process or spawns one (single-flight per key), then returns its
    ///     localhost OpenAI-compatible endpoint. Spawning a new distinct model when the loaded-cap is full rejects
    ///     (decision #18).
    /// </summary>
    /// <exception cref="LlamaRuntimeException">
    ///     Spawn failed, the loaded-cap was reached, or the restart-backoff cap was exceeded — message is sanitized.
    /// </exception>
    Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>Evicts (tree-kills) the <c>(model, role)</c> process if running and releases its port. Idempotent.</summary>
    Task EvictAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Aggregates every running process's health into one snapshot — operational iff the supervisor can serve
    ///     requests; per-process detail is surfaced for diagnostics.
    /// </summary>
    Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct);
}
