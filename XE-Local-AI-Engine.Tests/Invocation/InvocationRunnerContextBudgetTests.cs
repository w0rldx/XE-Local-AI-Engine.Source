namespace XE_Local_AI_Engine.Tests.Invocation;

using System.ClientModel.Primitives;
using System.Collections.Frozen;
using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Invocation.InvocationRunnerTestSupport;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

[Category(TestCategories.Unit)]
public sealed class InvocationRunnerContextBudgetTests
{
    [Test]
    public async Task RunAsync_WhenHistoryStillExceedsBudgetAfterTruncation_FailsCleanlyBeforeAnyProviderCall()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        // A capacity this tiny cannot be satisfied by ANY history (even a single protected turn), so the budgeter's
        // two-pass truncation cannot bring the estimate under budget: ExceedsBudget stays true and the runner must
        // hard-stop BEFORE ever touching the agent factory (no agentUpdates are ever consumed).
        var runner = CreateRunner(eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 1,
                ReservedOutputTokenFloor = 0
            });
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "Conversation exceeds the model's context window even after truncation — Compact the conversation to summarize older messages, start a new chat, or switch to a larger-context model.",
            FailureCategory.ContextWindowExceeded);
    }

    /// <summary>
    ///     At a 4096 window about 19 tool schemas alone overflow, failing every first message with advice to compact a
    ///     conversation that has no history. The offer is narrowed to what fits.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenTheToolOfferAloneOverflowsTheWindow_NarrowsItToFitInsteadOfFailing()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        FittedToolOffer? fittedOffer = null;
        var factory = CreateFactory(CreateUpdates("Jupiter"), onCreate: _ => fittedOffer = ToolRelevanceScope.Current?.FittedOffer);
        var runner = CreateRunner(factory,
            eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 6000,
                ReservedOutputTokenFloor = 0
            });

        var package = WithLargeTools(RuntimePackageBuilder.Valid(), count: 20).Build();
        await RunAsync(runner, package);

        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationFailedAsync(Guid.Empty, string.Empty, FailureCategory.Unexpected);
        var fitted = AssertEx.NotNull(fittedOffer, "the offer is narrowed for this turn");
        AssertEx.Equal(20, fitted.TokenCosts.Count, "every offered tool is costed");
        AssertEx.True(fitted.RankedTokenBudget >= fitted.TokenCosts.Values.Min(), "at least one ranked tool fits");
        AssertEx.True(fitted.RankedTokenBudget < fitted.TokenCosts.Values.Sum(), "not every tool fits, or the offer would not need narrowing");
    }

    /// <summary>Model-matrix V4: at a 4,096 window the real Default Assistant offer runs a turn with ranked tools.</summary>
    /// <remarks>
    ///     The count-based fit refused even its minimum there, so every agent turn failed with the tool message. The
    ///     provider-round budgeter must accept exactly what the relevance hop then sends.
    /// </remarks>
    [Test]
    public async Task RunAsync_WithTheRealDefaultAssistantOfferAtA4096Window_SendsRankedToolsInsideTheProviderBudget()
    {
        const string Model = "qwen3.5:0.8b";
        const string Question = "What is 17 * 23? Use the calculator.";
        var offer = await DefaultAssistantOfferAsync(Model);
        var systemPrompt = string.Concat(await ReadAgentInstructionsAsync("BaseScaffold.txt"), "\n\n", await ReadAgentInstructionsAsync("LocalChatDefault.txt"));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        FittedToolOffer? fittedOffer = null;
        var factory = CreateFactory(CreateUpdates("Jupiter"), onCreate: _ => fittedOffer = ToolRelevanceScope.Current?.FittedOffer);
        var budgetOptions = new ConversationContextBudgetOptions
        {
            DefaultContextTokens = 4096
        };
        var contextBudgeter = new ConversationContextBudgeter(new HeuristicTokenEstimator(), Options.Create(budgetOptions), StubNodeRuntimeSettings.Create().Build());
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, contextBudgetOptions: budgetOptions, contextBudgeter: contextBudgeter);
        var package = RuntimePackageBuilder.Valid().WithSystemPrompt(systemPrompt).WithUserMessage(Question).Build() with
        {
            AllowedTools = [.. offer]
        };

        await RunAsync(runner, package);

        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationFailedAsync(Guid.Empty, string.Empty, FailureCategory.Unexpected);
        var fitted = AssertEx.NotNull(fittedOffer, "the full offer does not fit 4,096, so the turn runs on a fitted one");

        // Live C2: the round after one Calculate call and its result overflowed the window the first round filled. Checked for
        // the costliest selection the hop could make inside the budget, measured as the fit measured it.
        var rankable = fitted.TokenCosts.Keys.ToList();
        var costliest = Enumerable.Range(0, 1 << rankable.Count)
                                  .Select(mask => rankable.Where((_, index) => (mask & (1 << index)) != 0).ToList())
                                  .Where(subset => subset.Sum(name => fitted.TokenCosts[name]) <= fitted.RankedTokenBudget)
                                  .MaxBy(subset => subset.Sum(name => fitted.TokenCosts[name]))!;
        var offered = costliest.Append(AskUserTool.ToolName).ToHashSet(StringComparer.Ordinal);
        List<ChatMessage> toolRoundTrip =
        [
            new(ChatRole.Assistant, [
                new FunctionCallContent("call_0", "Calculate", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["expression"] = "17 * 23"
                })
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call_0", "17 * 23 = 391")])
        ];
        var secondRound = contextBudgeter.Budget([new ChatMessage(ChatRole.User, Question), .. toolRoundTrip],
            4096,
            budgetOptions.ReservedOutputTokenFloor,
            systemPrompt,
            [ListToolsFunction.BudgetDefinition, .. InvocationRunner.BuildToolBudgetDefinitions([.. offer.Where(tool => offered.Contains(tool.Name))])],
            Model);
        // Charged as if the selection used the whole budget: which tools fill it depends on the model's per-tool costs.
        var unusedBudget = fitted.RankedTokenBudget - costliest.Sum(name => fitted.TokenCosts[name]);
        AssertEx.True(secondRound.EstimatedTokensBefore <= secondRound.EffectiveBudgetTokens - unusedBudget,
            $"one tool round-trip fits after a full fitted offer without trimming the turn ({secondRound.EstimatedTokensBefore} > {secondRound.EffectiveBudgetTokens} - {unusedBudget})");

        // The provider call the turn makes: the real offer as executable tools plus list_tools, through the relevance hop
        // and the provider-round budgeter, which throws when the request does not fit the window.
        List<AITool> tools =
            [.. offer.Select(static tool => new MetadataToolFunction(tool.Name, tool.Description, MetadataToolFunction.ParseSchema(tool.ParameterSchema!), static (_, _) => Task.FromResult("ok")))];
        tools.Add(new ListToolsFunction(tools));
        ChatOptions? sent = null;
        var provider = Substitute.For<IChatClient>();
        provider.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Do<ChatOptions?>(options => sent = options), Arg.Any<CancellationToken>())
                .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Jupiter")));
        using var budgeter = new ProviderCallBudgetChatClient(provider, NullLogger<ProviderCallBudgetChatClient>.Instance);
        using var hop = new ToolRelevanceChatClient(budgeter,
            new LexicalToolRelevanceSelector(),
            new ToolRelevanceOptions(),
            NullLogger<ToolRelevanceChatClient>.Instance);
        List<string> sentNames;
        using (ToolRelevanceScope.BeginScope(active: true, FrozenSet<string>.Empty, fitted))
            using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
                   {
                       DefaultContextTokens = 4096
                   }))
            {
                var options = new ChatOptions
                {
                    ModelId = Model,
                    Tools = tools,
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["num_ctx"] = 4096
                    }
                };
                List<ChatMessage> round = [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, Question)];
                _ = await hop.GetResponseAsync(round, options);
                sentNames = [.. AssertEx.NotNull(sent).Tools!.Select(static tool => tool.Name)];

                // The next provider round, after the call and its result, for this query's selection and the costliest one.
                round.AddRange(toolRoundTrip);
                _ = await hop.GetResponseAsync(round, options);
                _ = await budgeter.GetResponseAsync(round, new ChatOptions
                {
                    ModelId = Model,
                    Tools = [.. tools.Where(tool => offered.Contains(tool.Name) || tool is ListToolsFunction)],
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["num_ctx"] = 4096
                    }
                });
            }

        AssertEx.Contains(sentNames, ListToolsFunction.ToolName);
        AssertEx.Contains(sentNames, AskUserTool.ToolName);
        AssertEx.True(sentNames.Count > 2, $"at least one ranked tool beside list_tools and ask_user, sent: {string.Join(", ", sentNames)}");
        AssertEx.True(sentNames.Count < tools.Count, "the offer was narrowed");
    }

    /// <summary>
    ///     The attachment is sized against the costliest offer the hop can send, whatever the rank order: with room 1000 the
    ///     hop can send both 500s, so the largest-first 600 is an underestimate.
    /// </summary>
    [Test]
    [Arguments(new[]
    {
        600,
        500,
        500
    }, 1000, 1000)]
    [Arguments(new[]
    {
        300,
        700,
        250,
        400
    }, 1000, 1000)]
    [Arguments(new[]
    {
        1200,
        900
    }, 1000, 900)]
    [Arguments(new[]
    {
        1200
    }, 1000, 0)]
    public void CostliestWithin_ReturnsTheLargestTotalCostTheHopCanSendWithinTheRoom(int[] costs, int room, int expectedTotal)
    {
        var subset = InvocationRunner.CostliestWithin(costs, room);

        AssertEx.Equal(subset.Count, subset.Distinct().Count(), "each tool counted once");
        AssertEx.Equal(expectedTotal, subset.Sum(index => costs[index]));
    }

    [Test]
    public async Task RunAsync_WhenEvenTheNarrowedToolOfferOverflows_NamesTheToolsInsteadOfAdvisingCompaction()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 1500,
                ReservedOutputTokenFloor = 0
            });

        // Every tool alone is larger than the window: not even list_tools plus one ranked tool fits.
        var package = WithLargeTools(RuntimePackageBuilder.Valid(), count: 20, paddingChars: 8_000).Build();
        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "The tools offered with this message do not fit this model's context window — Turn off tools for this chat, or switch to a larger-context model.",
            FailureCategory.ContextWindowExceeded);
    }

    /// <summary>A pasted message that overflows the window on its own must not be blamed on the tools: turning them off would not help.</summary>
    [Test]
    public async Task RunAsync_WhenTheTurnOverflowsEvenWithNoTools_FailsWithTheGenericMessageNotTheToolOne()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 6000,
                ReservedOutputTokenFloor = 0
            });

        var package = WithLargeTools(RuntimePackageBuilder.Valid().WithUserMessage(new string('x', 40_000)), count: 20).Build();
        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "Conversation exceeds the model's context window even after truncation — Compact the conversation to summarize older messages, start a new chat, or switch to a larger-context model.",
            FailureCategory.ContextWindowExceeded);
    }

    /// <summary>A 30k-character attachment is trimmed at 4k and 8k windows rather than failing the first turn.</summary>
    [Test]
    public async Task RunAsync_WhenTheAttachmentOverflowsTheWindow_ShortensItAndTellsTheUser()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        IReadOnlyList<ChatMessage>? sent = null;
        var factory = CreateFactory(CreateUpdates("summary"), onCreate: definition => sent = definition.ConversationContext);
        var runner = CreateRunner(factory,
            eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 4096,
                ReservedOutputTokenFloor = 0
            });
        var parts = new[]
        {
            new AttachmentTextPart("notes.txt", new string('a', 30_000))
        };
        var package = WithAttachment(RuntimePackageBuilder.Valid().Build(), parts);

        await RunAsync(runner, package);

        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationFailedAsync(Guid.Empty, string.Empty, FailureCategory.Unexpected);
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.AttachmentShortened
            && payload.Message == "The attached file was shortened to fit this model's context window; the model was told it is incomplete."));
        var attachmentText = AssertEx.NotNull(sent).Single(message => message.Text.Contains(ConversationAttachmentContextComposer.Preamble, StringComparison.Ordinal)).Text;
        AssertEx.True(attachmentText.Length < 16_000, $"shortened to the window, was {attachmentText.Length} chars");
        AssertEx.Contains(attachmentText, ConversationAttachmentContextComposer.TruncationNotice);
    }

    [Test]
    public async Task RunAsync_WhenNoShortenedAttachmentFits_NamesTheAttachment()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 800,
                ReservedOutputTokenFloor = 0
            });
        // The file name rides inside the fence, so even the emptiest recomposition outgrows the window while the turn alone fits.
        var package = WithAttachment(RuntimePackageBuilder.Valid().Build(), [new AttachmentTextPart(new string('n', 6_000), new string('a', 30_000))]);

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "The attached file does not fit this model's context window, even shortened — Remove the attachment, or switch to a larger-context model.",
            FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task RunAsync_WhenTheApprovalResumeOverrunsOnReasoningAlone_FailsTheTurnWithoutPass4()
    {
        // The boundary Pass 4 is measured against. On the approval resume the runner re-budgets the FOLDED segment, whose
        // assistant message carries the model's reasoning. Every message of that history is inside the protected recent
        // window, so Passes 1-3 have nothing to reclaim: with the pass off the turn dies before the resumed provider call.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ReasoningHeavyApprovalRequestUpdates(reasoningChars: 100_000) : CreateUpdates("done");
        });
        var runner = CreateRunner(factory,
            eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 4096,
                ReservedOutputTokenFloor = 0,
                StripProtectedReasoning = false
            });
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 1, segment, "the resume segment must never start once the round is rejected as over budget");
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            Arg.Any<string>(),
            FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task RunAsync_WhenTheApprovalResumeOverrunsOnReasoningAlone_CompletesWithPass4AndAnEnrichedNotice()
    {
        // Same turn, same numbers, Pass 4 at its shipped default: the superseded reasoning is reclaimed, the resume runs,
        // and the user is told what happened in a notice that names the reasoning strip rather than a plain history trim.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ReasoningHeavyApprovalRequestUpdates(reasoningChars: 100_000) : CreateUpdates("done");
        });
        var runner = CreateRunner(factory,
            eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                // StripProtectedReasoning is deliberately NOT set: this grades the SHIPPED default, so flipping the pass
                // back off fails here rather than silently reintroducing the failed turn above.
                DefaultContextTokens = 4096,
                ReservedOutputTokenFloor = 0
            });
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the approved resume must run instead of the turn failing");
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId,
            Arg.Any<string>(),
            FailureCategory.ContextWindowExceeded);
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.HistoryTruncated
            && payload.Message.Contains("reasoning removed from 1 message(s)", StringComparison.Ordinal)
            && !payload.Message.Contains("The originals are kept", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_WhenTheSystemPromptTakesMostOfTheWindow_CountsItOnceAcrossBothBudgetStages()
    {
        // Live 2048/512: the tool-loop stage budgets the seed WITH the prompt as its System message and also charged it as overhead.
        // ~1100 prompt tokens fit a 2125-token budget once, not twice.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                ReservedOutputTokenFloor = 0
            });
        var package = RuntimePackageBuilder.Valid()
                                           .WithSystemPrompt(new string('p', 4384))
                                           .WithSamplingOptions(new SamplingOptions
                                           {
                                               NumCtx = 2500
                                           })
                                           .Build();

        await RunAsync(runner, package);

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId,
            Arg.Any<string>(),
            FailureCategory.ContextWindowExceeded);
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.HistoryTruncated));
    }

    [Test]
    public async Task BudgetFirstRound_MeasuresTheSameRequestTheToolLoopStageMeasuresForTheSameBenchmarkPackage()
    {
        // The benchmark freeze refuses with BudgetFirstRound; the run fails on the tool-loop stage's hard stop. Built from the same
        // package, the two must agree on every number, or the freeze admits a run the runtime refuses (the live 2048/512 case).
        var budgetOptions = new ConversationContextBudgetOptions();
        var budgeter = new RecordingContextBudgeter(new ConversationContextBudgeter(new HeuristicTokenEstimator(), Options.Create(budgetOptions), StubNodeRuntimeSettings.Create().Build()));
        AllowedToolDto[] tools =
        [
            new()
            {
                Id = Guid.NewGuid(),
                Name = "calculate",
                Location = ToolLocation.ClientLocal,
                Description = "Evaluates an arithmetic expression.",
                ParameterSchema = """{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}"""
            }
        ];
        var package = BenchmarkRunExecutor.BuildPrimaryPackage(new LocalChatRuntimePackageBuilder(),
            new ResolvedAgentRuntime(new string('p', 1900), tools, ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1),
            "What is 2+2? Answer with the number only.",
            "bench-model",
            BenchmarkFrozenPolicies.DeterministicSampling(maxOutputTokens: 512),
            requestedContextTokens: 8192,
            invocationTimeoutSeconds: 600);
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo => Task.FromResult(new InvocationAgentContext
               {
                   Agent = new FakeAIAgent(_ => CreateUpdates("4"), onSessionObserved: null),
                   Session = null,
                   SeedMessages = InvocationAgentFactory.BuildSeedMessages(callInfo.Arg<InvocationAgentDefinition>())
               }));

        await RunAsync(CreateRunner(factory, contextBudgeter: budgeter, contextBudgetOptions: budgetOptions), package);
        var preflight = InvocationRunner.BudgetFirstRound(budgeter.Inner, package, budgetOptions, StoredNodeSettings.DefaultDefaultContextTokens, "bench-model");

        AssertEx.Equal(expected: 2, budgeter.Results.Count, "one initial-assembly and one tool-loop budget");
        var toolLoop = budgeter.Results[1];
        AssertEx.Equal(toolLoop.EstimatedTokensBefore, preflight.EstimatedTokensBefore);
        AssertEx.Equal(toolLoop.FixedOverheadTokens, preflight.FixedOverheadTokens);
        AssertEx.Equal(toolLoop.EffectiveBudgetTokens, preflight.EffectiveBudgetTokens);
        AssertEx.Equal(toolLoop.ExceedsBudget, preflight.ExceedsBudget);
        AssertEx.Equal(budgeter.Results[0].EstimatedTokensBefore + budgeter.Results[0].FixedOverheadTokens, preflight.EstimatedTokensBefore + preflight.FixedOverheadTokens,
            "the initial assembly measures the same request, with the prompt as overhead instead of history");
    }

    [Test]
    public async Task RunAsync_WhenLaunchedWindowExceedsTheConfiguredDefault_BudgetsAgainstTheRealWindow()
    {
        // The regression: the launched window was only ever allowed to SHRINK the configured default, so a model
        // running a 64k window was budgeted at the 8k default and long conversations failed before any provider call.
        // A default of 1 token cannot admit even a single protected turn, so this run can only survive if the effective
        // window replaced it.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: 65536),
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 1,
                ReservedOutputTokenFloor = 0
            });
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId,
            Arg.Any<string>(),
            FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task RunAsync_WhenLaunchedWindowIsBelowTheConfiguredDefault_StillClampsDown()
    {
        // The down-tier direction the original Math.Min was right about: a model launched below the configured default
        // must be budgeted at the smaller REAL window, so an over-large default cannot push an over-budget send.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: 1),
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 131072,
                ReservedOutputTokenFloor = 0
            });
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            Arg.Any<string>(),
            FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task RunAsync_WhenPerSendNumCtxIsSmallerThanTheLaunchedWindow_HonoursTheOverride()
    {
        // The explicit per-send bound is the user's ask: a roomy launched window must not silently widen it back.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: 65536),
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 131072,
                ReservedOutputTokenFloor = 0
            });
        var package = RuntimePackageBuilder.Valid()
                                           .WithSamplingOptions(new SamplingOptions
                                           {
                                               NumCtx = 1
                                           })
                                           .Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            Arg.Any<string>(),
            FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task RunAsync_WhenGenerationAdmissionRejectsUnknownEffectiveContext_DoesNotCallProviderGeneration()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var runner = CreateRunner(CreateGenerationSpyFactory(() => generationCalls++),
            eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: null));
        var package = RuntimePackageBuilder.Valid()
                                           .WithSamplingOptions(new SamplingOptions
                                           {
                                               NumCtx = 8192
                                           })
                                           .Build();

        await RunAsync(runner, package, generationAdmissionPolicy: policy);

        AssertEx.Equal(expected: 0, generationCalls);
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            MinimumEffectiveContextAdmissionPolicy.EffectiveContextUnavailableMessage,
            FailureCategory.AgentRuntime);
        await dispatcher.DidNotReceive().ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenGenerationAdmissionRejectsUndersizedEffectiveContext_DoesNotCallProviderGeneration()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var runner = CreateRunner(CreateGenerationSpyFactory(() => generationCalls++),
            eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: 4096));
        var package = RuntimePackageBuilder.Valid()
                                           .WithSamplingOptions(new SamplingOptions
                                           {
                                               NumCtx = 8192
                                           })
                                           .Build();

        await RunAsync(runner, package, generationAdmissionPolicy: policy);

        AssertEx.Equal(expected: 0, generationCalls);
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "Requested context 8192 tokens exceeds effective context 4096 tokens.",
            FailureCategory.AgentRuntime);
        var admissionContext = AssertEx.NotNull(policy.LastContext);
        AssertEx.Equal(expected: 8192, admissionContext.RequestedContextTokens);
        AssertEx.Equal(expected: 4096, admissionContext.EffectiveContextTokens);
    }

    [Test]
    public async Task RunAsync_WhenGenerationAdmissionAllowsEffectiveContext_CallsProviderGenerationOnce()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var runner = CreateRunner(CreateGenerationSpyFactory(() => generationCalls++),
            eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: 16384));
        var package = RuntimePackageBuilder.Valid()
                                           .WithSamplingOptions(new SamplingOptions
                                           {
                                               NumCtx = 8192
                                           })
                                           .Build();

        await RunAsync(runner, package, generationAdmissionPolicy: policy);

        AssertEx.Equal(expected: 1, generationCalls);
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
        var admissionContext = AssertEx.NotNull(policy.LastContext);
        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, admissionContext.ProviderName);
        AssertEx.Equal(expected: 16384, admissionContext.EffectiveContextTokens);
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenWarmFailsBeforeGenerationAdmission_PreservesProviderFailureAndDoesNotGenerate()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var warmFailure = new HttpRequestException("provider leaked /private/model/path", inner: null, HttpStatusCode.InternalServerError);
        var runner = CreateRunner(CreateGenerationSpyFactory(() => generationCalls++),
            eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: null, warmFailure));
        var package = RuntimePackageBuilder.Valid()
                                           .WithSamplingOptions(new SamplingOptions
                                           {
                                               NumCtx = 8192
                                           })
                                           .Build();

        await RunAsync(runner, package, generationAdmissionPolicy: policy);

        AssertEx.Equal(expected: 0, generationCalls);
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
        AssertEx.Null(policy.LastContext);
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "The model could not be loaded or run on the provider.",
            FailureCategory.ModelLoadFailed);
    }

    [Test]
    public async Task RunAsync_WhenGenerationAdmissionReturnsHostileReason_SurfacesOnlyFixedPolicyMessage()
    {
        const string hostileReason = "../../private/model.gguf\r\nsecret-token";
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var runner = CreateRunner(CreateGenerationSpyFactory(() => generationCalls++),
            eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens: 16384));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner,
            package,
            generationAdmissionPolicy: new RejectingAdmissionPolicy(hostileReason));

        AssertEx.Equal(expected: 0, generationCalls);
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "Invocation generation was rejected by policy.",
            FailureCategory.AgentRuntime);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId,
            Arg.Is<string>(message => message.Contains("private", StringComparison.Ordinal)
                                      || message.Contains("secret-token", StringComparison.Ordinal)),
            Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenProviderRoundIrreduciblyExceedsWindow_ClassifiesContextWindowExceeded()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        // The provider-boundary budgeter rejects a single irreducible over-window round with this typed exception; the
        // runner must classify it as ContextWindowExceeded and surface its fixed, path-free message verbatim (the bounded
        // token/window diagnostics it also carries are never surfaced).
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new ProviderContextWindowExceededException(estimatedTokens: 9000, windowTokens: 4096)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == ProviderContextWindowExceededException.RoundExceedsWindowMessage),
            FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task RunAsync_WhenStoredHistoryHoldsAnOversizedMessage_StillRunsTheTurn()
    {
        // The poisoned-conversation case: a per-turn re-validation that hard-fails on ANY over-cap message in the
        // assembled context also fails on one already persisted, and every later turn re-validates the same stored
        // row and fails the same way, so the user could only abandon the conversation. The cap belongs
        // to the entry seams; oversized history is the budgeter's problem.
        var securityOptions = Options.Create(new SecurityOptions
        {
            MaxMessageSizeKb = 1,
            AllowedModelNamePattern = "^[a-zA-Z0-9._:-]+$"
        });
        var validator = new RuntimePackageValidator(new ModelNameValidator(securityOptions), securityOptions);

        var package = RuntimePackageBuilder.Valid().Build() with
        {
            ConversationContext =
            [
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = new string(c: 'h', count: 2048),
                    SortOrder = 0
                },
                new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = "and what about this?",
                    SortOrder = 1
                }
            ]
        };

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(validator: validator, eventDispatcher: dispatcher);

        await RunAsync(runner, package);

        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
        await dispatcher.ReceivedWithAnyArgs(1).ReportInvocationCompletedAsync(Arg.Any<Guid>());
    }
}
