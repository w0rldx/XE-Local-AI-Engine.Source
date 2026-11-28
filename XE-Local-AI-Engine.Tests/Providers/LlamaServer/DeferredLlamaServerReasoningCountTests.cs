namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pinned llama-server reports no reasoning token count, so the deferred client counts each round's reasoning
///     text with the same server's <c>/tokenize</c>, and stamps the thinking budget it put on the wire on that round's usage.
/// </summary>
/// <remarks>
///     Driven through both entry points against a loopback server (<see cref="CapturingServer" />),
///     so the MEAI adapter's own mapping of <c>reasoning_content</c> and of the usage chunk is exercised, not assumed. Only the
///     native client is substituted. Counting is best effort: every failure leaves the count null and the turn intact.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class DeferredLlamaServerReasoningCountTests
{
    private const string Reasoning = "Let me think. Then answer.";

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ARoundWithReasoning_GetsItsCountFromTheSameServersTokenize(bool streaming)
    {
        using var server = Start(streaming, extraUsage: "");
        var native = NativeClientCounting(tokens: 7);
        using var client = Client(server, native);

        var usage = await RunAsync(client, streaming, new ChatOptions());

        AssertEx.Equal(7L, usage.ReasoningTokenCount);
        AssertEx.False(IsStampedUncounted(usage), "a counted round is not uncounted");
        await native.Received(1).TokenizeAsync(server.BaseAddress, Reasoning, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AProviderReportedCount_IsKept_AndNothingIsTokenized(bool streaming)
    {
        using var server = Start(streaming, extraUsage: ",\"completion_tokens_details\":{\"reasoning_tokens\":3}");
        var native = NativeClientCounting(tokens: 7);
        using var client = Client(server, native);

        var usage = await RunAsync(client, streaming, new ChatOptions());

        AssertEx.Equal(3L, usage.ReasoningTokenCount);
        await native.DidNotReceiveWithAnyArgs().TokenizeAsync(default!, default!, default);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ARoundWithoutReasoning_StaysUnmeasured_AndNothingIsTokenized(bool streaming)
    {
        using var server = Start(streaming, extraUsage: "", reasoning: null);
        var native = NativeClientCounting(tokens: 7);
        using var client = Client(server, native);

        var usage = await RunAsync(client, streaming, new ChatOptions());

        AssertEx.Null(usage.ReasoningTokenCount, "no reasoning is unmeasured, not zero");
        AssertEx.False(IsStampedUncounted(usage), "a round without reasoning has nothing to count");
        await native.DidNotReceiveWithAnyArgs().TokenizeAsync(default!, default!, default);
    }

    [Test]
    [Arguments("throws")]
    [Arguments("status")]
    [Arguments("body")]
    [Arguments("timeout")]
    public async Task AFailedCount_LeavesItNull_AndTheStreamCompletes(string failure)
    {
        using var server = Start(streaming: true, extraUsage: "");
        var native = Substitute.For<ILlamaServerNativeClient>();
        switch (failure)
        {
            case "throws":
                native.TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
                break;
            case "status":
                native.TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Tokenized(HttpStatusCode.InternalServerError, "{\"tokens\":[1,2]}"));
                break;
            case "body":
                native.TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Tokenized(HttpStatusCode.OK, "not json"));
                break;
            default:
                // Parks until the client's own short timeout cancels it.
                native.TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns(async call =>
                      {
                          await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                          return Tokenized(HttpStatusCode.OK, "{\"tokens\":[1]}");
                      });
                break;
        }

        using var client = Client(server, native);

        var updates = await DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions(), CancellationToken.None));

        AssertEx.Null(UsageOf(updates).ReasoningTokenCount);
        AssertEx.True(IsStampedUncounted(UsageOf(updates)), "the turn total must know this round's reasoning went uncounted");
        AssertEx.Equal("ok", string.Concat(updates.Select(static update => update.Text)), "the answer still arrives whole");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AStreamedRoundThatReasonedWithoutAUsageChunk_StillReachesTheTurnTotal(bool countable)
    {
        // A tool round reasoned and ended on [DONE] with no usage; the final round reported 255.
        using var toolRound = Start(streaming: true, extraUsage: "", withUsage: false);
        using var finalRound = Start(streaming: true, extraUsage: ",\"completion_tokens_details\":{\"reasoning_tokens\":255}");
        var native = countable ? NativeClientCounting(tokens: 7) : Substitute.For<ILlamaServerNativeClient>();
        if (!countable)
        {
            native.TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
        }

        using var first = Client(toolRound, native);
        using var last = Client(finalRound, native);

        var updates = await DrainAsync(first.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions(), CancellationToken.None));
        updates.AddRange(await DrainAsync(last.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions(), CancellationToken.None)));

        // Folded exactly as the runner folds every usage update of a turn.
        InvocationRunner.UsageSnapshot? turn = null;
        foreach (var usage in updates.SelectMany(static update => update.Contents).OfType<UsageContent>())
        {
            turn = InvocationRunner.UsageSnapshot.Accumulate(turn, InvocationRunner.UsageSnapshot.From(usage.Details));
        }

        // Counted, the tool round adds its own 7; uncounted, the turn's reasoning is unknown rather than the last round's 255.
        AssertEx.Equal(countable ? (int?)262 : null,AssertEx.NotNull(turn).ReasoningTokens);
        AssertEx.Equal("okok", string.Concat(updates.Select(static update => update.Text)), "both answers arrive whole");
    }

    [Test]
    public async Task AnAnswerOnlyRoundWithoutAUsageChunk_EndsOnItsOwnUsage_NotTheEarlierRounds()
    {
        // A measured tool round, then an answer-only round that ends on [DONE] with no usage: the message's last-round
        // counts must be that round's (unknown), and the turn keeps the tool round's exact reasoning, the only reasoning it produced.
        using var toolRound = Start(streaming: true, extraUsage: "");
        using var answerRound = Start(streaming: true, extraUsage: "", reasoning: null, withUsage: false);
        var native = NativeClientCounting(tokens: 7);
        using var first = Client(toolRound, native);
        using var last = Client(answerRound, native);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.ReasoningBudgetMarkerKey] = 4096
            }
        };

        var updates = await DrainAsync(first.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None));
        updates.AddRange(await DrainAsync(last.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None)));

        // Folded exactly as the runner's StreamState.AddUsage folds every usage update of a turn.
        var usages = updates.SelectMany(static update => update.Contents).OfType<UsageContent>().Select(static usage => usage.Details).ToList();
        AssertEx.Equal(2, usages.Count, "the answer round ends on a usage update of its own");
        InvocationRunner.UsageSnapshot? turn = null;
        InvocationRunner.UsageSnapshot? lastRound = null;
        foreach (var usage in usages)
        {
            lastRound = InvocationRunner.UsageSnapshot.From(usage);
            turn = InvocationRunner.UsageSnapshot.Accumulate(turn, lastRound);
        }

        var answerUsage = AssertEx.NotNull(lastRound);
        AssertEx.Null(answerUsage.InputTokens);
        AssertEx.Null(answerUsage.OutputTokens);
        AssertEx.Null(answerUsage.ReasoningTokens);
        AssertEx.Equal(7, AssertEx.NotNull(turn).ReasoningTokens);
        AssertEx.True(AssertEx.NotNull(usages[1].AdditionalCounts).TryGetValue(DeferredLlamaServerChatClient.AppliedReasoningBudgetUsageKey, out var applied));
        AssertEx.Equal(4096L, applied);
        AssertEx.False(IsStampedUncounted(usages[1]), "an answer without reasoning has nothing uncounted");
        await native.Received(1).TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARoundThatStreamsNothing_EndsOnItsOwnUsage_NotTheEarlierRounds()
    {
        // A measured tool round, then a final round whose SSE body is only [DONE]: no update reaches the client before the stream ends.
        using var toolRound = Start(streaming: true, extraUsage: "");
        using var emptyRound = CapturingServer.Start("data: [DONE]\n\n", "text/event-stream");
        var native = NativeClientCounting(tokens: 7);
        using var first = Client(toolRound, native);
        using var last = Client(emptyRound, native);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.ReasoningBudgetMarkerKey] = 4096
            }
        };

        var updates = await DrainAsync(first.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None));
        var emptyUpdates = await DrainAsync(last.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None));

        var emptyUsage = UsageOf(emptyUpdates);
        AssertEx.Equal<ChatRole?>(ChatRole.Assistant, emptyUpdates.Single().Role);
        AssertEx.Null(emptyUsage.InputTokenCount);
        AssertEx.Null(emptyUsage.OutputTokenCount);
        AssertEx.Null(emptyUsage.ReasoningTokenCount);
        AssertEx.False(IsStampedUncounted(emptyUsage), "a round without reasoning has nothing uncounted");
        AssertEx.True(AssertEx.NotNull(emptyUsage.AdditionalCounts).TryGetValue(DeferredLlamaServerChatClient.AppliedReasoningBudgetUsageKey, out var applied));
        AssertEx.Equal(4096L, applied);

        // Folded as the runner folds: the turn keeps the tool round's 7, the last round is the empty one.
        var turn = InvocationRunner.UsageSnapshot.Accumulate(InvocationRunner.UsageSnapshot.From(UsageOf(updates)), InvocationRunner.UsageSnapshot.From(emptyUsage));
        AssertEx.Equal(7, turn.ReasoningTokens);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AStopWhileTheReasoningIsCounted_EndsTheStreamCancelled(bool withUsage)
    {
        // Without a usage chunk the count runs after the provider stream has ended, so nothing later can observe the Stop.
        using var server = Start(streaming: true, extraUsage: "", withUsage: withUsage);
        var counting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = Substitute.For<ILlamaServerNativeClient>();
        native.TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  counting.TrySetResult();
                  await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                  return Tokenized(HttpStatusCode.OK, "{\"tokens\":[1]}");
              });
        using var client = Client(server, native);
        using var stop = new CancellationTokenSource();

        var drain = DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions(), stop.Token));
        await counting.Task.WaitAsync(TestBudgets.Contended);
        await stop.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => drain.WaitAsync(TestBudgets.Contended), "a Stop during the count is a Stop, not a finished round");
    }

    [Test]
    public async Task WithoutANativeClient_TheCountStaysNull()
    {
        using var server = Start(streaming: true, extraUsage: "");
        using var client = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
        {
            EnsureEndpoint = server.BaseAddress
        }, "qwen3:8b", TimeSpan.FromSeconds(30));

        var usage = await RunAsync(client, streaming: true, new ChatOptions());

        AssertEx.Null(usage.ReasoningTokenCount);
        AssertEx.True(IsStampedUncounted(usage));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ABudgetedRound_StampsTheBudgetItPutOnTheWire_OnThatRoundsUsage(bool streaming)
    {
        using var server = Start(streaming, extraUsage: "");
        using var client = Client(server, NativeClientCounting(tokens: 7));
        var options = new ChatOptions
        {
            MaxOutputTokens = 2048,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeferredLlamaServerChatClient.ReasoningBudgetMarkerKey] = 4096
            }
        };

        var usage = await RunAsync(client, streaming, options);

        // An explicit output limit of 2048 halves to 1024: the clamp's value, not the marker's, is what ran.
        AssertEx.True(AssertEx.NotNull(usage.AdditionalCounts).TryGetValue(DeferredLlamaServerChatClient.AppliedReasoningBudgetUsageKey, out var applied));
        AssertEx.Equal(1024L, applied);
    }

    [Test]
    public async Task ARoundWithoutABudget_StampsNoAppliedBudget()
    {
        using var server = Start(streaming: true, extraUsage: "");
        using var client = Client(server, NativeClientCounting(tokens: 7));

        var usage = await RunAsync(client, streaming: true, new ChatOptions());

        AssertEx.False(usage.AdditionalCounts?.ContainsKey(DeferredLlamaServerChatClient.AppliedReasoningBudgetUsageKey) == true);
    }

    [Test]
    [Arguments(null, false, null, 4096)]
    [Arguments(2048, false, null, 1024)]
    [Arguments(4096 + 16_384, true, null, 4096)]
    [Arguments(null, false, 6000, 3000)]
    [Arguments(10_000, false, 6000, 3000)]
    public void TheAppliedBudget_IsTheClampedWireValue(int? maxOutputTokens, bool defaultCap, int? window, int expected)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            [DeferredLlamaServerChatClient.ReasoningBudgetMarkerKey] = 4096
        };
        if (defaultCap)
        {
            properties[DeferredLlamaServerChatClient.DefaultOutputCapMarkerKey] = true;
        }

        if (window is { } numCtx)
        {
            properties[SamplingOptionKeys.NumCtx] = numCtx;
        }

        var options = new ChatOptions
        {
            MaxOutputTokens = maxOutputTokens,
            AdditionalProperties = properties
        };

        AssertEx.Equal(expected, DeferredLlamaServerChatClient.ResolveAppliedReasoningBudget(options));
        AssertEx.Null(DeferredLlamaServerChatClient.ResolveAppliedReasoningBudget(new ChatOptions()));
    }

    [Test]
    public void TheAppliedBudgetUsageKey_MatchesTheOneTheBudgetHopReads()
    {
        AssertEx.Equal(ProviderCallBudgetChatClient.AppliedReasoningBudgetUsageKey, DeferredLlamaServerChatClient.AppliedReasoningBudgetUsageKey);
    }

    [Test]
    public void TheReasoningUncountedUsageKey_MatchesTheOneTheTurnAccumulatorReads()
    {
        AssertEx.Equal(InvocationRunner.UsageSnapshot.ReasoningUncountedUsageKey, DeferredLlamaServerChatClient.ReasoningUncountedUsageKey);
    }

    private static bool IsStampedUncounted(UsageDetails usage) =>
        usage.AdditionalCounts?.TryGetValue(DeferredLlamaServerChatClient.ReasoningUncountedUsageKey, out var uncounted) == true && uncounted == 1;

    private static DeferredLlamaServerChatClient Client(CapturingServer server, ILlamaServerNativeClient native) =>
        new(new FakeProcessSupervisor
        {
            EnsureEndpoint = server.BaseAddress
        }, "qwen3:8b", TimeSpan.FromSeconds(30), nativeClient: native);

    private static ILlamaServerNativeClient NativeClientCounting(int tokens)
    {
        var native = Substitute.For<ILlamaServerNativeClient>();
        var body = $"{{\"tokens\":[{string.Join(',', Enumerable.Range(1, tokens))}]}}";
        native.TokenizeAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Tokenized(HttpStatusCode.OK, body));
        return native;
    }

    // The tokenize response owns the message it wraps, and the client under test disposes it.
