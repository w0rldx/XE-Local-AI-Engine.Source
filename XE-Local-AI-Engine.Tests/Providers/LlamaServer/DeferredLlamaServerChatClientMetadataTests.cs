namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="DeferredLlamaServerChatClient" /> must answer <see cref="ChatClientMetadata" /> from
///     construction-time knowledge BEFORE its inner adapter exists. MEAI's <c>OpenTelemetryChatClient</c> snapshots
///     that metadata in its own constructor and never refreshes it, so a null answered before first use permanently
///     costs every span and metric emitted over a deferred client its <c>gen_ai.request.model</c> and
///     <c>gen_ai.provider.name</c> — and the background jobs that wrap one
///     (<c>ProviderChatClientTelemetry.WithProviderTelemetry</c> over the summarizer, memory-extraction,
///     playbook-analysis and config-draft clients) set no <c>ChatOptions.ModelId</c> to fill the gap.
/// </summary>
public sealed class DeferredLlamaServerChatClientMetadataTests
{
    private const string ModelName = "deferred-metadata-model-3f21";
    private static readonly Uri DeadEndpoint = new("http://127.0.0.1:9/v1");
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public void GetService_BeforeFirstUse_ReportsTheModelAndTheSameProviderTheBuiltAdapterReports()
    {
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = DeadEndpoint
        };
        using var client = new DeferredLlamaServerChatClient(supervisor, ModelName, NetworkTimeout);

        var deferred = AssertEx.NotNull(client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata,
            "a deferred client must expose metadata before its inner adapter exists");

        // The real post-init adapter, built exactly as EnsureInnerAsync builds it, is the authority on the provider
        // name — asserting a literal here instead would let the two drift apart silently.
        using var built = LlamaServerOpenAIAdapterFactory.CreateChatClient(DeadEndpoint, ModelName, NetworkTimeout);
        var innerMetadata = AssertEx.NotNull(built.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata,
            "the MEAI OpenAI adapter must expose ChatClientMetadata; the deferred answer is modelled on it");

        AssertEx.Equal(ModelName, deferred.DefaultModelId,
            "the model the request will carry is known at construction and is what gen_ai.request.model needs");
        var innerProviderName = AssertEx.NotNull(innerMetadata.ProviderName, "the built adapter must report a provider name");
        AssertEx.Equal(innerProviderName, deferred.ProviderName,
            "the provider name must be identical before and after init, or a span's provider changes mid-life");
        AssertEx.Equal(expected: 0, supervisor.EnsureCalls,
            "answering metadata must never start a llama-server process");
    }

    [Test]
    public void GetService_BeforeFirstUse_CarriesABoundBenchmarkEndpointAsTheProviderUri()
    {
        var binding = Substitute.For<ILlamaServerEndpointBinding>();
        binding.GetBoundEndpoint(ModelName, ModelRole.Chat)
               .Returns(new LlamaServerEndpoint(ModelName, ModelRole.Chat, DeadEndpoint));
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor(),
            ModelName,
            NetworkTimeout,
            endpointBinding: binding);

        var deferred = AssertEx.NotNull(client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata);

        AssertEx.Equal(DeadEndpoint, deferred.ProviderUri,
            "a bound endpoint is already resolved, so the pre-init metadata can carry the real address");

        // Without a binding there is no address to report yet; a fabricated one would be worse than none.
        using var unbound = new DeferredLlamaServerChatClient(new FakeProcessSupervisor(), ModelName, NetworkTimeout);
        var unboundMetadata = AssertEx.NotNull(unbound.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata);
        AssertEx.Null(unboundMetadata.ProviderUri);
    }

    [Test]
    public async Task WithProviderTelemetry_OverADeferredClient_EmitsASpanCarryingTheModelId()
    {
        // The failure this pins: the background jobs wrap the deferred client BEFORE it has ever been used, and
        // OpenTelemetryChatClient reads the metadata once, in its own constructor.
        var recorded = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, ProviderChatClientTelemetry.ActivitySourceName, StringComparison.Ordinal),
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = recorded.Enqueue
        };

        ActivitySource.AddActivityListener(listener);

        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = DeadEndpoint
        };
        // Owned by the telemetry wrapper below, which disposes it; the extra using is CA2000's price for a builder.
        using var deferred = new DeferredLlamaServerChatClient(supervisor, ModelName, NetworkTimeout);
        using (var wrapped = deferred.WithProviderTelemetry())
        {
            // Nothing listens on the dead port, so the call fails at transport — the span is still emitted, and its
            // request-model tag is written from the metadata snapshot taken before any of this ran.
            _ = await AssertEx.ThrowsAsync<Exception>(() => wrapped.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        }

        // The source is process-static; select this test's own span by its model id.
        AssertEx.ContainsSingle(recorded,
            activity => string.Equals(activity.GetTagItem("gen_ai.request.model") as string, ModelName, StringComparison.Ordinal),
            "the span over a never-yet-used deferred client must carry gen_ai.request.model");
    }
}
