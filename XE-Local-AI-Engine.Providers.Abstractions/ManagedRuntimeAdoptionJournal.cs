namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The intent record written before an adoption touches a directory, and the input to its recovery.</summary>
public sealed record ManagedRuntimeAdoptionJournal<TBackend>(
    Guid BuildId,
    TBackend NewBackend,
    string NewCommit,
    bool HadPreviousDestination,
    ManagedRuntimeState<TBackend>? PreviousState,
    ManagedRuntimeState<TBackend> NewState)
    where TBackend : struct, Enum;
