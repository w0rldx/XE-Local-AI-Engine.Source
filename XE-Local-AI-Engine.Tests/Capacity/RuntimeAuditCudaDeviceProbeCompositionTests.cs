namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The node registers its audit-aware CUDA probe so the whisper and sd-server providers' TryAdd default never lands.</summary>
[Category(TestCategories.Integration)]
public sealed class RuntimeAuditCudaDeviceProbeCompositionTests
{
    [Test]
    public void Composition_TheNodeProbeBeatsTheProvidersDefault()
    {
        // Same order as AddNodeApplication: the model-fit module registers before the transcription and image modules add their providers.
        var builder = Host.CreateApplicationBuilder();
        builder.AddNodeModelFit(builder.Configuration);
        builder.Services.AddWhisperCppRuntime();
        builder.Services.AddStableDiffusionCppImageProvider();

        var registrations = builder.Services.Where(static d => d.ServiceType == typeof(ICudaDeviceProbe)).ToList();
        AssertEx.Equal(1, registrations.Count);
        AssertEx.Equal(typeof(RuntimeAuditCudaDeviceProbe), registrations[0].ImplementationType);
    }
}
