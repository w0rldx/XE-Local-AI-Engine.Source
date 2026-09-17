namespace XE_Local_AI_Engine.Tests.Telemetry;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     MEAI records a failed gen_ai call with <c>SetStatus(Error, error.Message)</c> regardless of
///     <c>EnableSensitiveData</c>, and provider exception messages carry request text or a raw HTTP response body.
///     The unit tests pin the processor's contract; the two end-to-end tests drive a REAL failure through the two
///     production construction sites (the background-chat telemetry hop and the OpenAI-compatible embedding hop),
///     assert the leak the processor exists for, and then assert it is gone once the processor has run.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GenAiErrorDescriptionRedactionProcessorTests
{
    private const string ActivitySourceName = "Microsoft.Extensions.AI";

    [Test]
    public void OnEnd_GenAiErrorSpan_ReplacesTheDescriptionWithTheErrorType()
    {
        var (source, listener) = GenAiSource();
        using (source)
            using (listener)
            {
                using var activity = StartRecorded(source, "chat");
                activity.SetStatus(ActivityStatusCode.Error, "the user's password is hunter2");
                activity.SetTag("error.type", "System.InvalidOperationException");
                activity.SetTag("gen_ai.request.model", "m");
                activity.AddEvent(new ActivityEvent("marker"));

                Redact(activity);

                AssertEx.Equal(ActivityStatusCode.Error, activity.Status);
                AssertEx.Equal("System.InvalidOperationException", activity.StatusDescription);
                AssertEx.Equal("System.InvalidOperationException", activity.GetTagItem("error.type") as string);
                AssertEx.Equal("m", activity.GetTagItem("gen_ai.request.model") as string);
                AssertEx.Contains(activity.Events, activityEvent => string.Equals(activityEvent.Name, "marker", StringComparison.Ordinal),
                    "the processor must not touch recorded events.");
            }
    }

    [Test]
    public void OnEnd_GenAiErrorSpanWithoutAnErrorType_ClearsTheDescription()
    {
        var (source, listener) = GenAiSource();
        using (source)
            using (listener)
            {
                using var activity = StartRecorded(source, "chat");
                activity.SetStatus(ActivityStatusCode.Error, "the user's password is hunter2");

                Redact(activity);

                AssertEx.Equal(ActivityStatusCode.Error, activity.Status);
                AssertEx.True(string.IsNullOrEmpty(activity.StatusDescription),
                    "with no low-cardinality error.type to fall back on there is nothing safe to export.");
            }
    }

    [Test]
    public void OnEnd_NonGenAiSource_LeavesTheDescriptionUntouched()
    {
        using var source = new ActivitySource("XE.Node");
        using var listener = ListenerFor(source.Name);
        ActivitySource.AddActivityListener(listener);

        using var activity = StartRecorded(source, "node.op");
        activity.SetStatus(ActivityStatusCode.Error, "node-owned diagnostic text");
        activity.SetTag("error.type", "System.InvalidOperationException");

        Redact(activity);

        AssertEx.Equal("node-owned diagnostic text", activity.StatusDescription);
    }

    [Test]
    public void OnEnd_GenAiSpanThatDidNotFail_IsLeftUntouched()
    {
        var (source, listener) = GenAiSource();
        using (source)
            using (listener)
            {
                using var unset = StartRecorded(source, "chat");
                using var ok = StartRecorded(source, "chat");
                ok.SetStatus(ActivityStatusCode.Ok);

                Redact(unset);
                Redact(ok);

                AssertEx.Equal(ActivityStatusCode.Unset, unset.Status);
                AssertEx.Equal(ActivityStatusCode.Ok, ok.Status);
                AssertEx.True(string.IsNullOrEmpty(unset.StatusDescription), "a healthy span must not gain a description.");
                AssertEx.True(string.IsNullOrEmpty(ok.StatusDescription), "a healthy span must not gain a description.");
            }
    }

    [Test]
    public async Task ProviderChatTelemetryHop_ProviderExceptionMessage_LeaksIntoTheSpanUntilTheProcessorRedactsIt()
    {
        const string modelId = "chat-failure-6b04";
        const string secret = "conversation-text-6b04-never-leaves-the-node";
        var recorded = new ConcurrentQueue<Activity>();
        using var listener = ListenerFor(ActivitySourceName, recorded);
        ActivitySource.AddActivityListener(listener);

        // The production wrapper the nine background chat sites use; nothing about this test re-implements it.
        // The inner client is held in its own `using` so CA2000 can see it disposed; WithProviderTelemetry's wrapper
        // also disposes it, and ThrowingChatClient.Dispose is a no-op, so the double dispose is harmless.
        using var provider = new ThrowingChatClient($"llama-server rejected the request: {secret}", modelId);
        using var client = provider.WithProviderTelemetry();

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
                client.GetResponseAsync([new ChatMessage(ChatRole.User, secret)], new ChatOptions
                {
                    ModelId = modelId
                }),
            "the fake provider must surface its failure through the telemetry hop.");

        var activity = SpanFor(recorded, modelId);

        // (i) The leak itself, asserted rather than described: MEAI copies error.Message into the status description
        // even though this hop pins EnableSensitiveData=false.
        AssertEx.Equal(ActivityStatusCode.Error, activity.Status);
        AssertEx.Contains(activity.StatusDescription, secret, message: "this documents the MEAI behaviour the processor exists for.");

        // (ii) After the export-boundary processor the description is metadata only.
        Redact(activity);

        AssertEx.NotNullOrEmpty(activity.GetTagItem("error.type") as string, "the semconv error marker must survive redaction.");
        AssertEx.False(activity.StatusDescription?.Contains(secret, StringComparison.Ordinal) == true,
            "the redacted description must not carry the provider message.");
        AssertEx.False(Rendered(activity).Contains(secret, StringComparison.Ordinal),
            "no tag or event may carry the sentinel either.");
    }

    [Test]
    public async Task OpenAICompatibleEmbeddingHop_ErrorResponseBody_LeaksIntoTheSpanUntilTheProcessorRedactsIt()
    {
        const string modelId = "embed-failure-6b04";
        const string secret = "knowledge-base-text-6b04-never-leaves-the-node";
        var recorded = new ConcurrentQueue<Activity>();
        using var listener = ListenerFor(ActivitySourceName, recorded);
        ActivitySource.AddActivityListener(listener);

        var recorder = new OpenAiWireRecorder
        {
            // A 500 whose BODY carries the sentinel: ClientResultException quotes the response body in its message.
            Responder = static _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"{{\"error\":{{\"message\":\"failed embedding {secret}\"}}}}", Encoding.UTF8, "application/json")
            }
        };

        // Same ownership shape as EmbeddingTelemetryHopTests: the HttpClient is owned here because
        // HttpClientPipelineTransport does not take ownership, and CA2000 cannot see through the handler transfer
        // (disposeHandler: true) or the transport wrapper, which owns nothing of its own.
