namespace XE_Local_AI_Engine.Tests.Inference;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Inference.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What Direct Inference sends and relays, over the real invocation slot and GPU gate; only the provider is substituted.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DirectInferenceServiceTests
{
    private const string ModelId = "qwen3.5-9b";

    [Test]
    public async Task StreamAsync_SendsSystemThenUser_WithNoTools_AndTheMappedSampling()
    {
        var harness = new Harness(Updates(Text("Hel"), Text("lo"), Usage(), Finish()));

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest
                                      {
                                          ModelId = $" {ModelId} ",
                                          SystemPrompt = "Be terse.",
                                          Prompt = "Say hello.",
                                          SamplingOptions = new SamplingOptions { Temperature = 0.5f, TopK = 40, Seed = "7" }
                                      },
                                      CancellationToken.None)
                                  .ToListAsync();

        var messages = AssertEx.NotNull(harness.SentMessages);
        AssertEx.Equal(2, messages.Count);
        AssertEx.Equal(ChatRole.System, messages[0].Role);
        AssertEx.Equal("Be terse.", messages[0].Text);
        AssertEx.Equal(ChatRole.User, messages[1].Role);
        AssertEx.Equal("Say hello.", messages[1].Text);
        var options = AssertEx.NotNull(harness.SentOptions);
        AssertEx.Equal(ModelId, options.ModelId);
        AssertEx.Null(options.Tools, "no tool is ever offered");
        AssertEx.Equal(0.5f, options.Temperature);
        AssertEx.Equal(40, options.TopK);
        AssertEx.Equal(7L, options.Seed);
        AssertEx.Equal("delta,delta,usage,done", string.Join(",", events.Select(static e => e.Type)), "a provider with no llama timings sends no timings event");
        AssertEx.Equal("Hel", events[0].TextDelta);
        var usage = AssertEx.NotNull(events[2].Usage);
        AssertEx.Equal(12L, usage.InputTokens);
        AssertEx.Equal(3L, usage.OutputTokens);
        AssertEx.Equal(4L, usage.CachedInputTokens);
        var done = events[^1];
        AssertEx.Equal("stop", done.FinishReason);
        AssertEx.True(done.ElapsedMs >= 0 && done.FirstTokenMs >= 0 && done.FirstTokenMs <= done.ElapsedMs);
        AssertEx.Null(harness.Dispatcher.SlotHolder, "the slot is free once the stream ends");
        AssertEx.Empty(harness.Dispatcher.GetLiveSnapshot().Live, "nothing reads the finished request as still running");
        var recorded = harness.Recorded.Single(static state => state.Status == InvocationStatus.Completed);
        AssertEx.Equal(2, recorded.StreamedChunkCount, "one per streamed text delta, as a chat turn counts them");
        AssertEx.Equal(0, recorded.StreamedThinkingChunkCount);
    }

    [Test]
    public async Task StreamAsync_WithoutASystemPrompt_SendsOnlyTheUserMessage_AndRelaysReasoning()
    {
        var harness = new Harness(Updates(new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking")]), Text("answer"), Finish()));

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, SystemPrompt = "  ", Prompt = "Hi" }, CancellationToken.None)
                                  .ToListAsync();

        var message = AssertEx.NotNull(harness.SentMessages).Single();
        AssertEx.Equal(ChatRole.User, message.Role);
        AssertEx.Equal("thinking", events[0].ReasoningDelta);
        AssertEx.Null(events[0].TextDelta);
        AssertEx.Equal("answer", events[1].TextDelta);
        var recorded = harness.Recorded.Single(static state => state.Status == InvocationStatus.Completed);
        AssertEx.Equal(1, recorded.StreamedChunkCount);
        AssertEx.Equal(1, recorded.StreamedThinkingChunkCount);
    }

    [Test]
    [Arguments(3f, null)]
    [Arguments(null, "not-a-number")]
    public void StreamAsync_AnOutOfRangeSamplingValue_IsRefusedBeforeAnyStream(float? temperature, string? seed)
    {
        var harness = new Harness(Updates(Text("never")));

        var exception = AssertEx.Throws<DirectInferenceInvalidRequestException>(() => harness.Service.StreamAsync(new DirectInferenceRequest
            {
                ModelId = ModelId,
                Prompt = "Hi",
                SamplingOptions = new SamplingOptions { Temperature = temperature, Seed = seed }
            },
            CancellationToken.None));

        AssertEx.NotNullOrEmpty(exception.Message);
        AssertEx.Null(harness.SentMessages, "the provider is never called");
    }

    [Test]
    public void StreamAsync_WithoutAModelOrPrompt_NamesBoth()
    {
        var harness = new Harness(Updates(Text("never")));

        var exception = AssertEx.Throws<DirectInferenceInvalidRequestException>(() => harness.Service.StreamAsync(new DirectInferenceRequest(), CancellationToken.None));

        AssertEx.Equal("A model is required. A prompt is required.", exception.Message);
    }

    [Test]
    public async Task StreamAsync_WhileAnExclusiveGpuJobRuns_FailsFastWithTheHolderNotice()
    {
        var harness = new Harness(Updates(Text("never")));
        AssertEx.Succeeded(harness.Gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var hold));
        using var holdScope = hold;

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync();

        var error = events.Single();
        AssertEx.Equal(DirectInferenceEventTypes.Error, error.Type);
        AssertEx.Equal(DirectInferenceErrorCodes.GpuWorkExclusive, error.ErrorCode);
        AssertEx.Equal("This message was queued behind a running training run.", error.ErrorMessage);
        AssertEx.Null(harness.SentMessages, "the provider is never called");
        AssertEx.Null(harness.Dispatcher.CurrentInvocation, "the slot was never taken");
    }

    [Test]
    public async Task StreamAsync_AProviderFailure_EndsWithAClassifiedErrorEvent_AndFreesTheSlot()
    {
        var harness = new Harness(Failing(new HttpRequestException("connection refused")));

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync();

        var error = events.Single();
        AssertEx.Equal(DirectInferenceEventTypes.Error, error.Type);
        AssertEx.Equal(nameof(FailureCategory.ProviderUnreachable), error.ErrorCode);
        AssertEx.NotNullOrEmpty(error.ErrorMessage);
        AssertEx.Null(harness.Dispatcher.SlotHolder);
        AssertEx.Empty(harness.Dispatcher.GetLiveSnapshot().Live);
    }

    [Test]
    public async Task StreamAsync_CancelledMidStream_StopsTheProvider_AndClosesTheLiveEntry()
    {
        var harness = new Harness(null);
        harness.Stream = token => Hanging(harness, token);
        using var cancellation = new CancellationTokenSource();
        List<DirectInferenceEvent> received = [];

        await AssertEx.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var streamEvent in harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, cancellation.Token))
            {
                received.Add(streamEvent);
                await cancellation.CancelAsync();
            }
        });

        AssertEx.Equal(DirectInferenceEventTypes.Delta, received.Single().Type);
        AssertEx.True(harness.ProviderObservedCancellation, "the stream token reaches the provider");
        AssertEx.Null(harness.Dispatcher.SlotHolder);
        AssertEx.Equal(InvocationStatus.Cancelled, AssertEx.NotNull(harness.Dispatcher.CurrentInvocation).Status);
    }

    private static ChatResponseUpdate Text(string text) =>
        new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Usage() =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 12, OutputTokenCount = 3, TotalTokenCount = 15, CachedInputTokenCount = 4 })]);

    private static ChatResponseUpdate Finish() =>
        new(ChatRole.Assistant, string.Empty) { FinishReason = ChatFinishReason.Stop };

    private static Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Updates(params ChatResponseUpdate[] updates) =>
        _ => updates.ToAsyncEnumerable();

    private static Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Failing(Exception exception) =>
        _ => Throwing(exception);

    private static async IAsyncEnumerable<ChatResponseUpdate> Throwing(Exception exception)
    {
        await Task.Yield();
        if (exception is not null)
        {
            throw exception;
        }

        yield break;
    }

    private sealed class Harness
    {
        public Harness(Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? stream)
        {
            Stream = stream ?? (_ => AsyncEnumerable.Empty<ChatResponseUpdate>());
            var chatClient = Substitute.For<IChatClient>();
            chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                      .Returns(call =>
                      {
                          SentMessages = call.Arg<IEnumerable<ChatMessage>>().ToList();
                          SentOptions = call.Arg<ChatOptions?>();
                          return Stream(call.Arg<CancellationToken>());
                      });
            History.When(history => history.Record(Arg.Any<InvocationState>())).Do(call => Recorded.Add(call.Arg<InvocationState>()));
            Dispatcher = new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
                History,
                NullLogger<WorkerEventDispatcher>.Instance,
                TimeProvider.System);
            Service = new DirectInferenceService(chatClient, Dispatcher, Gate, NullLogger<DirectInferenceService>.Instance);
        }

        public Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Stream { get; set; }

        public GpuWorkGate Gate { get; } = new();

        public IInvocationHistory History { get; } = Substitute.For<IInvocationHistory>();

        /// <summary>Every state the dispatcher published to the history; its terminal one writes the "Invocation completed" log line.</summary>
        public List<InvocationState> Recorded { get; } = [];

        public WorkerEventDispatcher Dispatcher { get; }

        public DirectInferenceService Service { get; }

        public List<ChatMessage>? SentMessages { get; private set; }

        public ChatOptions? SentOptions { get; private set; }

        public bool ProviderObservedCancellation { get; set; }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Hanging(Harness harness, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Text("first");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            harness.ProviderObservedCancellation = true;
            throw;
        }
    }
}
