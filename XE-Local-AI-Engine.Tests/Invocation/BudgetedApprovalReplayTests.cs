namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The combined replay gate for the outer budgeter's two last-resort passes. The budgeter's own unit tests grade its
///     arithmetic; this grades the thing that actually matters — that a history it rewrote is still a history the rest of
///     the chain accepts. Nothing here is a stand-in for a production component except the model itself:
///     <list type="number">
///         <item>
///             <description>
///                 <see cref="ConversationContextBudgeter" /> with both last-resort passes enabled and a window tight
///                 enough that they fire every round.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="ApprovalResponseValidatingAgent" /> over a real <c>ChatClientAgent</c> whose pipeline runs
///                 <c>UseFunctionInvocation</c> — the validator that fails an invocation outright when a replayed approval
///                 arrives without its request, or a resolved one without the result it produced.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="ProviderCallBudgetChatClient" /> beneath function invocation, under a real ambient
///                 <see cref="ProviderCallBudget" /> scope, with a SMALLER excerpt budget than the outer budgeter — so
///                 every oversized tool result is excerpted twice, once by each budgeter, which is the shape a long tool
///                 loop actually produces and the one most likely to mangle a result's identity.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The REAL MEAI OpenAI adapter, on both the llama-server lane and the plain OpenAI-shaped lane, so the
///                 assertion is about the bytes a provider would parse rather than about the in-memory object graph.
///             </description>
///         </item>
///     </list>
///     Scenarios covered by the one scripted conversation the harness drives: structured (JSON) tool results,
///     multi-content assistant messages, parallel tool calls in a single round, an approved tool alongside a rejected one,
///     and reasoning-only messages.
/// </summary>
public sealed class BudgetedApprovalReplayTests
{
    private const string QueryToolName = "structured_query";
    private const string ArchiveToolName = "archive_batch";
    private const string QueryCallId = "call-structured-query";
    private const string ArchiveCallId = "call-archive-batch";
    private const string WarmCallId = "call-warm-cache";
    private const string SystemPrompt = "Answer with the tools. Call structured_query and archive_batch together.";

    // Distinctive enough to prove by absence that the reasoning-only message was dropped WHOLE — an empty survivor would
    // also have lost the text, so a "no reasoning left" check alone could not tell the two outcomes apart.
    private static readonly string ReasoningOnlyMarker = new(c: 'p', count: 4000);

    // The outer budgeter excerpts to 200 chars and the inner one to 80, so a result large enough to trip the outer pass
    // is guaranteed to be re-excerpted by the inner hop — the double-excerpt case.
    private const int OuterExcerptChars = 200;
    private const int InnerExcerptChars = 80;

    // Both budgeters measure against TokenEstimatorCalibrationStore.EstimateSafetyFactor of their window, so the OUTER
    // capacity is stated as the value whose margined budget is 2048 — the number this scenario was built on. Stating a
    // bare 2048 here instead makes the outer budget 1740, i.e. TIGHTER than the inner window below, and the outer hop
    // then hands the inner one a round that already fits: the double excerpt silently stops happening and
    // BudgetedReplay_DoubleExcerptedStructuredResult_KeepsItsCallIdAndItsMarkers degenerates into a re-assertion of the
    // outer excerpt. The inner window stays a bare 2048 on purpose, so its margined 1740 sits strictly below the outer
    // budget and the inner hop must act on every round the outer one already reduced.
    private const int OuterContextTokens = 2410;

    [Test]
    public async Task BudgetedReplay_WithBothLastResortPassesOn_CompletesWithoutTrippingTheApprovalValidator()
    {
        var replay = await RunReplayAsync();

        AssertEx.Equal("all done", replay.FinalText, "the scripted conversation must reach its final answer");
        AssertEx.Equal(expected: 2, replay.ApprovalRequestsSurfaced, "both parallel tool calls must have been gated, or the scenario has degenerated");
        AssertEx.Equal(expected: 1, replay.QueryExecutions, "the approved tool must execute exactly once");
        AssertEx.Equal(expected: 0, replay.ArchiveExecutions, "the rejected tool must never execute");
        AssertEx.True(replay.ReasoningStripped > 0, "the round must actually have exercised Pass 4, not merely tolerated it");
        AssertEx.True(replay.ProtectedResultsExcerpted > 0, "the round must actually have exercised Pass 5");
        AssertEx.False(replay.EverExceededBudget, "the budgeter must never hand the chain an over-budget round");
    }