#pragma warning disable CA2000
    private static LlamaServerTokenizeResponse Tokenized(HttpStatusCode status, string body) =>
        new(new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        });
#pragma warning restore CA2000

    private static async Task<UsageDetails> RunAsync(DeferredLlamaServerChatClient client, bool streaming, ChatOptions options)
    {
        if (!streaming)
        {
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
            return AssertEx.NotNull(response.Usage);
        }

        return UsageOf(await DrainAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None)));
    }

    private static UsageDetails UsageOf(List<ChatResponseUpdate> updates) =>
        updates.SelectMany(static update => update.Contents).OfType<UsageContent>().Single().Details;

    private static async Task<List<ChatResponseUpdate>> DrainAsync(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in stream)
        {
            updates.Add(update);
        }

        return updates;
    }

    // extraUsage is spliced into the usage object, so a test can add the provider's own completion_tokens_details.
    private static CapturingServer Start(bool streaming, string extraUsage, string? reasoning = Reasoning, bool withUsage = true)
    {
        var usage = "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":9,\"total_tokens\":14" + extraUsage + "}";
        if (!streaming)
        {
            var reasoningField = reasoning is null ? string.Empty : ",\"reasoning_content\":\"" + reasoning + "\"";
            return CapturingServer.Start(
                "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"qwen3:8b\","
                + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"" + reasoningField + "},\"finish_reason\":\"stop\"}],"
                + usage + "}",
                "application/json");
        }

        // The reasoning arrives in two deltas, as llama-server streams it; the usage rides its own final chunk.
        var reasoningChunks = reasoning is null
            ? string.Empty
            : Chunk("{\"role\":\"assistant\",\"reasoning_content\":\"" + reasoning[..13] + "\"}") + Chunk("{\"reasoning_content\":\"" + reasoning[13..] + "\"}");
        return CapturingServer.Start(
            reasoningChunks
            + Chunk("{\"content\":\"ok\"}")
            + "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + (withUsage ? "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\",\"choices\":[]," + usage + "}\n\n" : string.Empty)
            + "data: [DONE]\n\n",
            "text/event-stream");

        static string Chunk(string delta) =>
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\",\"choices\":[{\"index\":0,\"delta\":" + delta
            + ",\"finish_reason\":null}]}\n\n";
    }
}
