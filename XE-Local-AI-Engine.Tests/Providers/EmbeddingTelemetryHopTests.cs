namespace XE_Local_AI_Engine.Tests.Providers;

using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Both node-local embedding construction sites — the shared OpenAI-compatible transport (which serves
///     llama-server) and the Ollama provider — must emit a metadata-only gen_ai span, on the same activity source the
///     agent chat pipeline uses. Embedding inputs are conversation, memory and knowledge-base text, so the hop pins
///     <c>EnableSensitiveData</c> false rather than reading the operator's interactive-pipeline opt-in.
///     Both tests drive a real embedding call through the production factory, not a re-applied wrapper.
/// </summary>
public sealed class EmbeddingTelemetryHopTests
{
    private const string ActivitySourceName = "Microsoft.Extensions.AI";
    private const string SecretInput = "embedding-input-9c31-never-in-a-span";

    [Test]
    public async Task OpenAICompatibleFactory_EmbeddingCall_EmitsAGenAiSpanCarryingNoInputText()
    {
        const string modelId = "openai-compatible-embed-9c31";
        var recorded = new ConcurrentQueue<Activity>();
        using var listener = ListenerFor(recorded);
        ActivitySource.AddActivityListener(listener);

        var recorder = new OpenAiWireRecorder
        {
            Responder = static _ => EmbeddingsResponse()
        };

        // The HttpClient is owned here, not by the generator: HttpClientPipelineTransport does not take ownership of
        // the client it is given, and OpenAIClient is not IDisposable — so nothing downstream would ever dispose it.
        // Declared before the generator so it outlives the call and is disposed after it. CA2000 is scoped off for
        // the two objects it cannot see through: the handler transfers to the HttpClient (disposeHandler: true) and
        // the transport is a thin pipeline wrapper that owns nothing of its own.
#pragma warning disable CA2000
        using var httpClient = new HttpClient(recorder.CreateHandler(), disposeHandler: true);
        using var generator = OpenAICompatibleClientFactory.CreateEmbeddingGenerator(new Uri("http://127.0.0.1:1/v1/"),
            modelId,
            apiKey: null,
            TimeSpan.FromSeconds(30),
            new HttpClientPipelineTransport(httpClient));
#pragma warning restore CA2000

        var embeddings = await generator.GenerateAsync([SecretInput]);

        AssertEx.Equal(expected: 1, embeddings.Count, "the canned response carries exactly one embedding.");
        AssertSpanIsMetadataOnly(generator, recorded, modelId);
    }

    [Test]
    public async Task OllamaProvider_EmbeddingCall_EmitsAGenAiSpanCarryingNoInputText()
    {
        const string modelId = "ollama-embed-9c31";
        var recorded = new ConcurrentQueue<Activity>();
        using var listener = ListenerFor(recorded);
        ActivitySource.AddActivityListener(listener);

        await using var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = [modelId],
            EmbeddingDimensions = 8
        }, CancellationToken.None);

#pragma warning disable CA2000 // Ownership transfers to the factory, which is disposed below.
        var httpClient = new HttpClient
        {
            BaseAddress = server.BaseAddress
        };
#pragma warning restore CA2000
        using var clientFactory = new OllamaApiClientFactory(httpClient, ownsHttpClient: true);
        using var baseClient = clientFactory.CreateClient(selectedModel: null);
        using var provider = new OllamaLocalModelProvider(baseClient, clientFactory);

        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = modelId,
            ProviderName = OllamaLocalModelProvider.OllamaProviderName
        });

        var embeddings = await generator.GenerateAsync([SecretInput]);

        AssertEx.Equal(expected: 1, embeddings.Count, "the fake server answers one input with one embedding.");
        AssertSpanIsMetadataOnly(generator, recorded, modelId);
    }

    private static void AssertSpanIsMetadataOnly(IEmbeddingGenerator<string, Embedding<float>> generator,
        ConcurrentQueue<Activity> recorded,
        string modelId)
    {
        // Select by this test's own model id: the source is process-static and sibling tests emit spans on it too.
        var activity = recorded.FirstOrDefault(candidate => string.Equals(candidate.GetTagItem("gen_ai.request.model") as string, modelId, StringComparison.Ordinal))
                       ?? throw new AssertionException($"The generator emitted no activity for '{modelId}' on '{ActivitySourceName}'.");

        AssertEx.False(string.IsNullOrWhiteSpace(activity.GetTagItem("gen_ai.operation.name") as string),
            "the span must carry the gen_ai operation name, not be an untagged activity.");
        AssertEx.False(Rendered(activity).Contains(SecretInput, StringComparison.Ordinal),
            "the embedded text must never appear in the span.");

        var telemetryGenerator = generator.GetService(typeof(OpenTelemetryEmbeddingGenerator<string, Embedding<float>>)) as OpenTelemetryEmbeddingGenerator<string, Embedding<float>>
                                 ?? throw new AssertionException("Expected the construction site to install an OpenTelemetryEmbeddingGenerator.");
        AssertEx.False(telemetryGenerator.EnableSensitiveData,
            "the embedding hop must hard-code EnableSensitiveData=false and never inherit the operator opt-in.");
    }

    private static string Rendered(Activity activity)
    {
        var tags = activity.TagObjects.Select(static tag => $"{tag.Key}={tag.Value}");
        var events = activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{activityEvent.Name}:{tag.Key}={tag.Value}"));
        return string.Join('\n', tags.Concat(events));
    }

    private static ActivityListener ListenerFor(ConcurrentQueue<Activity> recorded)
    {
        return new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, ActivitySourceName, StringComparison.Ordinal),
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            // Stopped, not started: the response metadata lands on the span as it closes.
            ActivityStopped = recorded.Enqueue
        };
    }

    private static HttpResponseMessage EmbeddingsResponse()
    {
        const string payload = """
                               {"object":"list","model":"m","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2,0.3]}],"usage":{"prompt_tokens":3,"total_tokens":3}}
                               """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }
}
