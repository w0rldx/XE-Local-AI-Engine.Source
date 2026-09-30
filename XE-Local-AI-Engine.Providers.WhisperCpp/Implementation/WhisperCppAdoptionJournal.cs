namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>The intent record written before an adoption touches a directory, and the input to its recovery.</summary>
internal sealed record WhisperCppAdoptionJournal(
    Guid BuildId,
    WhisperBackend NewBackend,
    string NewCommit,
    bool HadPreviousDestination,
    WhisperInstalledRuntimeState? PreviousState,
    WhisperInstalledRuntimeState NewState);
