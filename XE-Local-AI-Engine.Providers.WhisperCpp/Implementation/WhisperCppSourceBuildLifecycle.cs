namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

internal sealed class WhisperCppSourceBuildLifecycle : ManagedSourceBuildLifecycle
{
    public WhisperCppSourceBuildLifecycle(IWhisperCppSourceBuildService service,
        ILogger<WhisperCppSourceBuildLifecycle> logger)
        : base(service.RecoverAsync, service.ShutdownAsync, logger, "whisper.cpp")
    {
    }
}
