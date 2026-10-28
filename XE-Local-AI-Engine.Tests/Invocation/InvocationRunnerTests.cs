namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Invocation.InvocationRunnerTestSupport;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

[Category(TestCategories.Unit)]
public sealed class InvocationRunnerTests
{
    [Test]
    public async Task RunAsync_ValidPackage_ReportsChunksAndCompletionToDispatcher()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates("Hello", " world"));

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, " world");
        // The runner now stamps a wall-clock generation duration on the completion report; match it with Arg.Any so the
        // non-deterministic elapsed value does not fail the call assertion (the rest of the args stay null-checked).
        await dispatcher.Received(1)
                        .ReportInvocationCompletedAsync(package.InvocationId, Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null),
                            Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null), Arg.Any<long?>(),
                            Arg.Any<string?>(), Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_KnowledgeSearchOffered_RequestsOneKnowledgeWarm()
    {
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, knowledgeModelPrewarmer: prewarmer);
        var package = WithOfferedTools(RuntimePackageBuilder.Valid().Build(), "search_knowledge_base", "read_document", "read_surrounding_chunks");

        await RunAsync(runner, package);

        // One warm per turn, not one per offered knowledge tool; the completion proves the turn actually ran.
        prewarmer.Received(1).RequestWarm();
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<InvocationThroughput?>());
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task RunAsync_KnowledgeSearchNotOfferedOrToolsDisabled_RequestsNoKnowledgeWarm(bool offerSearch, bool toolsEnabled)
    {
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, knowledgeModelPrewarmer: prewarmer, knowledgeToolsEnabled: toolsEnabled);
        var package = offerSearch
            ? WithOfferedTools(RuntimePackageBuilder.Valid().Build(), "search_knowledge_base")
            : WithOfferedTools(RuntimePackageBuilder.Valid().Build(), "read_document");

        await RunAsync(runner, package);

        prewarmer.DidNotReceive().RequestWarm();
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WithThinkingAndTextChunks_ReportsBothToDispatcher()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: CreateMixedUpdates((Text: "Hello", Thinking: "Let me think..."), (Text: " world", Thinking: " more thought")));

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationThinkingChunkAsync(package.InvocationId, "Let me think...");
        await dispatcher.Received(1).ReportInvocationThinkingChunkAsync(package.InvocationId, " more thought");
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, " world");
        // Match the new wall-clock duration arg with Arg.Any (non-deterministic); the token args remain null-checked.
        await dispatcher.Received(1)
                        .ReportInvocationCompletedAsync(package.InvocationId, Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null),
                            Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null), Arg.Any<long?>(),
                            Arg.Any<string?>(), Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenPlainContextReceivesUsageContent_SendsAuthoritativeTokenCounts()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: null),
            (Text: " world", Usage: new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 2,
                TotalTokenCount = 12
            })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        // The authoritative token counts are asserted exactly; the new wall-clock duration arg is matched with Arg.Any
        // because the elapsed value is non-deterministic.
        await dispatcher.Received(1)
                        .ReportInvocationCompletedAsync(package.InvocationId, Arg.Is<int?>(10), Arg.Is<int?>(2), Arg.Is<int?>(12), Arg.Is<int?>(static value => value == null),
                            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<InvocationThroughput?>());
    }

    [Test]
    [NotInParallel]
    public async Task RunAsync_WhenUsageFinalized_EmitsModelTokenUsageCounterByDirection()
    {
        // The terminal usage-finalize must publish the cumulative model-token counter once per direction on the
        // shared "XE.Node" meter, tagged provider/model/direction only (content-free). Capture through a real
        // MeterListener — the same surface the exporter attaches — so a wrong meter, dropped tag, or double-count is
        // caught. [NotInParallel] keeps a sibling turn's emission out of the capture window.
        var captured = new ConcurrentBag<(long Value, string? Provider, string? Model, string? Direction)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "XE.Node", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "model_token_usage_total", StringComparison.Ordinal))
            {
                activeListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? provider = null;
            string? model = null;
            string? direction = null;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "provider":
                        provider = tag.Value as string;
                        break;
                    case "model":
                        model = tag.Value as string;
                        break;
                    case "direction":
                        direction = tag.Value as string;
                        break;
                    default:
                        break;
                }
            }

            captured.Add((measurement, provider, model, direction));
        });
        listener.Start();

        var runner = CreateRunner(agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 2,
            TotalTokenCount = 12
        })));

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().Build());

        listener.Dispose();

        var input = captured.Where(static measurement => measurement.Direction == "input").ToArray();
        var output = captured.Where(static measurement => measurement.Direction == "output").ToArray();

        // Exactly one increment per direction (finalized once, not per tool-loop round) with the authoritative counts.
        AssertEx.Equal(expected: 1, input.Length);
        AssertEx.Equal(expected: 10L, input[0].Value);
        AssertEx.Equal(expected: 1, output.Length);
        AssertEx.Equal(expected: 2L, output[0].Value);

        // Bounded, content-free tags: the coarse provider dimension (remote, as the harness warms no local provider) and
        // a model id — never any prompt/completion text.
        AssertEx.Equal("remote", input[0].Provider);
        AssertEx.True(!string.IsNullOrEmpty(input[0].Model), "The usage counter must carry a model tag.");
    }

    [Test]
    [NotInParallel]
    public async Task RunAsync_WhenTurnCompletes_EmitsOneTerminalHarnessEfficiencyRecord()
    {
        using var capture = new HarnessMetricCapture();
        var runner = CreateRunner(agentUpdates: CreateUpdates("Hello", " world"));

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().Build());

        var (value, provider, outcome, orchestration) = capture.Terminals.Single();
        AssertEx.Equal(expected: 1L, value);
        AssertEx.Equal("remote", provider);
        AssertEx.Equal("completed", outcome);
        AssertEx.Equal(expected: false, orchestration);
    }

    [Test]
    [NotInParallel]
    public async Task RunAsync_WhenTurnFails_EmitsOneFailedHarnessEfficiencyRecord()
    {
        using var capture = new HarnessMetricCapture();
        var runner = CreateRunner(agentUpdates: ThrowingUpdates());

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().Build());

        var terminal = capture.Terminals.Single();
        AssertEx.Equal(expected: 1L, terminal.Value);
        AssertEx.Equal("failed", terminal.Outcome);
        AssertEx.Equal(expected: false, terminal.Orchestration);
    }

    [Test]
    [NotInParallel]
    public async Task RunAsync_WhenOrchestrationChangesParticipant_RecordsOneHandoffAndOneTerminal()
    {
        using var capture = new HarnessMetricCapture();
        var orchestrationFactory = CreateOrchestrationFactory(OrchestrationParticipantTransitionUpdates(), out _);
        var runner = CreateRunner(orchestrationAgentFactory: orchestrationFactory);

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build());

        var terminal = capture.Terminals.Single();
        AssertEx.Equal("completed", terminal.Outcome);
        AssertEx.Equal(expected: true, terminal.Orchestration);
        AssertEx.Equal(expected: 1L, capture.Handoffs.Single());
    }

    [Test]
    public async Task RunAsync_WhenSeveralProviderRoundsReportUsage_SendsTheLastRoundAndReportsTheSummedTurnTotals()
    {
        // A tool-calling turn is several llama-server requests inside ONE RunStreamingAsync (FunctionInvokingChatClient
        // runs that loop internally), and each round reports its own usage. The two consumers want different numbers.
        // The completion payload and the completed report become the assistant MESSAGE's tokens, which the chat meter
        // reads as context OCCUPANCY: a round's prompt is the whole conversation so far, so the last round already
        // contains every earlier one and summing showed 10,722 for a context that never held more than ~3,000. The
        // turn's COST is the sum, and it rides the terminal-telemetry report onto the run-envelope row instead.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: CreateUpdatesWithUsage((Text: "round one", Usage: new UsageDetails
                {
                    InputTokenCount = 1_000,
                    OutputTokenCount = 10,
                    ReasoningTokenCount = 4,
                    TotalTokenCount = 1_014
                }),
                (Text: "round two", Usage: new UsageDetails
                {
                    InputTokenCount = 2_000,
                    OutputTokenCount = 20,
                    ReasoningTokenCount = 6,
                    TotalTokenCount = 2_026
                }),
                (Text: "round three", Usage: new UsageDetails
                {
                    InputTokenCount = 3_000,
                    OutputTokenCount = 30,
                    ReasoningTokenCount = 8,
                    TotalTokenCount = 3_038
                })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        await dispatcher.Received(1)
                        .ReportInvocationCompletedAsync(package.InvocationId,
                            Arg.Is<int?>(3_000),
                            Arg.Is<int?>(30),
                            Arg.Is<int?>(3_038),
                            Arg.Is<int?>(8),
                            Arg.Any<long?>(),
                            Arg.Any<string?>(),
                            Arg.Any<InvocationThroughput?>());
        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId,
                            Arg.Any<long?>(),
                            Arg.Is<TurnUsageTotals?>(static usage => usage != null
                                                                     && usage.InputTokens == 6_000
                                                                     && usage.OutputTokens == 60
                                                                     && usage.TotalTokens == 6_078
                                                                     && usage.ReasoningTokens == 18));
    }

    // The four tests below pin UsageSnapshot.From()'s SINGLE-ROUND arithmetic — int32 saturation and the
    // reasoning-double-counting-avoidance in the derived total. They read the numbers off the dispatcher report that
    // persists them onto the assistant message, not the hub completion payload, which is only an observation point
    // for the same numbers. The sibling
    // RunAsync_WhenSummedUsageExceedsInt32_SaturatesInsteadOfWrapping covers the cross-round Accumulate sum, which is
    // a different code path and does not subsume any of these.
    [Test]
    public async Task RunAsync_WhenUsageTokenCountExceedsInt32_SaturatesInsteadOfFaulting()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            // A provider reporting a count past int.MaxValue must clamp, not throw mid-stream and fail the invocation.
            InputTokenCount = (long)int.MaxValue + 100,
            OutputTokenCount = 5,
            TotalTokenCount = (long)int.MaxValue + 105
        })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            int.MaxValue,
            5,
            int.MaxValue,
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenTheDerivedTotalExceedsInt32_SaturatesInsteadOfFaulting()
    {
        // The provider reported no total of its own, so the total is DERIVED from input + output. Summing them as
        // checked ints threw OverflowException mid-stream and failed the invocation; the derivation has to clamp
        // exactly as the single-round and accumulated paths already do.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            InputTokenCount = int.MaxValue,
            OutputTokenCount = 1
        })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            int.MaxValue,
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenProviderReportsNoTotal_DerivesTheTotalWithoutDoubleCountingReasoning()
    {
        // Microsoft.Extensions.AI documents ReasoningTokenCount as counted INSIDE OutputTokenCount, so a derived total
        // of input + output + reasoning charged every reasoning token twice whenever the provider omitted its own
        // total. 100 + 40 is the turn, not 100 + 40 + 30.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 40,
            ReasoningTokenCount = 30
        })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            140,
            30,
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenProviderReportsOnlyReasoningTokens_LeavesTheTotalUnreported()
    {
        // Null-preserving: with neither input nor output reported there is nothing to derive a total from, and a
        // reasoning-only count is not a turn total.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            ReasoningTokenCount = 30
        })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        // A turn with no input or output count has no derivable total.
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            null,
            30,
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenSummedUsageExceedsInt32_SaturatesInsteadOfWrapping()
    {
        // Each round is comfortably inside int range; their SUM is not. The accumulator must clamp exactly as a single
        // oversized round does, rather than wrap negative and report a turn that consumed less than nothing. Asserted on
        // the turn totals, because they are the only place the rounds are added together at all.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: CreateUpdatesWithUsage((Text: "first", Usage: new UsageDetails
                {
                    InputTokenCount = int.MaxValue - 10,
                    OutputTokenCount = 5,
                    TotalTokenCount = int.MaxValue - 5
                }),
                (Text: "second", Usage: new UsageDetails
                {
                    InputTokenCount = 100,
                    OutputTokenCount = 5,
                    TotalTokenCount = 105
                })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId,
                            Arg.Any<long?>(),
                            Arg.Is<TurnUsageTotals?>(static usage => usage != null
                                                                     && usage.InputTokens == int.MaxValue
                                                                     && usage.OutputTokens == 10
                                                                     && usage.TotalTokens == int.MaxValue));
    }

    [Test]
    public async Task RunAsync_ValidationFails_ThrowsInvalidOperationException()
    {
        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>(), Arg.Any<bool>()).Returns(new RuntimePackageValidationResult(isValid: false, ["bad package"]));

        var runner = CreateRunner(validator: validator);
        var package = RuntimePackageBuilder.Valid().Build();

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner, package));

        AssertEx.Contains(exception.Message, "bad package");
    }

    [Test]
    public async Task RunAsync_NeverEnforcesTheMessageSizeCap()
    {
        // Pins the wiring the healing above depends on: the cap is enforced at the inbound seams (the chat hub and the
        // encrypted-envelope assembler), never on the node's own re-assembled turn context.
        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>(), Arg.Any<bool>()).Returns(RuntimePackageValidationResult.Success);

        var runner = CreateRunner(validator: validator);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        validator.Received(1).Validate(Arg.Any<RuntimePackage>(), enforceMessageSizeCap: false);
    }

    [Test]
    public async Task RunAsync_AgentRuntimeThrows_SendsInvocationFailed()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NotSupportedException("factory failed")));

        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            Arg.Is<string>(message => message.Contains("Agent runtime error", StringComparison.Ordinal)),
            FailureCategory.AgentRuntime);
    }

    [Test]
    public async Task RunAsync_ApprovalExpiredThrows_ClassifiesTimeoutNamingTheTool()
    {
        // An expired approval must not collapse into the generic "The operation timed out.": the operator needs to know
        // WHICH approval ran out, and the message is our own fixed shape carrying only the tool name.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new ApprovalExpiredException("run_in_agent_home")));

        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            "The approval for tool 'run_in_agent_home' expired before anyone answered it.",
            FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_NoChatModelInstalledThrows_ClassifiesModelNotInstalled()
    {
        // MapFailure must classify NoChatModelInstalledException as ModelNotInstalled with the actionable, path-free
        // constant — NOT the generic Unexpected/ProviderUnreachable — so a local-default send with no installed GGUF
        // surfaces a "pull a model" CTA instead of a dead-end provider error.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NoChatModelInstalledException()));

        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            Arg.Is<string>(message => message.Contains("No chat model installed", StringComparison.Ordinal)),
            FailureCategory.ModelNotInstalled);
    }

    [Test]
    public async Task RunAsync_RespectsCancellationToken_StopsStreaming()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: BlockingUpdates(gate.Task, started));
        var package = RuntimePackageBuilder.Valid().Build();
        using var cancellationTokenSource = new CancellationTokenSource();

        var runTask = RunAsync(runner, package, cancellationToken: cancellationTokenSource.Token);
        await started.Task;
        await cancellationTokenSource.CancelAsync();
        gate.TrySetCanceled();
        await runTask;

        // The CALLER's token cancelled — host shutdown / a disconnecting caller, not the invocation watchdog — so the
        // turn is Cancelled, matching the category the callers themselves report for the same event (see
        // NodeChatStreamService's OperationCanceledException handler). Timeout is reserved for the invocation
        // CancelAfter watchdog; see RunAsync_WhenInvocationTimeoutElapses_MapsTimeoutFailureCategory.
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_MapsInvocationDefinitionSystemPromptAndSortOrder()
    {
        InvocationAgentDefinition? capturedDefinition = null;
        var factory = CreateFactory(CreateUpdates("ok"), definition => capturedDefinition = definition);

        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("late")
                                           .WithConversationMessage(MessageRole.Assistant, "middle", sortOrder: 1)
                                           .WithConversationMessage(MessageRole.User, "early", sortOrder: -1)
                                           .Build();

        var runner = CreateRunner(factory);
        await RunAsync(runner, package);

        var definition = AssertEx.NotNull(capturedDefinition);
        AssertEx.Equal("You are helpful.", definition.Instructions);
        AssertEx.Equal("early", definition.ConversationContext[0].Text);
        AssertEx.Equal("late", definition.ConversationContext[1].Text);
        AssertEx.Equal("middle", definition.ConversationContext[2].Text);
        AssertEx.Empty(definition.Tools);
    }

    [Test]
    public async Task RunAsync_WhenSystemPromptIsExplicitlyOmitted_ProjectsOnlyConversationMessagesToTheFactory()
    {
        InvocationAgentDefinition? capturedDefinition = null;
        var factory = CreateFactory(CreateUpdates("ok"), definition => capturedDefinition = definition);
        var package = RuntimePackageBuilder.Valid().WithoutSystemPrompt().WithUserMessage("Run the node.").Build();

        await RunAsync(CreateRunner(factory), package);

        var definition = AssertEx.NotNull(capturedDefinition);
        AssertEx.True(definition.OmitSystemPrompt);
        AssertEx.Equal(string.Empty, definition.Instructions);
        AssertEx.Equal(expected: 1, definition.ConversationContext.Count);
        AssertEx.Equal(ChatRole.User, definition.ConversationContext[0].Role);
        AssertEx.Equal("Run the node.", definition.ConversationContext[0].Text);
    }

    [Test]
    public async Task RunAsync_WhenNodeManagedLlamaIsRequired_ScopesTheExactModelForTheWholeAgentRun()
    {
        string? modelDuringFactory = null;
        string? modelDuringStream = null;
        var factory = CreateFactory(_ => ObserveScope(), definition => modelDuringFactory = NodeManagedLlamaRoutingScope.CurrentModel);
        var package = RuntimePackageBuilder.Valid().WithModel("gguf-model").RequiringNodeManagedLlama().Build();
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync("gguf-model", Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));

        await RunAsync(CreateRunner(factory, providerResolver: providerResolver), package);

        AssertEx.Equal("gguf-model", modelDuringFactory);
        AssertEx.Equal("gguf-model", modelDuringStream);
        AssertEx.Null(NodeManagedLlamaRoutingScope.CurrentModel);

        async IAsyncEnumerable<AgentResponseUpdate> ObserveScope()
        {
            modelDuringStream = NodeManagedLlamaRoutingScope.CurrentModel;
            yield return new AgentResponseUpdate
            {
                Contents = [new TextContent("ok")]
            };
            await Task.CompletedTask;
        }
    }

    [Test]
    public async Task RunAsync_WhenConversationMessageCarriesImages_EmitsDataContentIntoTheAgentContext()
    {
        // A vision turn: a ConversationMessageDto carrying image parts must map to an MEAI DataContent alongside its
        // text, so the model actually receives the image. Proves BuildChatMessages emits the image part.
        InvocationAgentDefinition? capturedDefinition = null;
        var factory = CreateFactory(CreateUpdates("ok"), definition => capturedDefinition = definition);

        var imageBytes = new byte[]
        {
            0x89,
            0x50,
            0x4E,
            0x47,
            0x01,
            0x02
        };
        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("describe this image")
                                           .WithImageMessage("look:", "image/png", imageBytes, sortOrder: 1)
                                           .Build();

        var runner = CreateRunner(factory);
        await RunAsync(runner, package);

        var definition = AssertEx.NotNull(capturedDefinition);
        var dataContent = definition.ConversationContext
                                    .SelectMany(message => message.Contents)
                                    .OfType<DataContent>()
                                    .Single();
        AssertEx.Equal("image/png", dataContent.MediaType);
        AssertEx.True(dataContent.Data.Span.SequenceEqual(imageBytes),
            "The image bytes must ride the agent context as a DataContent part.");
    }

    [Test]
    public async Task RunAsync_PassesNullSessionToWorkerAgent()
    {
        var lastObservedSessionWasNull = false;
        var runner = CreateRunner(CreateFactory(CreateUpdates("ok"), onSessionObserved: value => lastObservedSessionWasNull = value));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.True(lastObservedSessionWasNull);
    }

    [Test]
    public async Task RunAsync_WhenTheSameInvocationIsAlreadyActive_ThrowsInvalidOperationException()
    {
        // The dispatcher's slot, not the runner, serializes turns; the runner refuses only a duplicate invocation id.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(agentUpdates: BlockingUpdates(gate.Task, started));
        var invocationId = Guid.NewGuid();

        var firstTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).Build());
        await started.Task;

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).Build()));
        AssertEx.Contains(exception.Message, "already registered");

        gate.SetResult();
        await firstTask;
    }

    [Test]
    public async Task DrainActiveInvocationsAsync_WhenActiveInvocationCompletes_ReturnsTrue()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(agentUpdates: BlockingUpdates(gate.Task, started));

        var runTask = RunAsync(runner, package);
        await started.Task;
        AssertEx.Equal(expected: 1, runner.ActiveInvocationCount);

        var drainTask = runner.DrainActiveInvocationsAsync(TimeSpan.FromSeconds(2));
        AssertEx.False(drainTask.IsCompleted);

        gate.SetResult();

        AssertEx.True(await drainTask);
        await runTask;
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
    }

    [Test]
    public async Task DrainActiveInvocationsAsync_WhenTimeoutElapses_ReturnsFalseWithoutCancellingInvocation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(agentUpdates: BlockingUpdates(gate.Task, started));

        var runTask = RunAsync(runner, package);
        await started.Task;

        // real-timer: the drain deadline is the subject's own input; the invocation is parked on a gate, so it expires.
        var drained = await runner.DrainActiveInvocationsAsync(TimeSpan.FromMilliseconds(10));

        AssertEx.False(drained);
        AssertEx.False(runTask.IsCompleted);
        AssertEx.Equal(expected: 1, runner.ActiveInvocationCount);

        gate.SetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
    }

    [Test]
    public async Task RunAsync_WhenTheProviderBudgetReadObservesCancellation_LeavesNoActiveInvocationRegistered()
    {
        var runner = CreateRunner(configureRuntimeSettings: static settings =>
            settings.GetProviderBudgetMaxCumulativeInputTokensAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<int>>(static _ => throw new OperationCanceledException()));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => RunAsync(runner, RuntimePackageBuilder.Valid().Build()));

        // A registration left behind here is never cleared and the shutdown drain waits on it.
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
    }

    [Test]
    public async Task RunAsync_WhenInvocationTimeoutElapses_MapsTimeoutFailureCategory()
    {
        // Use a normal (non-zero) invocation timeout so the token is NOT already cancelled when the
        // runner starts: that guarantees the agent is enumerated and signals `started`. WithTimeout(0)
        // raced the timeout against reaching enumeration — under load the token cancelled before the
        // agent began streaming, so `started` never fired and `await started.Task` hung the whole run.
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        // Fire the invocation timeout deterministically now that the agent is streaming. The invocation source is
        // cancelled with no user cancel and a live caller token — the exact observable state the real CancelAfter
        // watchdog leaves behind — so the failure must map to Timeout.
        await AssertEx.NotNull(GetActiveInvocationCancellationTokenSource(runner)).CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenInvocationTimeoutElapses_ReportsTheNodeMessageRequestTimeoutAsTheReason()
    {
        // The failure-reason breadcrumb: FailureCategory.Timeout alone cannot say WHICH bound fired (the invocation
        // ceiling, the stream-idle watchdog and an HTTP timeout all map to it), so the persisted message must name the
        // node's maximum message request timeout and its configured seconds. Pinned against the shared, unattributable
        // "Invocation timed out or was cancelled" this replaced.
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 900).Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)),
            eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        await AssertEx.NotNull(GetActiveInvocationCancellationTokenSource(runner)).CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await dispatcher.Received(1)
                        .ReportInvocationFailedAsync(package.InvocationId,
                            "Timed out: the response exceeded the node maximum message request timeout (900s).",
                            FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenTheProviderTimesOutWithNoTokenCancelled_ReportsATimeoutNotAnExternalStop()
    {
        // A provider-side HTTP timeout arrives as a TaskCanceledException whose token is NOT the runner's or the
        // caller's. It must not fall into the origin fallback and persist "Stopped externally (node shutdown or
        // client disconnect)" under the Cancelled category — blaming a disconnect that never happened and hiding a real
        // timeout. Nothing of ours is cancelled in this test, which is exactly the state that must map to Timeout.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var runner = CreateRunner(CreateFactory(ProviderTimeoutUpdates()), eventDispatcher: dispatcher);

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenAToolApprovalExpires_LogsAWarningNotAnErrorAndStillReportsATimeoutFailure()
    {
        // An unanswered approval is an expected, typed outcome, so it must not print an ERR line with a stack trace.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var logger = new RecordingLogger<InvocationRunner>();
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var runner = CreateRunner(CreateFactory(ApprovalExpiredUpdates()), eventDispatcher: dispatcher, logger: logger);

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 1, logger.CountContaining("The approval for tool 'run_python' expired", LogLevel.Warning));
        AssertEx.Equal(expected: 0, logger.CountContaining("failed", LogLevel.Error));
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenTimeoutCallbacksRunInReverseRegistrationOrder_StillMapsTimeoutFailureCategory()
    {
        // Regression pin for the LIFO callback race. CancellationToken callbacks run in REVERSE registration order, so
        // every registration made after the runner's own (a streaming agent's, or the one this test makes) is invoked
        // FIRST and can release the run into the failure mapping before anything the runner registered earlier has had
        // a chance to run. Classification must therefore be derived from observable state, never from a flag set by a
        // racing callback. This test makes that ordering deterministic instead of load-dependent: the late-registered
        // callback below releases the parked agent and then BLOCKS the cancel-callback loop until the failure has been
        // reported, so any earlier registration provably runs too late to influence the category.
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failureReported = new ManualResetEventSlim(initialState: false);

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>())
                  .Returns(_ =>
                  {
                      failureReported.Set();
                      return Task.CompletedTask;
                  });

        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForRelease(release.Task, started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        var invocationCancellationTokenSource = AssertEx.NotNull(GetActiveInvocationCancellationTokenSource(runner));
        using (invocationCancellationTokenSource.Token.Register(() =>
               {
                   release.TrySetResult();
                   failureReported.Wait(TimeSpan.FromSeconds(5));
               }))
        {
            await invocationCancellationTokenSource.CancelAsync();
        }

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenTheStreamEndsCleanlyOnACancelledTurn_ReportsCancelledNotCompleted()
    {
        // The Cancelled half of the runner's post-tool-loop invocationToken.ThrowIfCancellationRequested(). Same shape
        // as the Timeout sibling below, and for the same reason: the test registers its own token callback AFTER the
        // runner's, so in LIFO order it runs FIRST, releases the parked stream and then BLOCKS the callback chain until
        // the failure has been reported. Nothing the runner registered earlier — and nothing downstream of the stream —
        // can observe the cancellation before the loop returns, so the stream ends NORMALLY and the runner's own check
        // is the only thing left that can stop a cancelled turn being reported as a completed one.
        //
        // The trigger is an OPERATOR stop rather than the raw token source, which is what makes this the category pin:
        // Cancel records its origin, so the same check must classify this Cancelled while the sibling classifies
        // Timeout. Both go red if that one line is removed.
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failureReported = new ManualResetEventSlim(initialState: false);

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>())
                  .Returns(_ =>
                  {
                      failureReported.Set();
                      return Task.CompletedTask;
                  });

        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForRelease(release.Task, started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        var invocationCancellationTokenSource = AssertEx.NotNull(GetActiveInvocationCancellationTokenSource(runner));
        using (invocationCancellationTokenSource.Token.Register(() =>
               {
                   release.TrySetResult();
                   failureReported.Wait(TimeSpan.FromSeconds(5));
               }))
        {
            runner.Cancel(package.InvocationId);
        }

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
        await dispatcher.DidNotReceiveWithAnyArgs()
                        .ReportInvocationCompletedAsync(Arg.Any<Guid>(),
                            Arg.Any<int?>(),
                            Arg.Any<int?>(),
                            Arg.Any<int?>(),
                            Arg.Any<int?>(),
                            Arg.Any<long?>(),
                            Arg.Any<string?>(),
                            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task CancelAll_WhileRunning_MapsCancelledFailureCategory()
    {
        // A disconnect-driven CancelAll is an external stop, not the invocation watchdog: it must classify as
        // Cancelled, and it must do so without depending on which token callback happens to run first.
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        runner.CancelAll();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_LocalLlamaCppModel_WarmsToReadinessBeforeGenerating()
    {
        // For a local llama.cpp model the runner warms the model to readiness BEFORE the watched streaming pull
        // begins, so the cold load is never guarded by (and killed by) the stream-idle watchdog.
        var events = new ConcurrentQueue<string>();

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        provider.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    events.Enqueue("warm");
                    return Task.CompletedTask;
                });

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var factory = CreateFactory(_ => WarmOrderingUpdates(events));
        var runner = CreateRunner(factory, providerResolver: resolver);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await provider.Received(1).WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var ordered = events.ToArray();
        AssertEx.True(ordered.Length >= 2, "Both the warm and stream events should have fired.");
        AssertEx.Equal("warm", ordered[0]); // readiness precedes generation.
        AssertEx.Contains(ordered, "stream");
    }

    [Test]
    public async Task RunAsync_LocalLlamaCppModel_ReportsTheModelReadinessDurationOntoTheInvocationState()
    {
        // The whole-turn clock starts BEFORE the warm above, so a cold first turn's duration is mostly llama-server
        // launching and the model loading (measured live: 206 s cold against 27 s warm for the same work). The warm
        // phase's own duration therefore has to reach the invocation state, or the persisted turn time cannot be split.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        provider.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(eventDispatcher: dispatcher, providerResolver: resolver, agentUpdates: CreateUpdates("ok"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId, Arg.Is<long?>(static value => value >= 0), Arg.Any<TurnUsageTotals?>());
    }

    [Test]
    public async Task RunAsync_WhenNoLocalRuntimeWarms_ReportsNoModelReadinessDuration()
    {
        // Null, not zero: a remote or Ollama turn warmed nothing, and zero would claim it proved a warm start.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(OllamaLocalModelProvider.OllamaProviderName);

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(OllamaLocalModelProvider.OllamaProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(eventDispatcher: dispatcher, providerResolver: resolver, agentUpdates: CreateUpdates("ok"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId, Arg.Is<long?>(static value => value == null), Arg.Any<TurnUsageTotals?>());
    }

    [Test]
    public async Task RunAsync_OllamaModel_DoesNotWarmViaReadinessPhase()
    {
        // The readiness (warm) phase is llama.cpp-only. An Ollama model must NOT be warmed here (it warms
        // cheaply on first send), so the phase is a no-op for it.
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(OllamaLocalModelProvider.OllamaProviderName);

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(OllamaLocalModelProvider.OllamaProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(providerResolver: resolver, agentUpdates: CreateUpdates("ok"));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await provider.DidNotReceive().WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenUnexpected_ClearsInvocationCancellationTokenSource()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("boom")));

        var runner = CreateRunner(factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Null(GetActiveInvocationCancellationTokenSource(runner));
    }

    /// <summary>The per-effort budgets are node settings read per turn.</summary>
    [Test]
    public async Task RunAsync_HandsTheNodeReasoningBudgetsToTheAgentDefinition()
    {
        var budgets = ReasoningBudgets.Default with
        {
            Medium = 3000,
            UnspecifiedEffort = "medium"
        };
        InvocationAgentDefinition? captured = null;
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Do<InvocationAgentDefinition>(definition => captured = definition), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("stop after the definition")));

        var runner = CreateRunner(factory,
            configureRuntimeSettings: settings => settings.GetReasoningBudgetsAsync(Arg.Any<CancellationToken>()).Returns(budgets));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(budgets, AssertEx.NotNull(captured).ReasoningBudgets);
    }

    /// <summary>
    ///     With no cap a 0.8B model can repeat "000…" until the 65k window runs out. The
    ///     shipped variant caps at half the launched window, at most the node ceiling; notice-only and off do not cap.
    /// </summary>
    [Test]
    [Arguments("cap", 16_384, 65_536, 16_384)]
    [Arguments("cap", 16_384, 4_096, 2_048)]
    [Arguments("cap", 1_000, 65_536, 1_000)]
    [Arguments("notice", 16_384, 65_536, null)]
    [Arguments("off", 16_384, 65_536, null)]
    public async Task RunAsync_HandsTheNodeOutputCapForTheLaunchedWindowToTheAgentDefinition(string mode, int ceiling, int window, int? expectedCap)
    {
        InvocationAgentDefinition? captured = null;
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Do<InvocationAgentDefinition>(definition => captured = definition), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("stop after the definition")));

        var runner = CreateRunner(factory,
            providerResolver: CreateLlamaCppResolver(window),
            configureRuntimeSettings: settings => settings.GetChatOutputCapAsync(Arg.Any<CancellationToken>()).Returns(new ChatOutputCap
            {
                Mode = mode,
                MaxTokens = ceiling
            }));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(expectedCap, AssertEx.NotNull(captured).DefaultMaxOutputTokens);
    }

    /// <summary>
    ///     A frozen benchmark replays what it sent before the cap and the thinking ladder became node settings: no default
    ///     output cap, and 1024/2048/8192/24576 with a blank effort on medium, whatever the node has saved since.
    /// </summary>
    [Test]
    public async Task RunAsync_ForABenchmarkPackage_IgnoresTheNodeOutputCapAndThinkingBudgets()
    {
        InvocationAgentDefinition? captured = null;
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Do<InvocationAgentDefinition>(definition => captured = definition), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("stop after the definition")));
        var runner = CreateRunner(factory,
            providerResolver: CreateLlamaCppResolver(65_536),
            configureRuntimeSettings: settings =>
            {
                settings.GetChatOutputCapAsync(Arg.Any<CancellationToken>()).Returns(new ChatOutputCap
                {
                    Mode = "cap",
                    MaxTokens = 1_000
                });
                settings.GetReasoningBudgetsAsync(Arg.Any<CancellationToken>()).Returns(ReasoningBudgets.Default with
                {
                    Medium = 3000,
                    UnspecifiedEffort = "minimal"
                });
            });
        var package = BenchmarkRunExecutor.BuildPrimaryPackage(new LocalChatRuntimePackageBuilder(),
            new ResolvedAgentRuntime("Be helpful.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1),
            "What is 2+2?",
            "bench-model",
            BenchmarkFrozenPolicies.DeterministicSampling(),
            requestedContextTokens: 65_536,
            invocationTimeoutSeconds: 600);

        await RunAsync(runner, package);

        var definition = AssertEx.NotNull(captured);
        AssertEx.Null(definition.DefaultMaxOutputTokens, "a context-limited benchmark takes no default output cap");
        AssertEx.Equal(new ReasoningBudgets
        {
            Minimal = 1024,
            Low = 2048,
            Medium = 8192,
            High = 24576,
            UnspecifiedEffort = "medium"
        }, definition.ReasoningBudgets);
    }

    /// <summary>An answer cut off at the cap or the window says so, rather than reading as complete.</summary>
    [Test]
    [Arguments("cap", true)]
    [Arguments("notice", true)]
    [Arguments("off", false)]
    public async Task RunAsync_WhenTheAnswerEndsOnLength_EmitsTheOutputLimitNoticeUnlessOff(string mode, bool expectNotice)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: ReasoningThenContentFinishing("length", "thought", "0000"),
            configureRuntimeSettings: settings => settings.GetChatOutputCapAsync(Arg.Any<CancellationToken>()).Returns(new ChatOutputCap
            {
                Mode = mode,
                MaxTokens = 16_384
            }));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(expectNotice ? 1 : 0).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.OutputLimitReached
            && payload.Message == "The answer stopped at the length limit before the model finished."
            && payload.Detail == "length"));
    }

    [Test]
    public async Task RunAsync_WhenTheAnswerEndsOnStop_EmitsNoOutputLimitNotice()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ReasoningThenContentFinishing("stop", "thought", "Hello."));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.OutputLimitReached));
    }

    /// <summary>
    ///     Model-matrix follow-up: orchestration participants ran uncapped. Each is capped against the window ITS model was
    ///     launched with (participants can run different models); notice-only and off do not cap.
    /// </summary>
    [Test]
    [Arguments("cap", 16_384, 2_048)]
    [Arguments("notice", null, null)]
    [Arguments("off", null, null)]
    public async Task RunAsync_ForAnOrchestration_CapsEachParticipantAgainstItsOwnWindow(string mode, int? expectedTriageCap, int? expectedSpecialistCap)
    {
        var definition = await CaptureOrchestrationDefinitionAsync(new ChatOutputCap
        {
            Mode = mode,
            MaxTokens = 16_384
        }, RuntimePackageBuilder.Valid());

        AssertEx.Equal(expectedTriageCap, definition.Participants[0].DefaultMaxOutputTokens, "the triage runs the turn's 65,536 window");
        AssertEx.Equal(expectedSpecialistCap, definition.Participants[1].DefaultMaxOutputTokens, "the specialist runs a 4,096 window");
    }

    /// <summary>
    ///     A participant on another local model that is not loaded yet has no window to cap against, and nothing recomputes the
    ///     cap after its deferred load, so it takes the ceiling; a cloud or external participant stays uncapped like such a turn.
    /// </summary>
    [Test]
    [Arguments("cold-model", "cap", 16_384)]
    [Arguments("cold-model", "notice", null)]
    [Arguments("cold-model", "off", null)]
    [Arguments(CloudModel, "cap", null)]
    [Arguments("ext:provider/remote-model", "cap", null)]
    public async Task RunAsync_ForAnOrchestration_CapsAParticipantWithAnUnknownWindowOnlyWhenItIsLocal(string specialistModel, string mode, int? expectedSpecialistCap)
    {
        var definition = await CaptureOrchestrationDefinitionAsync(new ChatOutputCap
        {
            Mode = mode,
            MaxTokens = 16_384
        }, RuntimePackageBuilder.Valid(), specialistModel: specialistModel);

        AssertEx.Equal(specialistModel, definition.Participants[1].ModelId);
        AssertEx.Null(definition.Participants[1].EffectiveContextTokens, "the specialist's window is not known at build time");
        AssertEx.Equal(expectedSpecialistCap, definition.Participants[1].DefaultMaxOutputTokens);
    }

    /// <summary>A cloud or external participant on another model budgets against its declared window, as such a turn does.</summary>
    [Test]
    [Arguments(CloudModel)]
    [Arguments("ext:provider/remote-model")]
    public async Task RunAsync_ForAnOrchestration_GivesARemoteParticipantItsDeclaredWindow(string specialistModel)
    {
        var definition = await CaptureOrchestrationDefinitionAsync(new ChatOutputCap
        {
            Mode = "cap",
            MaxTokens = 16_384
        }, RuntimePackageBuilder.Valid(), specialistModel: specialistModel, declaredRemoteWindow: 20_000);

        AssertEx.Equal(specialistModel, definition.Participants[1].ModelId);
        AssertEx.Equal(20_000, definition.Participants[1].EffectiveContextTokens);
        AssertEx.Equal(10_000, definition.Participants[1].DefaultMaxOutputTokens, "half the declared window, not the unlaunched-local ceiling");
    }

    [Test]
    [Arguments("small-model")]
    [Arguments("cold-model")]
    public async Task RunAsync_ForAFrozenBenchmarkOrchestration_UsesTheFrozenLadderAndNoCap(string specialistModel)
    {
        var definition = await CaptureOrchestrationDefinitionAsync(new ChatOutputCap
        {
            Mode = "cap",
            MaxTokens = 1_000
        }, RuntimePackageBuilder.Valid(), frozen: true, specialistModel);

        AssertEx.True(definition.Participants.All(static participant => participant.DefaultMaxOutputTokens is null), "a frozen run takes no default cap");
        AssertEx.True(definition.Participants.All(static participant => participant.ReasoningBudgets == ReasoningBudgets.Frozen), "a frozen run takes the fixed ladder");
    }

    /// <summary>
    ///     Model-matrix follow-up: an orchestration whose answer stopped at the length limit said nothing, unlike a single-agent turn.
    ///     A handoff round with no text after it must not hide the cut-off.
    /// </summary>
    [Test]
    [Arguments("cap", false, 1)]
    [Arguments("notice", false, 1)]
    [Arguments("off", false, 0)]
    [Arguments("cap", true, 1)]
    public async Task RunAsync_WhenTheLastOrchestrationAnswerEndsOnLength_EmitsOneOutputLimitNoticeUnlessOff(string mode, bool handoffRoundAfter, int expectedNotices)
    {
        OrchestrationRound[] rounds = handoffRoundAfter
            ? [new("a", "Handing over.", "tool_calls"), new("b", "0000", "length"), new("a", Text: null, "tool_calls")]
            : [new("a", "Handing over.", "tool_calls"), new("b", "0000", "length")];
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            orchestrationAgentFactory: CreateOrchestrationFactory(OrchestrationRounds(rounds), out _),
            configureRuntimeSettings: settings => settings.GetChatOutputCapAsync(Arg.Any<CancellationToken>()).Returns(new ChatOutputCap
            {
                Mode = mode,
                MaxTokens = 16_384
            }));

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build());

        await dispatcher.Received(expectedNotices).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.OutputLimitReached
            && payload.Message == "The answer stopped at the length limit before the model finished."
            && payload.Detail == "length"));
    }

    /// <summary>An earlier participant's cut-off is not the answer the user reads when the final participant finished normally.</summary>
    [Test]
    [Arguments("length")]
    [Arguments("tool_calls")]
    public async Task RunAsync_WhenTheFinalOrchestrationParticipantStops_EmitsNoOutputLimitNotice(string earlierFinish)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            orchestrationAgentFactory: CreateOrchestrationFactory(OrchestrationRounds(new("a", "partial", earlierFinish), new("b", "Done.", "stop")), out _));

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build());

        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.OutputLimitReached));
    }

    /// <summary>
    ///     A final round that is only <c>&lt;tool_call&gt;</c> text finished "stop" and raised no notice. The text stays (a turn
    ///     never loses its only visible text); the notice says it is no answer.
    /// </summary>
    [Test]
    [Arguments("<tool_call>\n{\"name\": \"Calculate\", \"arguments\": {\"expression\": \"2+2\"}}\n</tool_call>")]
    [Arguments("  <tool_call>{\"name\": \"a\"}</tool_call>\n\n<tool_call>{\"name\": \"b\"}</tool_call>\n")]
    // A malformed, unclosed block, as a model can emit it.
    [Arguments("<tool_call> {\"tool\": {\"name\": \"list_files\", \"arguments\": {}}")]
    [Arguments("<tool_call>{\"name\": \"a\"}")]
    public async Task RunAsync_WhenTheFinalAnswerIsOnlyToolCallMarkup_EmitsTheToolCallAsTextNotice(string answer)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates(answer));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EmptyAnswer
            && payload.Message == "The model wrote a tool call as text instead of calling the tool, so the call did not run and there is no answer."));
        await dispatcher.DidNotReceive().ReportInvocationTextReclassifiedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    [Arguments("Call it like this: <tool_call>{\"name\": \"a\"}</tool_call>")]
    [Arguments("<tool_call>{\"name\": \"a\"}</tool_call>\nThat is the syntax.")]
    [Arguments("<tool_call> is the tag a call starts with; nothing was called.")]
    [Arguments("The answer is 4.")]
    public async Task RunAsync_WhenTheFinalAnswerHasTextBesideToolCallMarkup_RaisesNoEmptyAnswerNotice(string answer)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates(answer));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EmptyAnswer));
    }

    [Test]
    public async Task RunAsync_AfterUnexpected_StartsSecondInvocationCleanly()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("boom")));

        var runner = CreateRunner(factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());

        AssertEx.Null(GetActiveInvocationCancellationTokenSource(runner));
    }

    [Test]
    public async Task RunAsync_WhenUnexpectedMessageIsLong_TruncatesTo512Characters()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        var longMessage = new string(c: 'x', count: 600);
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException(longMessage)));

        string? failureMessage = null;
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Do<string>(message => failureMessage = message), Arg.Any<FailureCategory>())
                  .Returns(Task.CompletedTask);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(expected: 512, AssertEx.NotNull(failureMessage).Length);
    }

    [Test]
    public async Task RunAsync_WhenNodeDrainingBegan_RejectsNewLocalInvocation()
    {
        // A local turn admitted AFTER shutdown drain has snapshotted the active set must be rejected, never
        // become an untracked active run the drain never waits for. DrainActiveInvocationsAsync fences local admission;
        // a subsequent local (loopback) RunAsync is rejected with a classified failure and never streams.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates("should-not-stream"));

        // Drain with nothing active: returns immediately but latches the draining fence.
        var drained = await runner.DrainActiveInvocationsAsync(TimeSpan.FromSeconds(1));
        AssertEx.True(drained, "an empty drain completes immediately");

        var package = RuntimePackageBuilder.Valid()
                                           .WithInvocationId(Guid.NewGuid())
                                           .Build();
        await RunAsync(runner, package);

        // Rejected cleanly: a classified failure reported, no stream, and the runner is not left tracking it as active.
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
        await dispatcher.DidNotReceive().ReportInvocationStreamChunkAsync(package.InvocationId, Arg.Any<string>());
        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
    }

    [Test]
    public async Task RunAsync_WhenPackageHasOrchestrationSpec_DrivesOrchestrationAndStreamsDeltas()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var singleAgentFactory = CreateFactory(CreateUpdates("single-agent-should-not-run"));
        var orchestrationFactory = CreateOrchestrationFactory(OrchestrationTextUpdates("Hello", " world"), out var sessionRef);
        var runner = CreateRunner(singleAgentFactory, eventDispatcher: dispatcher, orchestrationAgentFactory: orchestrationFactory);
        var package = RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build();

        await RunPlainAsync(runner, package);

        await orchestrationFactory.Received(1).CreateAsync(Arg.Any<OrchestrationAgentDefinition>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>());
        await singleAgentFactory.DidNotReceive().CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>());
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, " world");
        AssertEx.True(sessionRef.Value!.Disposed, "The orchestration session must be disposed after the run.");
    }

    [Test]
    public async Task RunAsync_WhenNoOrchestrationSpec_TakesSingleAgentPath()
    {
        // The single-agent regression guard: a package without a spec must NOT touch the orchestration factory.
        var singleAgentFactory = CreateFactory(CreateUpdates("Hello", " world"));
        var orchestrationFactory = Substitute.For<IOrchestrationAgentFactory>();
        var runner = CreateRunner(singleAgentFactory, orchestrationAgentFactory: orchestrationFactory);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        await orchestrationFactory.DidNotReceive().CreateAsync(Arg.Any<OrchestrationAgentDefinition>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>());
        await singleAgentFactory.Received(1).CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenOrchestrationSurfacesApproval_RoundTripsAndResumesOnSession()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        // The gated session blocks its post-approval text/terminal on ApprovalGate, which only RespondToApprovalAsync
        // completes — so "done" + completion are reached ONLY if the runner actually resumes the held session by key.
#pragma warning disable CA2000 // The runner owns disposal of the session via its `await using`; the test asserts Disposed.
        var gatedSession = new FakeOrchestrationRunSession(session => OrchestrationGatedApprovalThenText(session, "call-1", "run_in_agent_home", "done"));
#pragma warning restore CA2000
        var orchestrationFactory = CreateOrchestrationFactory(gatedSession, out var sessionRef);
        var runner = CreateRunner(eventDispatcher: dispatcher, orchestrationAgentFactory: orchestrationFactory);
        var invocationId = Guid.NewGuid();

        var runTask = RunPlainAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithOrchestrationSpec(SampleSpec()).Build());

        // The approval card's opaque request id is published only on the dispatcher now that no hub send carries it.
        string? requestId = null;
        await AssertEx.EventuallyAsync(() =>
        {
            requestId = dispatcher.ReceivedCalls()
                                  .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(IWorkerEventDispatcher.ReportApprovalRequestedAsync), StringComparison.Ordinal))
                                  .Select(call => ((ApprovalRequestPayload)call.GetArguments()[0]!).RequestId)
                                  .FirstOrDefault();
            return requestId is not null;
        }, TimeSpan.FromSeconds(5));

        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = requestId!,
            Approved = true
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportApprovalRequestedAsync(Arg.Is<ApprovalRequestPayload>(payload => payload.InvocationId == invocationId));
        // The approval card must name the tool, not the opaque correlation id (single-agent UX parity).
        AssertEx.Equal(expected: 1, sessionRef.Value!.ApprovalResponses.Count);
        AssertEx.True(sessionRef.Value.ApprovalResponses[0].Approved, "An approved decision must be forwarded to the session as approved=true.");
        AssertEx.Equal("call-1", sessionRef.Value.ApprovalResponses[0].RequestId);
        // Reaching this asserts the gated post-approval portion streamed — i.e. the resume drove the held session.
    }

    [Test]
    public async Task RunAsync_WhenCompletes_CleansUpStaleToolCalls()
    {
        var registry = new PendingToolCallRegistry();
        var runner = CreateRunner(workerOptions: OneMinutePendingToolCallAge(), pendingToolCallRegistry: registry);
        var stale = ParkStaleToolCall(registry, TimeSpan.FromMinutes(2));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(expected: 0, registry.Calls.Count, "a completed turn must sweep the calls nothing will ever answer");
        var exception = await AssertEx.ThrowsAsync<ApprovalExpiredException>(() => stale);
        AssertEx.Contains(exception.Message, "expired", StringComparison.Ordinal);
    }

    [Test]
    public async Task RunAsync_WhenFaults_CleansUpStaleToolCalls()
    {
        // The sweep lives in RunAsync's finally, so a turn that died on its stream must still release the stranded
        // calls — a failure path that skipped it would leave every one of them parked until the next cleanup tick.
        var registry = new PendingToolCallRegistry();
        var runner = CreateRunner(workerOptions: OneMinutePendingToolCallAge(),
            agentUpdates: ThrowingUpdates(),
            pendingToolCallRegistry: registry);
        var stale = ParkStaleToolCall(registry, TimeSpan.FromMinutes(2));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(expected: 0, registry.Calls.Count, "a failed turn must sweep the calls nothing will ever answer");
        var exception = await AssertEx.ThrowsAsync<ApprovalExpiredException>(() => stale);
        AssertEx.Contains(exception.Message, "expired", StringComparison.Ordinal);
    }

    [Test]
    public async Task ResolveToolCallCardId_MatchesTheStreamingLoopSemantics_ForAllCallIdShapes()
    {
        await Task.CompletedTask;

        // The approval lifecycle and the streaming tool-call lifecycle must resolve the SAME card id for a call so the
        // browser attaches the Approve/Deny controls to the matching card. Both go through this helper, so a present
        // CallId wins and an id-less one — null or, the shape Microsoft.Extensions.AI actually permits, the empty
        // string — falls back to the tool name on BOTH paths. Propagating the blank instead kept the two aligned but
        // aligned them on a key every downstream consumer discards, so the call was recorded nowhere; the tool name is
        // aligned AND usable. The streaming loop layers a "<name>#N" surrogate on top of this for a SECOND id-less call
        // to a tool whose first is still open; the first one still resolves here, which is what keeps the approval card
        // correlated.
        AssertEx.Equal("call-1", InvocationRunner.ResolveToolCallCardId("call-1", "run_in_agent_home"));
        AssertEx.Equal("run_in_agent_home", InvocationRunner.ResolveToolCallCardId(callId: null, "run_in_agent_home"));
        AssertEx.Equal("run_in_agent_home", InvocationRunner.ResolveToolCallCardId(string.Empty, "run_in_agent_home"));
        AssertEx.Equal(string.Empty, InvocationRunner.ResolveToolCallCardId(callId: null, toolName: null));
    }

    [Test]
    public async Task RunAsync_WhenModelRoutesToCloud_DoesNotWarmALocalProvider()
    {
        // Regression: a cloud model id (e.g. "gpt-5.6-terra") has no entry in the model→provider map, so the resolver
        // defaults it to the local llama.cpp provider. The warm path then tried to cold-load llama-server and failed with
        // "model not installed" for what is actually a cloud send. The warm must honor the same cloud-vs-local routing
        // decision the send makes, so a cloud-selected model warms nothing local.

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.IsCloudProviderSelected(Arg.Any<string?>()).Returns(true);

        var runner = CreateRunner(providerResolver: resolver,
            activeCloudFactory: cloudFactory,
            agentUpdates: CreateUpdates("ok"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        // The bug was the cold-load attempt itself; assert the warm never reached a local provider at all.
        await provider.DidNotReceive().WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenRequestedModelCannotBeVerified_SurfacesAModelSubstitutedNotice()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        // Force the fallback branch: the requested model never verifies against Ollama, so ResolveModelAsync falls
        // back to the node's default model (Ollama:ChatModel = "qwen3.5:0.8b", wired in CreateRunner) and reports the
        // substitution.
        capabilityReporter.VerifyOllamaAndModelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        var runner = CreateRunner(eventDispatcher: dispatcher, capabilityReporter: capabilityReporter, agentUpdates: CreateUpdates("ok"));
        var package = RuntimePackageBuilder.Valid().WithModel("some-unverifiable-model").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.ModelSubstituted
            && payload.Message.Contains("some-unverifiable-model", StringComparison.Ordinal)
            && payload.Message.Contains("qwen3.5:0.8b", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_ReportsTheLastFinishReasonTheProviderStreamed()
    {
        // The benchmark ranking reads this off the terminal snapshot to tell a truncated answer from a complete one, so
        // an intermediate tool-call segment must not be what the turn is recorded as having stopped for: last wins.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: FinishReasonUpdates(ChatFinishReason.ToolCalls, ChatFinishReason.Length));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            "length",
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenTheProviderReportsNoFinishReason_ReportsNull()
    {
        // Fail open, never infer: a provider that reports nothing leaves the field null rather than being labeled
        // "stop", which would make an unmeasured turn indistinguishable from a measured complete one.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: FinishReasonUpdates(first: null, last: null));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            null,
            Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenTheRuntimeStreamsTimings_ReportsTheSeparatedThroughput()
    {
        // The benchmark reads this off the terminal snapshot. Before it existed, one blended tokens/second conflated
        // prefill with decode, so the same model measured on a long prompt and a short one produced two numbers that
        // could not be compared. pp and tg must arrive apart, with TTFT alongside them.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: TimingsUpdates());
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Is<InvocationThroughput?>(throughput => throughput != null
                                                        && throughput.PromptTokens == 123
                                                        && IsClose(throughput.PromptMs, 456.5)
                                                        && throughput.GenerationTokens == 89
                                                        && IsClose(throughput.GenerationMs, 1011.5)
                                                        && throughput.CachedPromptTokens == 7
                                                        && throughput.SegmentCount == 1
                                                        && throughput.TimeToFirstTokenMs > 0));
    }

    [Test]
    public async Task RunAsync_WhenOneTurnMakesSeveralProviderRequests_SumsEveryReading()
    {
        // The live bug this exists for: a tool-calling turn is SEVERAL llama-server requests inside ONE
        // RunStreamingAsync (FunctionInvokingChatClient owns that loop), and only the LAST request's timings were kept.
        // A real run reported prompt 283 + cached 2346 + generated 1720 against a usage total of 4349 — those three
        // summed to the total precisely because a second request had happened and the first had been thrown away.
        // Every reading must fold in, and the request count must be visible so the sums can be read honestly.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: TwoRequestTimingsUpdates());
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Is<InvocationThroughput?>(throughput => throughput != null
                                                        && throughput.PromptTokens == 40 + 283
                                                        && IsClose(throughput.PromptMs, 12.5 + 456.5)
                                                        && throughput.GenerationTokens == 89 + 1720
                                                        && IsClose(throughput.GenerationMs, 200.25 + 1011.5)
                                                        && throughput.CachedPromptTokens == 2346
                                                        && throughput.SegmentCount == 2));
    }

    [Test]
    public async Task RunAsync_WhenTheRuntimeStreamsNoTimings_ReportsOnlyTheClientMeasuredTtft()
    {
        // Fail open: a provider that times nothing leaves the pp/tg split absent rather than having a zero-valued
        // measurement invented for it. Time to first token still arrives — it is measured by our own stopwatch, not by
        // the runtime — so the caller-visible latency is reported for every provider, cloud ones included.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: FinishReasonUpdates(ChatFinishReason.Stop, ChatFinishReason.Stop));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Is<InvocationThroughput?>(throughput => throughput != null
                                                        && throughput.PromptTokens == null
                                                        && throughput.GenerationTokens == null));
    }

    [Test]
    public async Task RunAsync_ExceedsMaxResponseSize_SendsInvocationFailed()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 1,
            MaxPendingToolCallAgeMinutes = 5
        }, agentUpdates: CreateUpdates(new string(c: 'x', (1024 * 1024) + 1)), eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message.Contains("Response size exceeded", StringComparison.Ordinal)),
            Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_ExceedsMaxReasoningSize_SendsInvocationFailed()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 1,
            MaxPendingToolCallAgeMinutes = 5
        }, agentUpdates: CreateMixedUpdates((Text: null, Thinking: new string(c: 'x', (1024 * 1024) + 1))), eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message.Contains("Reasoning size exceeded", StringComparison.Ordinal)),
            Arg.Any<FailureCategory>());
    }
}
