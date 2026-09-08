namespace XE_Local_AI_Engine.Tests.Integration;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the composition root against the gate-off regression: <c>AddOllamaLocalModelProvider</c> holds the only
///     real <c>IModelCapabilityClient</c> registration, so opting out of the OPTIONAL Ollama runtime used to leave the
///     mandatory <see cref="ModelCapabilityProber" /> unactivatable and the host unbuildable. Each test owns its host
///     because the gate is a host-build-time configuration value, which a shared factory cannot vary.
/// </summary>
public sealed class OllamaRuntimeGateStartupTests
{
    [Test]
    public async Task Host_WithTheOllamaRuntimeGateOff_BuildsAndReportsTheRuntimeUnreachable()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OllamaRuntimeGate.RuntimeEnabledConfigurationKey] = "false"
            }
        };

        // Resolving the reporter forces the container to activate the prober, which is where the missing
        // IModelCapabilityClient used to surface as a host-build failure.
        AssertEx.NotNull(factory.Services.GetRequiredService<ICapabilityReporter>(),
            "ICapabilityReporter must resolve with XE_OLLAMA_RUNTIME_ENABLED=false; the no-op capability client is what keeps the host buildable.");

        var prober = factory.Services.GetRequiredService<ModelCapabilityProber>();
        var status = await prober.DetectOllamaRuntimeAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(status.Reachable, "With the Ollama runtime gated off the node must report the runtime unreachable, not throw.");
        AssertEx.Null(status.Version, "An unreachable runtime reports no version.");
        AssertEx.Contains(status.Diagnostics, "ollama-unreachable",
            "The gate-off report must carry the same diagnostic a desktop without an Ollama daemon carries.");
    }

    [Test]
    public async Task UnavailableModelCapabilityClient_AnswersEveryProbeAsNothingToProbe()
    {
        var client = new UnavailableModelCapabilityClient();
        var cancellationToken = CancellationToken.None;

        AssertEx.False(await client.IsRuntimeReachableAsync(cancellationToken).ConfigureAwait(false), "The absent runtime is never reachable.");
        AssertEx.Null(await client.GetRuntimeVersionAsync(cancellationToken).ConfigureAwait(false), "The absent runtime reports no version.");
        AssertEx.Empty(await client.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false), "The absent runtime has no installed models.");
        AssertEx.Empty(await client.ListRunningModelsAsync(cancellationToken).ConfigureAwait(false), "The absent runtime has no running models.");

        var detail = await client.GetModelDetailAsync("any-model", cancellationToken).ConfigureAwait(false);
        AssertEx.Null(detail.MaxContextTokens, "The absent runtime cannot report a max context length.");
    }
}