    [Test]
    public async Task BudgetedReplay_TheRoundTheBudgeterRewrote_StillCarriesEveryApprovalPair()
    {
        // The validator fails an invocation outright when a replayed response arrives without its request, or a resolved
        // one without the result it produced. The run completing at all is one half of that proof; this is the other —
        // the budgeted list the resume was actually driven from still holds both halves of both rounds.
        var replay = await RunReplayAsync();

        var requestIds = replay.LastBudgeted
                               .SelectMany(static message => message.Contents)
                               .OfType<ToolApprovalRequestContent>()
                               .Select(static request => request.RequestId)
                               .ToList();
        var responseIds = replay.LastBudgeted
                                .SelectMany(static message => message.Contents)
                                .OfType<ToolApprovalResponseContent>()
                                .Select(static response => response.RequestId)
                                .ToList();

        AssertEx.Equal(expected: 2, requestIds.Count, "both approval requests must survive the passes that rewrote their message");
        AssertEx.Equal(expected: 2, responseIds.Count);
        foreach (var responseId in responseIds)
        {
            AssertEx.Contains(requestIds, responseId, "a replayed decision must never outlive the request it answers");
        }
    }

    [Test]
    public async Task BudgetedReplay_ReasoningOnlyMessages_AreDroppedWholeRatherThanSentEmpty()
    {
        var replay = await RunReplayAsync();

        AssertEx.Empty(replay.FirstBudgeted.Where(static message => message.Contents.Count == 0),
            "a message emptied by the reasoning strip must be dropped, never kept as a contentless message");
        AssertEx.Empty(replay.ProviderMessages.Where(static message => message.Contents.Count == 0),
            "and no contentless message may reach the provider by any other route either");
        AssertEx.False(replay.FirstBudgeted.SelectMany(static message => message.Contents).OfType<TextReasoningContent>().Any(static reasoning =>
                string.Equals(reasoning.Text, ReasoningOnlyMarker, StringComparison.Ordinal)),
            "the reasoning-only message must be gone from the budgeted round entirely");
        AssertEx.True(replay.FirstMessagesDropped > 0, "dropping it must be reported as a dropped message, not silently");
    }

    [Test]
    public async Task BudgetedReplay_DoubleExcerptedStructuredResult_KeepsItsCallIdAndItsMarkers()
    {
        var replay = await RunReplayAsync();

        var excerpted = replay.ProviderMessages
                              .SelectMany(static message => message.Contents)
                              .OfType<FunctionResultContent>()
                              .Select(static result => (result.CallId, Text: result.Result?.ToString() ?? string.Empty))
                              .Where(static result => result.Text.Contains("[truncated:", StringComparison.Ordinal))
                              .ToList();

        AssertEx.True(excerpted.Count > 0, "the oversized structured result must reach the provider excerpted, not whole");
        foreach (var (callId, text) in excerpted)
        {
            AssertEx.True(!string.IsNullOrEmpty(callId), "excerpting must never blank the call id the validator and the wire match on");
            AssertEx.True(text.Length <= InnerExcerptChars + 64,
                "an outer excerpt that the inner budgeter re-excerpts must end up bounded by the INNER budget, not the outer one");
        }
    }

    [Test]
    public async Task BudgetedReplay_LlamaServerWireBody_CorrelatesEveryToolResultWithACall()
    {
        var replay = await RunReplayAsync();

        var body = await LlamaGrammarToolOffer.CaptureLlamaServerWireBodyAsync(replay.ProviderMessages, BuildLlamaWireOptions());

        AssertWireCorrelation(body);
    }

    [Test]
    public async Task BudgetedReplay_OpenAiShapedWireBody_CorrelatesEveryToolResultWithACall()
    {
        var replay = await RunReplayAsync();

        var body = await LlamaGrammarToolOffer.CaptureWireBodyAsync(replay.ProviderMessages, BuildOpenAiWireOptions());

        AssertWireCorrelation(body);
    }

    /// <summary>
    ///     Every <c>tool_call_id</c> the serialized request carries must name a <c>tool_calls[].id</c> that is also in the
    ///     request. This is the failure a budgeting bug produces in the field: not an exception, but a provider 400 on a
    ///     tool result whose originating call was trimmed out from under it.
    /// </summary>
    private static void AssertWireCorrelation(string body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");

        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var resultIds = new List<string>();
        foreach (var message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    if (toolCall.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                    {
                        _ = callIds.Add(value);
                    }
                }
            }

            if (message.TryGetProperty("tool_call_id", out var resultId) && resultId.GetString() is { Length: > 0 } resultValue)
            {
                resultIds.Add(resultValue);
            }
        }

