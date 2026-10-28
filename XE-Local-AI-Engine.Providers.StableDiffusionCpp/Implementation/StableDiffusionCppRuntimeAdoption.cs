namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>The journaled adoption that turns a finished build tree into the node's managed image runtime.</summary>
internal sealed class StableDiffusionCppRuntimeAdoption : ManagedRuntimeAdoption<SdGpuBackend>
{
    public StableDiffusionCppRuntimeAdoption(string cacheRoot,
        IManagedRuntimeStore<SdGpuBackend> runtimeStore,
        IManagedSourceBuildSignal<SdGpuBackend> managedSignal,
        TimeProvider timeProvider,
        ILogger logger)
        : base(cacheRoot, "stable-diffusion.cpp", "image runtime", "sd-server", runtimeStore, managedSignal, timeProvider, logger)
    {
    }

    /// <inheritdoc cref="ManagedRuntimeAdoption{TBackend}.AdoptCoreAsync" />
    public async Task AdoptAsync(string buildDir,
        string serverPath,
        StableDiffusionCppSourceBuildDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        await AdoptCoreAsync(buildDir,
            serverPath,
            descriptor.BuildId,
            new ManagedRuntimeState<SdGpuBackend>(ManagedRuntimeValidity.Active,
                descriptor.Backend,
                descriptor.Repository,
                descriptor.ResolvedCommit!,
                descriptor.Source,
                descriptor.RevisionMode,
                descriptor.RequestedCommit,
                SourceBuildPath: null,
                ServerSha256: null,
                InstalledAtUtc: default),
            ct).ConfigureAwait(false);
    }

    protected override string BackendSlug(SdGpuBackend backend)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => "cuda",
            SdGpuBackend.Vulkan => "vulkan",
            _ => "cpu"
        };
    }

    protected override Exception RuntimeError(string message, Exception? innerException = null)
    {
        return innerException is null
            ? new StableDiffusionRuntimeException(message)
            : new StableDiffusionRuntimeException(message, innerException);
    }
}
