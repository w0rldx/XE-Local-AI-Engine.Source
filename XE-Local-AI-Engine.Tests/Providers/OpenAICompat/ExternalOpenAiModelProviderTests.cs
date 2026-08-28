namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The external provider's surface as the node's routing, catalog and lifecycle code sees it: one multiplexer
///     serving every connection, declarations rather than probes, and a lifecycle split where the operations the node
///     genuinely cannot perform refuse LOUDLY while the ones a background service calls generically stay inert.
/// </summary>
public sealed class ExternalOpenAiModelProviderTests
{
    [Test]
    public void ProviderName_IsTheSingleExternalKey()
    {
        // One key for every connection: the provider resolver snapshots the provider set in its constructor, so a
        // provider-per-connection design would need a container rebuild each time an operator adds one.
        AssertEx.Equal("external", Build(new FakeExternalProviderRegistry()).ProviderName);
    }

    [Test]
    public async Task ListModelsAsync_ProjectsTheOperatorsDeclarationsOntoDescriptors()
    {
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(contextLength: 65536, supportsTools: true, supportsVision: true, supportsReasoning: true));

        var models = await Build(registry).ListModelsAsync(CancellationToken.None);

        var descriptor = models.Single();
        AssertEx.Equal(ExternalProviderTestData.ModelId, descriptor.ModelName);
        AssertEx.Equal("external", descriptor.ProviderName);
        AssertEx.True(descriptor.IsAvailable, "a registered model is available; reachability is the health surface's job.");
        AssertEx.Equal(65536, descriptor.MaxContextTokens);
        AssertEx.True(descriptor.IsToolCapable);
        AssertEx.True(descriptor.IsMultimodalCapable);
        AssertEx.True(descriptor.IsReasoningCapable);
        // Native reasoning is a llama.cpp chat-template concept; claiming it would divert the model out of the graded
        // effort path this provider actually implements.
        AssertEx.False(descriptor.IsNativeReasoningCapable);
        // Vacuously true: this provider emits no llama-server budget field, so no cap can be silently lost.
        AssertEx.True(descriptor.ReasoningBudgetEnforceable);
        // Size and install time are properties of node-local weights, which an external model has none of here.
        AssertEx.Null(descriptor.SizeBytes);
        AssertEx.Null(descriptor.ModifiedAt);
        AssertEx.Contains(descriptor.Capabilities, "completion");
        AssertEx.Contains(descriptor.Capabilities, "tools");
        AssertEx.Contains(descriptor.Capabilities, "thinking");
        AssertEx.Contains(descriptor.Capabilities, "vision");
    }

    [Test]
    public async Task ListModelsAsync_WithNoConnections_ReturnsEmpty()
    {
        AssertEx.Empty(await Build(new FakeExternalProviderRegistry()).ListModelsAsync(CancellationToken.None));
    }

    [Test]
    public async Task ListModelsAsync_ForANonToolNonReasoningModel_ClaimsNothingItWasNotDeclared()
    {
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());

        var descriptor = (await Build(registry).ListModelsAsync(CancellationToken.None)).Single();

        AssertEx.False(descriptor.IsToolCapable);
        AssertEx.False(descriptor.IsReasoningCapable);
        AssertEx.False(descriptor.IsMultimodalCapable);
        AssertEx.Equal(1, descriptor.Capabilities.Count);
    }

    [Test]
    public async Task GetRuntimeInfoAsync_ReportsTheDeclaredContextWindow()
    {
        // Without this the turn budgeter falls back to its conservative default and silently truncates history on a
        // large-window endpoint — the declaration is the only window figure that exists for an external model.
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(contextLength: 32768));

        var info = await Build(registry).GetRuntimeInfoAsync(ExternalProviderTestData.ModelId, CancellationToken.None);

        AssertEx.Equal(32768, AssertEx.NotNull(info).EffectiveContextTokens);
    }

    [Test]
    public async Task GetRuntimeInfoAsync_WithNoDeclaredWindowOrNoRegistration_ReportsUnknown()
    {
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var provider = Build(registry);

        AssertEx.Null(await provider.GetRuntimeInfoAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        AssertEx.Null(await provider.GetRuntimeInfoAsync("ext:gone/model", CancellationToken.None));
    }

    [Test]
    public async Task WarmAndUnload_AreBenignNoOps()
    {
        // The keep-warm background service warms whatever model is selected without asking which runtime serves it,
        // so throwing here would turn an external default model into a recurring background failure.
        var provider = Build(new FakeExternalProviderRegistry());

        await provider.WarmModelAsync(ExternalProviderTestData.ModelId, CancellationToken.None);
        await provider.UnloadModelAsync(ExternalProviderTestData.ModelId, CancellationToken.None);
    }

    [Test]
    public async Task PullAndDelete_ThrowTheTypedRefusalTheEndpointLayerMapsTo409()
    {
        // Succeeding silently at deleting a model the node does not own would be a lie the UI renders as a completed
        // deletion — the opposite trade-off from warm/unload above, and deliberately so.
        var provider = Build(new FakeExternalProviderRegistry());

        _ = await AssertEx.ThrowsAsync<ExternalProviderOperationNotSupportedException>(() =>
            provider.PullModelAsync(ExternalProviderTestData.ModelId, progress: null, CancellationToken.None));
        _ = await AssertEx.ThrowsAsync<ExternalProviderOperationNotSupportedException>(() =>
            provider.DeleteModelAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
    }

    [Test]
    public void CreateEmbeddingGenerator_Throws()
    {
        // Embeddings stay on the node: knowledge and playbook text is embedded before any model sees it, so routing it
        // to a registered endpoint would send corpus content the user never chose to send.
        _ = AssertEx.Throws<NotSupportedException>(() =>
            Build(new FakeExternalProviderRegistry()).CreateEmbeddingGenerator(new LocalModelSelection
            {
                ModelName = ExternalProviderTestData.ModelId,
                ProviderName = "external"
            }));
    }

    [Test]
    public void CreateChatClient_WithAForeignProviderSelection_Throws()
    {
        _ = AssertEx.Throws<ArgumentException>(() =>
            Build(new FakeExternalProviderRegistry()).CreateChatClient(new LocalModelSelection
            {
                ModelName = ExternalProviderTestData.ModelId,
                ProviderName = "llamacpp"
            }).Dispose());
    }

    [Test]
    public async Task CheckHealthAsync_WithNoConnections_IsHealthy()
    {
        // Nothing configured is not the same as something broken.
        var health = await Build(new FakeExternalProviderRegistry()).CheckHealthAsync(CancellationToken.None);

        AssertEx.True(health.IsHealthy);
        AssertEx.Equal("external", health.ProviderName);
    }

    [Test]
    public async Task CheckHealthAsync_ProbesTheModelListingAndReportsPerConnection()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099"),
            ExternalProviderTestData.Model(),
            apiKey: "sk-probe");

        var health = await Build(registry, recorder).CheckHealthAsync(CancellationToken.None);

        AssertEx.True(health.IsHealthy);
        AssertEx.Equal("http://127.0.0.1:18099/v1/models", recorder.LastRequest.Uri?.AbsoluteUri);
        AssertEx.Equal("Bearer sk-probe", recorder.LastRequest.Authorization);
        // Diagnostics reach the operator, so they name the connection and never its base URL or key.
        AssertEx.ContainsSingle(health.Diagnostics, diagnostic => diagnostic.Contains("Unsloth box", StringComparison.Ordinal));
        AssertEx.False(health.Diagnostics.Any(diagnostic => diagnostic.Contains("18099", StringComparison.Ordinal)));
    }

    [Test]
    public async Task CheckHealthAsync_WhenTheEndpointRejectsTheListing_IsUnhealthy()
    {
        var recorder = new OpenAiWireRecorder
        {
            Responder = static _ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());

        var health = await Build(registry, recorder).CheckHealthAsync(CancellationToken.None);

        AssertEx.False(health.IsHealthy);
    }

    private static ExternalOpenAiModelProvider Build(IExternalProviderRegistry registry, OpenAiWireRecorder? recorder = null)
    {
        return new ExternalOpenAiModelProvider(registry, recorder is null ? null : recorder.CreateHandler);
    }
}
