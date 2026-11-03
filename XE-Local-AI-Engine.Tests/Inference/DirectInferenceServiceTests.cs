namespace XE_Local_AI_Engine.Tests.Inference;

using System.ClientModel.Primitives;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Chat;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Inference.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
// OpenAI.Chat also declares ChatMessage and ChatFinishReason; this file speaks MEAI's.
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

/// <summary>
///     What Direct Inference sends and relays, over the real invocation slot and GPU gate; only the provider is substituted.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DirectInferenceServiceTests
{
    private const string ModelId = "qwen3.5-9b";
    private const string CodexModelId = "gpt-6";
    private const string AzureDeploymentName = "my-azure-deployment";
    private const string LocalExternalModelId = "ext:lan/qwen3";
    private const string CloudExternalModelId = "ext:hosted/qwen3";
    private const string UnknownModelId = "no-such-model";

    // Shaped from a captured b12014 chunk: the merged entry's alternatives are the last token's, and -3.4028235E+38 is what llama-server sends for a zero probability.
    private const string MultiByteChunkJson = """
                                              {"id":"c","object":"chat.completion.chunk","created":1,"model":"m",
                                               "choices":[{"index":0,"finish_reason":null,"delta":{"content":"🌏"},
                                                "logprobs":{"content":[{"id":237,"token":"🌏","bytes":[240,159,140,143],"logprob":-0.5,
                                                 "top_logprobs":[{"id":237,"token":"�","bytes":[143],"logprob":-0.5},
                                                                 {"id":243,"token":"�","bytes":[149],"logprob":-3.4028235E+38}]}]}}]}
                                              """;

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

    [Test]
    public async Task StreamAsync_PastTheDeadline_EndsWithOneTimeoutError_AndFreesTheSlot()
    {
        var harness = new Harness(null);
        harness.Stream = token => Hanging(harness, token);

        var received = await RunPastTheDeadlineAsync(harness);

        AssertEx.Equal("delta,error", string.Join(",", received.Select(static e => e.Type)), "the error is the only terminal event");
        AssertTimedOut(harness, received[^1]);
    }

    [Test]
    public async Task StreamAsync_AProviderThrowingSomethingElseOnTheDeadline_IsStillOneTimeoutError()
    {
        var harness = new Harness(null);
        harness.Stream = token => ThrowingOnCancellation(harness, token);

        var received = await RunPastTheDeadlineAsync(harness);

        AssertEx.Equal("delta,error", string.Join(",", received.Select(static e => e.Type)));
        AssertTimedOut(harness, received[^1]);
    }

    [Test]
    public async Task StreamAsync_AProviderEndingQuietlyOnTheDeadline_IsATimeoutAndNeverDone()
    {
        var harness = new Harness(null);
        harness.Stream = token => EndingQuietlyOnCancellation(harness, token);

        var received = await RunPastTheDeadlineAsync(harness);

        AssertEx.Equal("delta,error", string.Join(",", received.Select(static e => e.Type)));
        AssertTimedOut(harness, received[^1]);
    }

    [Test]
    public async Task StreamAsync_PastTheDeadlineBeforeTheFirstToken_IsOneTimeoutError()
    {
        var harness = new Harness(null);
        var providerWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Stream = token => HangingBeforeTheFirstToken(harness, providerWaiting, token);

        var stream = harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync().AsTask();
        await providerWaiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        harness.Time.Advance(TimeSpan.FromSeconds(harness.Settings.Current.MaxMessageRequestTimeoutSeconds));
        var received = await stream.WaitAsync(TimeSpan.FromSeconds(30));

        AssertTimedOut(harness, received.Single());
    }

    [Test]
    public async Task StreamAsync_TheDeadlineStartsAfterAdmission_SoQueueingCannotExpireIt()
    {
        var harness = new Harness(Updates(Text("late"), Finish()));
        var holder = await harness.Dispatcher.ReportInvocationAssignedAsync(RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        var stream = harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync().AsTask();
        AssertEx.False(stream.IsCompleted, "the request queues behind the slot's holder");

        AssertEx.Equal(0, harness.Time.ArmedTimerCount, "no deadline is armed while queued");
        harness.Time.Advance(TimeSpan.FromSeconds(harness.Settings.Current.MaxMessageRequestTimeoutSeconds * 2));
        await holder.DisposeAsync();
        var events = await stream.WaitAsync(TimeSpan.FromSeconds(30));

        AssertEx.Equal("delta,done", string.Join(",", events.Select(static e => e.Type)));
    }

    [Test]
    public async Task StreamAsync_StoppedByTheUser_IsNeverATimeout_EvenWithTheDeadlinePassed()
    {
        var harness = new Harness(null);
        harness.Stream = token => Hanging(harness, token);
        using var cancellation = new CancellationTokenSource();

        await AssertEx.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, cancellation.Token))
            {
                await cancellation.CancelAsync();
                harness.Time.Advance(TimeSpan.FromSeconds(harness.Settings.Current.MaxMessageRequestTimeoutSeconds));
            }
        });

        AssertEx.Null(harness.Dispatcher.SlotHolder);
        var terminal = harness.Recorded.Single(static state => state.CompletedAt is not null);
        AssertEx.Equal(InvocationStatus.Cancelled, terminal.Status);
        AssertEx.Equal(FailureCategory.Cancelled, terminal.FailureCategory);
    }

    [Test]
    public async Task StreamAsync_AnInstalledLlamaModelWithNoOutputLimit_SendsTheDefaultCap()
    {
        var harness = new Harness(Updates(Text("x"), Finish()));

        _ = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync();

        AssertEx.Equal(16_384, AssertEx.NotNull(harness.SentOptions).MaxOutputTokens);
    }

    [Test]
    [Arguments(CodexModelId)]
    [Arguments(AzureDeploymentName)]
    [Arguments(LocalExternalModelId)]
    [Arguments(CloudExternalModelId)]
    [Arguments(UnknownModelId)]
    public async Task StreamAsync_AModelNotServedByLlamaCppWithNoOutputLimit_SendsNoCap(string modelId)
    {
        var harness = new Harness(Updates(Text("x"), Finish()));

        _ = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = modelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync();

        var options = AssertEx.NotNull(harness.SentOptions);
        AssertEx.Null(options.MaxOutputTokens);
        AssertEx.False(AssertEx.NotNull(options.AdditionalProperties).ContainsKey(InvocationAgentDefinition.DefaultOutputCapMarkerKey), "no default limit, so no default-limit marker");
    }

    [Test]
    public async Task StreamAsync_AnInstalledModelMappedToAnotherProviderWithNoOutputLimit_SendsNoCap()
    {
        var harness = new Harness(Updates(Text("x"), Finish()));
        harness.Providers.ResolveProviderNameForModelAsync(ModelId, Arg.Any<CancellationToken>()).Returns("another-runtime");

        _ = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync();

        AssertEx.Null(AssertEx.NotNull(harness.SentOptions).MaxOutputTokens);
    }

    [Test]
    [Arguments(ModelId)]
    [Arguments(CodexModelId)]
    [Arguments(AzureDeploymentName)]
    [Arguments(LocalExternalModelId)]
    [Arguments(CloudExternalModelId)]
    [Arguments(UnknownModelId)]
    public async Task StreamAsync_ATypedLimit_IsSentForEveryModel(string modelId)
    {
        var harness = new Harness(Updates(Text("x"), Finish()));

        _ = await harness.Service.StreamAsync(new DirectInferenceRequest
                                 {
                                     ModelId = modelId,
                                     Prompt = "Hi",
                                     SamplingOptions = new SamplingOptions { MaxOutputTokens = 100 }
                                 },
                                 CancellationToken.None)
                             .ToListAsync();

        AssertEx.Equal(100, AssertEx.NotNull(harness.SentOptions).MaxOutputTokens);
    }

    [Test]
    public async Task ThePageStatesTheServersDefaultCap()
    {
        var source = await File.ReadAllTextAsync(RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React",
            "src",
            "core",
            "directInference",
            "DirectInferenceModels.ts"));

        var declaration = string.Create(CultureInfo.InvariantCulture, $"directInferenceDefaultMaxOutputTokens = {DirectInferenceService.DefaultMaxOutputTokens};");
        AssertEx.True(source.Contains(declaration, StringComparison.Ordinal), $"DirectInferenceModels.ts must declare '{declaration}'");
    }

    [Test]
    [Arguments(100, null, 100)]
    [Arguments(20_000, null, 20_000)]
    [Arguments(null, 4_096, 4_096)]
    public async Task StreamAsync_AnExplicitLimitOrSmallerWindow_WinsOverTheDefaultCap(int? maxOutputTokens, int? numCtx, int expected)
    {
        var harness = new Harness(Updates(Text("x"), Finish()));

        _ = await harness.Service.StreamAsync(new DirectInferenceRequest
                                 {
                                     ModelId = ModelId,
                                     Prompt = "Hi",
                                     SamplingOptions = new SamplingOptions { MaxOutputTokens = maxOutputTokens, NumCtx = numCtx }
                                 },
                                 CancellationToken.None)
                             .ToListAsync();

        AssertEx.Equal(expected, AssertEx.NotNull(harness.SentOptions).MaxOutputTokens);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(21)]
    [Arguments(1000)]
    public void StreamAsync_TopLogprobsOutOfRange_IsRefusedBeforeAnyStream(int topLogprobs)
    {
        var harness = new Harness(Updates(Text("never")));

        var exception = AssertEx.Throws<DirectInferenceInvalidRequestException>(() => harness.Service.StreamAsync(new DirectInferenceRequest
            {
                ModelId = ModelId,
                Prompt = "Hi",
                TopLogprobs = topLogprobs
            },
            CancellationToken.None));

        AssertEx.Contains(exception.Message, "between 1 and 20");
        AssertEx.Null(harness.SentMessages, "the provider is never called");
    }

    [Test]
    [Arguments(1)]
    [Arguments(20)]
    public async Task StreamAsync_TopLogprobsInRange_ReachesTheWireThroughTheProvidersOwnPatches(int topLogprobs)
    {
        var harness = new Harness(Updates(Text("x"), Finish()));

        _ = await harness.Service.StreamAsync(new DirectInferenceRequest
                                 {
                                     ModelId = ModelId,
                                     Prompt = "Hi",
                                     TopLogprobs = topLogprobs,
                                     SamplingOptions = new SamplingOptions { TopK = 40 }
                                 },
                                 CancellationToken.None)
                             .ToListAsync();

        // The deferred client's body patches run on top of what the service sends, as they do on the real path.
        var sent = AssertEx.NotNull(DeferredLlamaServerChatClient.ApplySamplingPassthrough(AssertEx.NotNull(harness.SentOptions)));
        using var body = JsonDocument.Parse(await LlamaGrammarToolOffer.CaptureWireBodyAsync(sent, CancellationToken.None));
        AssertEx.True(body.RootElement.GetProperty("logprobs").GetBoolean());
        AssertEx.Equal(topLogprobs, body.RootElement.GetProperty("top_logprobs").GetInt32());
        AssertEx.Equal(40, body.RootElement.GetProperty("top_k").GetInt32(), "the provider's patch composes with this one");
        AssertEx.Equal(16_384, body.RootElement.GetProperty("max_completion_tokens").GetInt32(), "the default cap survives the clone");
    }

    [Test]
    public async Task StreamAsync_WithoutTopLogprobs_RequestsNoneAndRelaysNone()
    {
        var harness = new Harness(Updates(Chunk(MultiByteChunkJson), Finish()));

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None).ToListAsync();

        var options = AssertEx.NotNull(harness.SentOptions);
        AssertEx.Null(options.RawRepresentationFactory, "the request stays byte-identical to one without the feature");
        AssertEx.Empty(events.Where(static e => e.Type == DirectInferenceEventTypes.Logprobs), "entries a provider volunteers are not read unless asked for");
    }

    [Test]
    [Arguments(CodexModelId)]
    [Arguments(AzureDeploymentName)]
    [Arguments(LocalExternalModelId)]
    [Arguments(CloudExternalModelId)]
    [Arguments(UnknownModelId)]
    public async Task StreamAsync_TopLogprobsForAModelNotServedByLlamaCpp_IsRefusedWithoutCallingIt(string modelId)
    {
        var harness = new Harness(Updates(Text("never")));

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = modelId, Prompt = "Hi", TopLogprobs = 3 }, CancellationToken.None).ToListAsync();

        var refusal = events.Single();
        AssertEx.Equal(DirectInferenceEventTypes.Error, refusal.Type);
        AssertEx.Equal(DirectInferenceErrorCodes.LogprobsLocalOnly, refusal.ErrorCode);
        AssertEx.Null(harness.SentMessages);
        AssertEx.Null(harness.Dispatcher.SlotHolder);
    }

    [Test]
    public async Task StreamAsync_TopLogprobsForAnInstalledModelMappedToAnotherProvider_IsRefused()
    {
        var harness = new Harness(Updates(Text("never")));
        harness.Providers.ResolveProviderNameForModelAsync(ModelId, Arg.Any<CancellationToken>()).Returns("another-runtime");

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi", TopLogprobs = 3 }, CancellationToken.None).ToListAsync();

        AssertEx.Equal(DirectInferenceErrorCodes.LogprobsLocalOnly, events.Single().ErrorCode);
    }

    [Test]
    public async Task StreamAsync_TopLogprobs_RelaysEntries_ToleratesGaps_AndKeepsUsageFromTheUsageEvent()
    {
        var reasoningChunk = Chunk(MultiByteChunkJson);
        reasoningChunk.Contents = [new TextReasoningContent("thinking")];
        var harness = new Harness(Updates(reasoningChunk, Chunk(MultiByteChunkJson), Text("no entry for this delta"), Usage(), Finish()));

        var events = await harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi", TopLogprobs = 3 }, CancellationToken.None).ToListAsync();

        var relayed = events.Where(static e => e.Type == DirectInferenceEventTypes.Logprobs).Select(static e => AssertEx.NotNull(e.Logprobs).Single()).ToList();
        AssertEx.Equal(2, relayed.Count, "the delta with no entry adds nothing and fails nothing");
        AssertEx.True(relayed[0].IsReasoning);
        AssertEx.False(relayed[1].IsReasoning);
        var merged = relayed[1];
        AssertEx.Equal("🌏", merged.Text, "a merged multi-byte entry keeps its whole text");
        AssertEx.Equal(-0.5f, merged.Logprob);
        AssertEx.Equal("�", merged.Alternatives[0].Text, "an alternative can be a partial token");
        AssertEx.Equal(-float.MaxValue, merged.Alternatives[1].Logprob, "llama-server's stand-in for a zero probability reads as a number");
        AssertEx.Equal(3L, AssertEx.NotNull(events.Single(static e => e.Type == DirectInferenceEventTypes.Usage).Usage).OutputTokens);
        AssertEx.Equal(DirectInferenceEventTypes.Done, events[^1].Type);
    }

    [Test]
    public async Task ThePageStatesTheServersTopLogprobsLimit()
    {
        var source = await File.ReadAllTextAsync(RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React",
            "src",
            "core",
            "directInference",
            "DirectInferenceModels.ts"));

        var declaration = string.Create(CultureInfo.InvariantCulture, $"directInferenceMaxTopLogprobs = {DirectInferenceService.MaxTopLogprobs};");
        AssertEx.True(source.Contains(declaration, StringComparison.Ordinal), $"DirectInferenceModels.ts must declare '{declaration}'");
    }

    private static async Task<List<DirectInferenceEvent>> RunPastTheDeadlineAsync(Harness harness)
    {
        List<DirectInferenceEvent> received = [];
        await foreach (var streamEvent in harness.Service.StreamAsync(new DirectInferenceRequest { ModelId = ModelId, Prompt = "Hi" }, CancellationToken.None))
        {
            received.Add(streamEvent);
            if (received.Count == 1)
            {
                harness.Time.Advance(TimeSpan.FromSeconds(harness.Settings.Current.MaxMessageRequestTimeoutSeconds));
            }
        }

        return received;
    }

    private static void AssertTimedOut(Harness harness, DirectInferenceEvent last)
    {
        AssertEx.Equal(DirectInferenceEventTypes.Error, last.Type);
        AssertEx.Equal(nameof(FailureCategory.Timeout), last.ErrorCode);
        AssertEx.NotNullOrEmpty(last.ErrorMessage);
        AssertEx.True(harness.ProviderObservedCancellation, "the deadline reaches the provider");
        AssertEx.Null(harness.Dispatcher.SlotHolder);
        AssertEx.Empty(harness.Dispatcher.GetLiveSnapshot().Live);
        var terminal = harness.Recorded.Single(static state => state.CompletedAt is not null);
        AssertEx.Equal(InvocationStatus.Failed, terminal.Status);
        AssertEx.Equal(FailureCategory.Timeout, terminal.FailureCategory);
    }

    private static ChatResponseUpdate Chunk(string chunkJson) =>
        new(ChatRole.Assistant, "text")
        {
            RawRepresentation = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(chunkJson))
        };

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
            // The gate is real over its seams: only ModelId is installed, and an unmapped id routes to llamacpp as in production.
            var models = Substitute.For<IGgufModelStore>();
            models.ExistsAsync(ModelId, Arg.Any<CancellationToken>()).Returns(true);
            Providers.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(LlamaServerProviderConstants.ProviderName);
            var trust = new FakeModelTrustResolver().Register("lan", "qwen3").Register("hosted", "qwen3", ExternalProviderLocality.Cloud);
            Service = new DirectInferenceService(chatClient, Dispatcher, Gate, Settings, models, trust, Providers, Time, NullLogger<DirectInferenceService>.Instance);
        }

        public Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Stream { get; set; }

        public ILocalModelProviderResolver Providers { get; } = Substitute.For<ILocalModelProviderResolver>();

        public ManualTimeProvider Time { get; } = new();

        public FakeNodeSettingsStore Settings { get; } = new(new StoredNodeSettings());

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

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowingOnCancellation(Harness harness, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Text("first");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            harness.ProviderObservedCancellation = true;
            throw new IOException("The connection was aborted.", exception);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EndingQuietlyOnCancellation(Harness harness, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return Text("first");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            harness.ProviderObservedCancellation = true;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> HangingBeforeTheFirstToken(Harness harness,
        TaskCompletionSource providerWaiting,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        providerWaiting.SetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            harness.ProviderObservedCancellation = true;
            throw;
        }

        yield break;
    }
}
