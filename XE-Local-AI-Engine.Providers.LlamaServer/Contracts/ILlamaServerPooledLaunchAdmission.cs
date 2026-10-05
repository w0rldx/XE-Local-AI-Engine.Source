namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Capacity admission for a cold supervisor spawn — a pooled-role (<see cref="ModelRole.Embedding" />,
///     <see cref="ModelRole.Reranker" />) or a <see cref="ModelRole.Chat" /> launch — consulted by the supervisor before it
///     launches one.
/// </summary>
/// <remarks>
///     Pooled roles and the main chat path are spawned below every caller, so they cannot decide-then-hold the way a
///     sub-agent chat spawn does. The supervisor calls this once per cold launch, under its per-key ensure gate, and
///     disposes the reservation when the spawn settles: a resident process is then netted out of the measured free VRAM.
///     A chat launch a caller already admitted (a published launch admission for its key) skips the hook.
/// </remarks>
public interface ILlamaServerPooledLaunchAdmission
{
    /// <summary>
    ///     Admits a pooled-role launch and returns the reservation to hold until the spawn settles, or
    ///     <see langword="null" /> when nothing needs holding.
    /// </summary>
    /// <remarks>Throws a sanitized <see cref="LlamaRuntimeException" /> when capacity refuses the launch.</remarks>
    Task<IDisposable?> AdmitAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Admits a chat launch against global free VRAM and returns the reservation to hold until the spawn settles, or
    ///     <see langword="null" /> when nothing needs holding.
    /// </summary>
    /// <param name="modelName">The chat model to launch.</param>
    /// <param name="mayUnloadIdleModels">False for a background load: it never calls <paramref name="evictIdleModel" /> or waits, and loads only if it fits.</param>
    /// <param name="evictIdleModel">
    ///     Unloads the least recently used idle chat process, never a protected model name; or reports one only a lease kept,
    ///     or none.
    /// </param>
    /// <param name="ct">Cancels the decision.</param>
    /// <remarks>Throws a sanitized <see cref="LlamaRuntimeException" /> when capacity refuses the launch.</remarks>
    Task<IDisposable?> AdmitChatAsync(string modelName,
        bool mayUnloadIdleModels,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IdleChatEvictionResult>> evictIdleModel,
        CancellationToken ct);
}
