namespace XE_Local_AI_Engine.Tests.Integration;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the two no-ops the composition root substitutes when the OPTIONAL Ollama runtime is gated off, and the
///     gate-off host that needs them. <c>AddOllamaLocalModelProvider</c> holds the only real
///     <c>IModelCapabilityClient</c> and <c>IOllamaApiClient</c> registrations, so opting out used to leave
///     <see cref="ModelCapabilityProber" /> and <c>OllamaModelService</c> unactivatable. The host test here runs on the
///     fixture, which fakes an <c>IOllamaApiClient</c> unconditionally; the production-shape assertion lives in
///     <c>ServiceProviderValidationTests.CompositionRoot_WithTheOllamaRuntimeGateOff_BuildsWithScopeAndBuildValidationEnabled</c>.
///     Each test owns its host because the gate is a host-build-time configuration value a shared factory cannot vary.
/// </summary>
[Category(TestCategories.Integration)]
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
        var reporter = factory.Services.GetRequiredService<ICapabilityReporter>();

        AssertEx.False(await reporter.VerifyOllamaAndModelAsync(modelName: null, CancellationToken.None),
            "With the Ollama runtime gated off the preflight must report the runtime unreachable, not throw.");
    }

    [Test]
    public async Task UnavailableModelCapabilityClient_AnswersEveryProbeAsNothingToProbe()
    {
        var client = new UnavailableModelCapabilityClient();
        var cancellationToken = CancellationToken.None;

        AssertEx.False(await client.IsRuntimeReachableAsync(cancellationToken), "The absent runtime is never reachable.");
        AssertEx.Empty(await client.ListInstalledModelsAsync(cancellationToken), "The absent runtime has no installed models.");
    }

    [Test]
    public async Task UnavailableOllamaModelService_ListsNothingAndFailsTransportsLikeAnAbsentDaemon()
    {
        var service = new UnavailableOllamaModelService();
        var cancellationToken = CancellationToken.None;

        AssertEx.False(await service.IsAvailableAsync(cancellationToken), "The absent daemon is never available.");
        AssertEx.Empty(await service.ListLocalModelsAsync(cancellationToken), "The absent daemon holds no installed models.");
        AssertEx.Empty(await service.ListRunningModelsAsync(cancellationToken), "The absent daemon holds no running models.");

        // HttpRequestException with no StatusCode is exactly what a refused connection raises, and it is what
        // UnloadLocalModelEndpoint's `when (exception.StatusCode is null)` filter absorbs as "nothing was resident".
        var exception = await AssertEx.ThrowsAsync<HttpRequestException>(() => service.UnloadModelAsync("any-model", cancellationToken),
            "Unloading against a disabled runtime must fail the way an absent daemon fails.");

        AssertEx.Null(exception.StatusCode, "A transport failure carries no status code; a status would escape the endpoint's filter.");
        AssertEx.Contains(exception.Message, OllamaRuntimeGate.RuntimeEnabledConfigurationKey,
            message: "The message must name the gate so an operator knows why the runtime is absent.");
    }
}