#pragma warning disable CA2000
        using var httpClient = new HttpClient(recorder.CreateHandler(), disposeHandler: true);
        using var generator = OpenAICompatibleClientFactory.CreateEmbeddingGenerator(new Uri("http://127.0.0.1:1/v1/"),
            modelId,
            apiKey: null,
            TimeSpan.FromSeconds(30),
            new HttpClientPipelineTransport(httpClient));
#pragma warning restore CA2000

        _ = await AssertEx.ThrowsAsync<ClientResultException>(() => generator.GenerateAsync([secret]),
            "the canned 500 must surface as a client failure through the embedding telemetry hop.");

        var activity = SpanFor(recorded, modelId);

        AssertEx.Equal(ActivityStatusCode.Error, activity.Status);
        AssertEx.Contains(activity.StatusDescription, secret, message: "the response body reaches the span through the exception message.");

        Redact(activity);

        AssertEx.NotNullOrEmpty(activity.GetTagItem("error.type") as string, "the semconv error marker must survive redaction.");
        AssertEx.False(activity.StatusDescription?.Contains(secret, StringComparison.Ordinal) == true,
            "the redacted description must not carry the response body.");
        AssertEx.False(Rendered(activity).Contains(secret, StringComparison.Ordinal),
            "no tag or event may carry the sentinel either.");
    }

    private static void Redact(Activity activity)
    {
        using var processor = new GenAiErrorDescriptionRedactionProcessor();
        processor.OnEnd(activity);
    }

    private static Activity SpanFor(ConcurrentQueue<Activity> recorded, string modelId)
    {
        // Select by this test's own model id: the activity source is process-static and sibling tests emit on it too.
        return recorded.FirstOrDefault(candidate => string.Equals(candidate.GetTagItem("gen_ai.request.model") as string, modelId, StringComparison.Ordinal))
               ?? throw new AssertionException($"The hop emitted no activity for '{modelId}' on '{ActivitySourceName}'.");
    }

    private static string Rendered(Activity activity)
    {
        var tags = activity.TagObjects.Select(static tag => $"{tag.Key}={tag.Value}");
        var events = activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{activityEvent.Name}:{tag.Key}={tag.Value}"));
        return string.Join('\n', tags.Concat(events));
    }

    private static (ActivitySource Source, ActivityListener Listener) GenAiSource()
    {
        var source = new ActivitySource(ActivitySourceName);
        var listener = ListenerFor(source.Name);
        ActivitySource.AddActivityListener(listener);
        return (source, listener);
    }

    private static ActivityListener ListenerFor(string sourceName, ConcurrentQueue<Activity>? recorded = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded
        };

        if (recorded is not null)
        {
            // Stopped, not started: MEAI records the failure on the span as it closes.
            listener.ActivityStopped = recorded.Enqueue;
        }

        return listener;
    }

    private static Activity StartRecorded(ActivitySource source, string name)
    {
        var activity = source.StartActivity(name);
        AssertEx.NotNull(activity, "Expected the listener to create a recorded activity.");
        return activity!;
    }

    /// <summary>A provider that fails with a message embedding caller content — the real shape of a llama-server or
    ///     OpenAI-compatible failure, without a transport.</summary>
    private sealed class ThrowingChatClient(string message, string modelId) : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", defaultModelId: modelId) : null;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(message);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(message);
        }
    }
}
