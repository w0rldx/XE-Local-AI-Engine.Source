namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A chat client a background job builds for itself (never resolved from DI) must still emit a gen_ai span, and
///     that span must stay metadata-only even when the operator has opted the interactive pipeline into
///     sensitive-content capture. The asymmetry is deliberate: the summarizer, memory-extraction and playbook-analysis
///     callers resolve their own node-local client precisely so conversation content does not leave the node, and a
///     span carrying prompts would cross the same boundary from the other side.
/// </summary>
public sealed class ProviderChatClientTelemetryTests
{
    private const string ModelId = "wrapper-test-model-71f0";
    private const string PromptText = "wrapper-test-prompt-71f0";
    private const string ResponseText = "wrapper-test-response-71f0";

    [Test]
    public async Task WithProviderTelemetry_EmitsAGenAiSpan_CarryingNoMessageContent()
    {
        var recorded = new ConcurrentQueue<Activity>();
        using var listener = ListenerFor(ProviderChatClientTelemetry.ActivitySourceName, recorded);
        ActivitySource.AddActivityListener(listener);

        // The fake is held in its own `using` as well as being owned by the wrapper: the wrapper disposes it too, and
        // its Dispose is idempotent, but CA2000 cannot see through the builder.
        using var innerClient = new FakeChatClient(ResponseText);
        using (var chatClient = innerClient.WithProviderTelemetry())
        {
            _ = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, PromptText)],
                new ChatOptions
                {
                    ModelId = ModelId
                });
        }

        // Select by this test's own model id: the source is process-static and sibling tests emit spans on it too.
        var activity = recorded.FirstOrDefault(candidate => string.Equals(candidate.GetTagItem("gen_ai.request.model") as string, ModelId, StringComparison.Ordinal))
                       ?? throw new AssertionException($"The wrapped client emitted no activity on '{ProviderChatClientTelemetry.ActivitySourceName}'.");

        AssertEx.Equal("chat", activity.GetTagItem("gen_ai.operation.name") as string,
            "the wrapper must emit the MEAI gen_ai chat span, not an untagged one");
        AssertEx.Equal(ModelId, activity.GetTagItem("gen_ai.request.model") as string,
            "metadata — the model the background job actually called — is the whole point of the span");

        AssertEx.False(Rendered(activity).Contains(PromptText, StringComparison.Ordinal),
            "the prompt must never appear in the span");
        AssertEx.False(Rendered(activity).Contains(ResponseText, StringComparison.Ordinal),
            "the completion must never appear in the span");
    }

    // The deliberate asymmetry: CaptureSensitiveContent is the operator's knob over the DI pipeline (interactive chat),
    // and the provider-resolved wrapper is deliberately deaf to it. Both halves are asserted together so a future
    // "make the wrapper consistent with the pipeline" edit fails here rather than silently exporting conversations.
    [Test]
    public void OperatorOptIn_EnablesThePipeline_ButNeverTheProviderWrapper()
    {
        using var innerClient = new FakeChatClient(ResponseText);
        using var wrapped = innerClient.WithProviderTelemetry();
        var wrapperTelemetry = wrapped.GetService(typeof(OpenTelemetryChatClient)) as OpenTelemetryChatClient
                               ?? throw new AssertionException("Expected the wrapper to expose an OpenTelemetryChatClient.");

        var services = new ServiceCollection();
        _ = services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _ = services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _ = services.AddSingleton<IChatClient>(_ => new FakeChatClient(ResponseText));
        _ = services.AddOptions<AgentTelemetryOptions>().Configure(static options => options.CaptureSensitiveContent = true);
        _ = services.DecorateChatClientPipeline();

        using var provider = services.BuildServiceProvider();
        var pipelineTelemetry = provider.GetRequiredService<IChatClient>().GetService(typeof(OpenTelemetryChatClient)) as OpenTelemetryChatClient
                                ?? throw new AssertionException("Expected the DI pipeline to expose an OpenTelemetryChatClient.");

        AssertEx.True(pipelineTelemetry.EnableSensitiveData,
            "the operator opt-in still governs the interactive pipeline");
        AssertEx.False(wrapperTelemetry.EnableSensitiveData,
            "the provider-resolved wrapper must hard-code EnableSensitiveData=false and never inherit the operator opt-in");
    }

    private static string Rendered(Activity activity)
    {
        var tags = activity.TagObjects.Select(static tag => $"{tag.Key}={tag.Value}");
        var events = activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{activityEvent.Name}:{tag.Key}={tag.Value}"));
        return string.Join('\n', tags.Concat(events));
    }

    private static ActivityListener ListenerFor(string sourceName, ConcurrentQueue<Activity> recorded)
    {
        return new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            // Stopped, not started: the response metadata lands on the span as it closes.
            ActivityStopped = recorded.Enqueue
        };
    }

    private sealed class FakeChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
