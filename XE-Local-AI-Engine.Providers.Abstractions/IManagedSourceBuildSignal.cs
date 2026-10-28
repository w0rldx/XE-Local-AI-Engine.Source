namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Versioned in-memory signal for the backend a validated managed source build serves.
/// </summary>
/// <remarks>
///     Set when the binary manager resolves a managed runtime and cleared when it tombstones one, so a record proven
///     unusable stops steering selection toward a runtime that no longer works.
/// </remarks>
public interface IManagedSourceBuildSignal<TBackend>
    where TBackend : struct, Enum
{
    TBackend? ActiveBackend { get; }

    long Version { get; }

    void SetActive(TBackend backend);

    void Clear();
}
