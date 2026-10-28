namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>The journaled adoption that turns a finished build tree into the node's managed whisper.cpp runtime.</summary>
internal sealed class WhisperCppRuntimeAdoption : ManagedRuntimeAdoption<WhisperBackend>
{
    public WhisperCppRuntimeAdoption(string cacheRoot,
        IManagedRuntimeStore<WhisperBackend> runtimeStore,
        IManagedSourceBuildSignal<WhisperBackend> managedSignal,
        TimeProvider timeProvider,
        ILogger logger)
        : base(cacheRoot, "whisper.cpp", "whisper.cpp runtime", "whisper-server", runtimeStore, managedSignal, timeProvider, logger)
    {
    }

    /// <inheritdoc cref="ManagedRuntimeAdoption{TBackend}.AdoptCoreAsync" />
    public async Task AdoptAsync(string buildDir,
        string serverPath,
        WhisperCppSourceBuildDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        await AdoptCoreAsync(buildDir,
            serverPath,
            descriptor.BuildId,
            new ManagedRuntimeState<WhisperBackend>(ManagedRuntimeValidity.Active,
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

    protected override string BackendSlug(WhisperBackend backend)
    {
        return backend switch
        {
            WhisperBackend.Cuda => "cuda",
            _ => "cpu"
        };
    }

    protected override Exception RuntimeError(string message, Exception? innerException = null)
    {
        return innerException is null ? new WhisperRuntimeException(message) : new WhisperRuntimeException(message, innerException);
    }
}
