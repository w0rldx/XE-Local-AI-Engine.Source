namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Capacity admission for a cold pooled-role (<see cref="ModelRole.Embedding" />, <see cref="ModelRole.Reranker" />)
///     spawn, consulted by the supervisor before it launches one.
/// </summary>
/// <remarks>
///     Pooled roles are spawned below every caller, so they cannot follow the caller-side decide-then-hold protocol chat
///     spawns use. The supervisor calls this once per cold launch, under its per-key ensure gate, and disposes the
///     returned reservation when the spawn settles (ready or failed): a resident process is then netted out of the
///     measured free VRAM. Implemented by the application layer over its capacity gate.
/// </remarks>
public interface ILlamaServerPooledLaunchAdmission
{
    /// <summary>
    ///     Admits the launch and returns the reservation to hold until the spawn settles, or <see langword="null" /> when
    ///     nothing needs holding.
    /// </summary>
    /// <remarks>Throws a sanitized <see cref="LlamaRuntimeException" /> when capacity refuses the launch.</remarks>
    Task<IDisposable?> AdmitAsync(string modelName, ModelRole role, CancellationToken ct);
}
