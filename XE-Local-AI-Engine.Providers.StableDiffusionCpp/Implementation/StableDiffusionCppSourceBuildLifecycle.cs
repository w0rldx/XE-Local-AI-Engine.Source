namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

internal sealed class StableDiffusionCppSourceBuildLifecycle : ManagedSourceBuildLifecycle
{
    public StableDiffusionCppSourceBuildLifecycle(IStableDiffusionCppSourceBuildService service,
        ILogger<StableDiffusionCppSourceBuildLifecycle> logger)
        : base(service.RecoverAsync, service.ShutdownAsync, logger, "stable-diffusion.cpp")
    {
    }
}
