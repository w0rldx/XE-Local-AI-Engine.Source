namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

internal sealed record StableDiffusionCppAdoptionJournal(
    Guid BuildId,
    SdGpuBackend NewBackend,
    string NewCommit,
    bool HadPreviousDestination,
    StableDiffusionInstalledRuntimeState? PreviousState,
    StableDiffusionInstalledRuntimeState NewState);
