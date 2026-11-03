namespace XE_Local_AI_Engine.Tests.Providers;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared OpenAI-compatible transport (which serves llama-server) must emit a metadata-only gen_ai span, on the
///     same activity source the agent chat pipeline uses. Embedding inputs are conversation, memory and knowledge-base
///     text, so the hop pins <c>EnableSensitiveData</c> false rather than reading the operator's interactive-pipeline
///     opt-in. The test drives a real embedding call through the production factory, not a re-applied wrapper.
/// </summary>
[Category(TestCategories.Integration)]
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

        await using var gateway = await FakeOpenAiGatewayServer.StartAsync(new FakeOpenAiGatewayOptions
        {
            AllowUnlistedModels = true,
            EmbeddingDimensions = 8
        });
        using var generator = OpenAICompatibleClientFactory.CreateEmbeddingGenerator(gateway.BaseAddress,
            modelId,
            apiKey: null,
            TimeSpan.FromSeconds(30));

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
}
