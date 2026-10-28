namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Secure, atomic persistence for one runtime's managed source-built runtime record.</summary>
public interface IManagedRuntimeStore<TBackend>
    where TBackend : struct, Enum
{
    Task<ManagedRuntimeState<TBackend>?> ReadAsync(CancellationToken ct);

    Task WriteAsync(ManagedRuntimeState<TBackend> state, CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);
}