        AssertEx.True(resultIds.Count > 0, "the replayed round must carry at least one tool result on the wire");
        foreach (var resultId in resultIds)
        {
            AssertEx.Contains(callIds, resultId, "a tool result on the wire must name a tool call that is also on the wire");
        }
    }

    /// <summary>
    ///     Drives the whole chain: seed a history already too long for the window, budget it, run the agent, replay the
    ///     approval decisions the agent surfaces, and repeat until the model gives a final answer. Every round re-budgets
    ///     the grown history exactly as <c>InvocationRunner</c>'s tool loop does.
    /// </summary>
    private static async Task<ReplayOutcome> RunReplayAsync()
    {
        var queryExecutions = 0;
        var archiveExecutions = 0;

        // A structured result, deliberately far past both excerpt budgets, so the double-excerpt path is exercised on a
        // JSON payload rather than on convenient prose.
        var structuredResult = JsonSerializer.Serialize(new
        {
            rows = Enumerable.Range(0, 200).Select(index => new
            {
                id = index,
                label = $"row-{index}"
            })
        });

        var queryTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string query) =>
            {
                queryExecutions++;
                return structuredResult;
            },
            QueryToolName,
            "Runs a structured query and returns its rows as JSON."));
        var archiveTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string batch) =>
            {
                archiveExecutions++;
                return "archived";
            },
            ArchiveToolName,
            "Archives a batch. Side-effecting and irreversible."));

        using var scripted = new ScriptedReplayChatClient();
        using var providerBudgeted = new ProviderCallBudgetChatClient(scripted, NullLogger<ProviderCallBudgetChatClient>.Instance);
        using var chatClient = providerBudgeted.AsBuilder()
                                               .UseFunctionInvocation(NullLoggerFactory.Instance)
                                               .Build();
        var services = new ServiceCollection().BuildServiceProvider();
        var agent = new ApprovalResponseValidatingAgent(new ChatClientAgent(chatClient,
            "budgeted-approval-replay",
            SystemPrompt,
            "Combined budgeter / approval-replay / wire gate.",
            new List<AITool>
            {
                queryTool,
                archiveTool
            },
            NullLoggerFactory.Instance,
            services));

        var budgeter = new ConversationContextBudgeter(new HeuristicTokenEstimator(),
            Options.Create(new ConversationContextBudgetOptions
            {
                RecentTurnKeepCount = 4,
                HistoricalToolResultExcerptChars = OuterExcerptChars,
                ReservedOutputTokenFloor = 0,
                StripProtectedReasoning = true,
                ExcerptProtectedToolResults = true
            }));

        var reasoningStripped = 0;
        var protectedResultsExcerpted = 0;
        var everExceededBudget = false;
        IReadOnlyList<ChatMessage>? firstBudgeted = null;
        IReadOnlyList<ChatMessage>? lastBudgeted = null;
        var firstMessagesDropped = 0;
        var approvalRequestsSurfaced = 0;

        // The inner budget is what makes this a DOUBLE excerpt: a smaller excerpt budget than the outer one, and a window
        // narrow enough that the inner hop has to act on rounds the outer hop already reduced.
        using var budgetScope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            DefaultContextTokens = 2048,
            ReservedOutputTokenFloor = 0,
            RecentMessagesToKeep = 2,
            OversizedToolResultExcerptChars = InnerExcerptChars
        });

        var messages = BuildSeedHistory();
        string? finalText = null;

        // Bounded so a scripting mistake fails as an assertion rather than by hanging the suite.
        for (var round = 0; round < 6; round++)
        {
            var budgeted = budgeter.Budget(messages,
                contextTokenCapacity: OuterContextTokens,
                reservedOutputTokens: 0,
                SystemPrompt,
                [$"{QueryToolName}: runs a structured query", $"{ArchiveToolName}: archives a batch"],
                "test-model");
            everExceededBudget |= budgeted.ExceedsBudget;
            reasoningStripped += budgeted.ReasoningStrippedCount;
            protectedResultsExcerpted += budgeted.ProtectedResultsExcerptedCount;
            messages = [.. budgeted.Messages];

            // Snapshots, not aliases: `messages` is mutated in place below, so keeping the list itself would silently
            // turn both "the round the budgeter produced" assertions into assertions about the final history.
            lastBudgeted = [.. messages];
            if (firstBudgeted is null)
            {
                firstBudgeted = lastBudgeted;
                firstMessagesDropped = budgeted.MessagesDropped;
            }

            var response = await agent
                                 .RunStreamingAsync(messages, session: null, options: null, CancellationToken.None)
                                 .ToAgentResponseAsync(CancellationToken.None);
            messages.AddRange(response.Messages);

            var requests = response.Messages.SelectMany(static message => message.Contents).OfType<ToolApprovalRequestContent>().ToList();
            if (requests.Count == 0)
            {
                finalText = response.Text;
                break;
            }

            approvalRequestsSurfaced += requests.Count;

            // Approve the query, reject the archive: one round carries both verdicts, which is the shape that makes the
            // validator's resolved-response bookkeeping (and the budgeter's atomic group eviction) matter.
            messages.Add(new ChatMessage(ChatRole.User,
                requests.Select(request => (AIContent)request.CreateResponse(
                    !string.Equals(ToolNameOf(request), ArchiveToolName, StringComparison.Ordinal),
                    "Decided by user.")).ToList()));
        }

        return new ReplayOutcome(AssertEx.NotNull(finalText, "the scripted conversation must terminate with a final answer"),
            queryExecutions,
            archiveExecutions,
            reasoningStripped,
            protectedResultsExcerpted,
            everExceededBudget,
            approvalRequestsSurfaced,
            AssertEx.NotNull(firstBudgeted, "the first round must have been budgeted"),
            firstMessagesDropped,
            AssertEx.NotNull(lastBudgeted, "the last round must have been budgeted"),
            AssertEx.NotNull(scripted.LastMessages, "the provider must have been called at least once"));
    }

    private static string ToolNameOf(ToolApprovalRequestContent request)
    {
        return request.ToolCall is FunctionCallContent call ? call.Name : string.Empty;
    }

    /// <summary>
    ///     A history that is already far over the window before the turn starts, and whose every turn falls inside the
    ///     protected recent window — so Passes 1-3 have nothing to reclaim and the round can only be rescued by the two
    ///     last-resort passes. It carries a reasoning-ONLY assistant message (the shape Pass 4 must drop whole rather than
    ///     forward empty), a multi-content one, and a completed tool round whose oversized result sits inside the protected
    ///     window, which is the only thing Pass 5 can take.
    /// </summary>
    private static List<ChatMessage> BuildSeedHistory()
    {
        return
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, "Summarize the earlier batch."),
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent(ReasoningOnlyMarker)]),
            new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent(new string(c: 'q', 4000)),
                new TextContent("The earlier batch is summarized above.")
            ]),
            new ChatMessage(ChatRole.User, "Warm the cache first."),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(WarmCallId, "warm_cache", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent(WarmCallId, new string(c: 'w', 12000))]),
            new ChatMessage(ChatRole.User, "Now query and archive the current batch.")
        ];
    }

    private static ChatOptions BuildLlamaWireOptions()
    {
        // The llama-server lane carries the per-send window marker the deferred client writes onto ChatOptions, so the
        // captured bytes are shaped the way a real local round is.
        return new ChatOptions
        {
            ModelId = "test-model",
            Tools = [.. LlamaGrammarToolOffer.BuildProductionToolOffer()],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["num_ctx"] = 2048
            }
        };
    }

    private static ChatOptions BuildOpenAiWireOptions()
    {
        return new ChatOptions
        {
            ModelId = "test-model",
            Tools =
            [
                LlamaGrammarToolOffer.BuildTool(QueryToolName, """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
                LlamaGrammarToolOffer.BuildTool(ArchiveToolName, """{"type":"object","properties":{"batch":{"type":"string"}},"required":["batch"]}""")
            ]
        };
    }

    private sealed record ReplayOutcome(
        string FinalText,
        int QueryExecutions,
        int ArchiveExecutions,
        int ReasoningStripped,
        int ProtectedResultsExcerpted,
        bool EverExceededBudget,
        int ApprovalRequestsSurfaced,
        IReadOnlyList<ChatMessage> FirstBudgeted,
        int FirstMessagesDropped,
        IReadOnlyList<ChatMessage> LastBudgeted,
        IReadOnlyList<ChatMessage> ProviderMessages);

    /// <summary>
    ///     Scripted stand-in for the model, and the capture point for the message list that would go to the provider — i.e.
    ///     the list AFTER the outer budgeter, function invocation and the inner budgeter have all had their say. Round one
    ///     emits a multi-content assistant message (reasoning plus TWO parallel tool calls); once every call has a result
    ///     it emits the final answer.
    /// </summary>
    private sealed class ScriptedReplayChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
            LastMessages = materialized;

            // Keyed on THIS turn's calls, not on "any result at all": the seed history already carries a completed
            // warm-cache round, so a blanket check would have the model declare victory before it ever called a tool.
            var turnToolsHaveRun = materialized
                                   .SelectMany(static message => message.Contents)
                                   .OfType<FunctionResultContent>()
                                   .Any(static result => string.Equals(result.CallId, QueryCallId, StringComparison.Ordinal)
                                                         || string.Equals(result.CallId, ArchiveCallId, StringComparison.Ordinal));
            if (turnToolsHaveRun)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "all done")));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent(new string(c: 'r', 6000)),
                new TextContent("Calling both tools."),
                new FunctionCallContent(QueryCallId,
                    QueryToolName,
                    new Dictionary<string, object?>
                    {
                        ["query"] = "select *"
                    }),
                new FunctionCallContent(ArchiveCallId,
                    ArchiveToolName,
                    new Dictionary<string, object?>
                    {
                        ["batch"] = "current"
                    })
            ])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return ToUpdates(GetResponseAsync(messages, options, cancellationToken), cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ToUpdates(Task<ChatResponse> responseTask,
            [EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            var response = await responseTask.ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }
    }
}
