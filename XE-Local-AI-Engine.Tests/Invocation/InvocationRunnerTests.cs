namespace XE_Local_AI_Engine.Tests.Invocation;

using System.ClientModel.Primitives;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

public sealed class InvocationRunnerTests
{
    private const string SkillName = "demo";

    // One stubbed local warm, and the floor a turn that summed TWO of them must clear. The floor sits well under the
    // pair and well over a single warm, so neither a fast machine nor a loaded one changes the verdict.
    private static readonly TimeSpan WarmDelay = TimeSpan.FromMilliseconds(40);

    private const long TwoWarmFloorMs = 60L;

    // MAF's skill-tool names, aliased once so the scoped MAAI001 suppression the [Experimental] Agent Skills surface
    // needs is not repeated at every use site below.
#pragma warning disable MAAI001
    private const string LoadSkillToolName = AgentSkillsProvider.LoadSkillToolName;

    private const string ReadSkillResourceToolName = AgentSkillsProvider.ReadSkillResourceToolName;

    private const string RunSkillScriptToolName = AgentSkillsProvider.RunSkillScriptToolName;
#pragma warning restore MAAI001

    private static readonly JsonSerializerOptions AskUserArgumentOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid SkillId = Guid.Parse("2f2f9a3e-0d1a-4c9a-9d9c-6f6f0a2b7c11");

    [Test]
    public async Task RunAsync_ValidPackage_SendsAcceptance()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("Hello", " world"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        AssertEx.Contains(sender.AcceptedInvocations, package.InvocationId);
    }

    [Test]
    public async Task RunAsync_ValidPackage_StreamsChunks()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("Hello", " world"));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.True(sender.SentEncryptedChunks.Count >= 1);
        AssertEx.True(sender.SentEncryptedChunks.All(chunk => chunk.MessageId != Guid.Empty));
        AssertEx.True(sender.SentEncryptedChunks.All(chunk => chunk.Kind == EncryptedChunkEnvelopeV1.ContentKind));
    }

    [Test]
    public async Task RunAsync_ValidPackage_ReportsChunksAndCompletionToDispatcher()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: CreateUpdates("Hello", " world"));

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
    public async Task RunAsync_WithThinkingAndTextChunks_ReportsBothToDispatcher()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
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
    public async Task RunAsync_WithThinkingAndTextChunks_SendsEncryptedReasoningChunksAndFinalReasoning()
    {
        var sender = new MockHubMessageSender();
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender,
            agentUpdates: CreateMixedUpdates((Text: "Hello", Thinking: "Let me think..."), (Text: " world", Thinking: " more thought")));

        await RunAsync(runner, package);

        var contentChunks = sender.SentEncryptedChunks.Where(chunk => chunk.Kind == EncryptedChunkEnvelopeV1.ContentKind).ToList();
        var reasoningChunks = sender.SentEncryptedChunks.Where(chunk => chunk.Kind == EncryptedChunkEnvelopeV1.ReasoningKind).ToList();

        AssertEx.Equal(expected: 2, contentChunks.Count);
        AssertEx.Equal(expected: 2, reasoningChunks.Count);
        AssertEx.Equal(expected: 1, reasoningChunks[0].Sequence);
        AssertEx.Equal(expected: 2, reasoningChunks[1].Sequence);
        AssertEx.Equal(expected: 1, sender.SentEncryptedCompletions.Count);
        AssertEx.True(sender.SentEncryptedCompletions[0].ReasoningFinalIv.HasValue);
        AssertEx.True(sender.SentEncryptedCompletions[0].ReasoningFinalCiphertext.HasValue);
        AssertEx.False(sender.SentEncryptedCompletions[0].TokenCounts.ContainsKey("outputTokens"));
        AssertEx.False(sender.SentEncryptedCompletions[0].TokenCounts.ContainsKey("reasoningTokens"));
    }

    [Test]
    public async Task RunAsync_WhenLoopbackInvocation_SkipsHubMessagesAndStillReportsDispatcherProgress()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var package = RuntimePackageBuilder.Valid()
                                           .WithRequestedCapability(LocalChatLoopbackDefaults.RequestedCapability)
                                           .Build();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: CreateUpdates("Hello", " world"));

        await RunAsync(runner, package);

        AssertEx.Empty(sender.AcceptedInvocations);
        AssertEx.Empty(sender.SentEncryptedChunks);
        AssertEx.Empty(sender.SentEncryptedCompletions);
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        // Match the new wall-clock duration arg with Arg.Any (non-deterministic); the token args remain null-checked.
        await dispatcher.Received(1)
                        .ReportInvocationCompletedAsync(package.InvocationId, Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null),
                            Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null), Arg.Any<long?>(),
                            Arg.Any<string?>(), Arg.Any<InvocationThroughput?>());
    }

    [Test]
    public async Task RunAsync_WhenPlainContext_StreamsTokenChunksAndSendsInvocationCompleted()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("Hello", " world"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        AssertEx.Contains(sender.AcceptedInvocations, package.InvocationId);
        AssertEx.Empty(sender.SentEncryptedChunks);
        AssertEx.Empty(sender.SentEncryptedCompletions);

        var contentChunks = sender.SentChunks.Where(chunk => !chunk.IsComplete).ToList();
        AssertEx.True(contentChunks.Count >= 2);
        AssertEx.True(contentChunks.All(chunk => chunk.InvocationId == package.InvocationId));
        AssertEx.True(sender.SentChunks.Any(chunk => chunk.IsComplete));

        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal(package.InvocationId, sender.SentCompletions[0].InvocationId);
        AssertEx.Equal("Hello world", sender.SentCompletions[0].FinalContent);
    }

    [Test]
    public async Task RunAsync_WhenPlainContext_StreamsReasoningChunksAndFinalReasoning()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
            agentUpdates: CreateMixedUpdates((Text: "Hello", Thinking: "Let me think..."), (Text: " world", Thinking: " more thought")));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        AssertEx.Empty(sender.SentEncryptedChunks);
        var reasoningChunks = sender.SentReasoningChunks.Where(chunk => !chunk.IsComplete).ToList();
        AssertEx.Equal(expected: 2, reasoningChunks.Count);
        AssertEx.Equal("Let me think...", reasoningChunks[0].Token);
        AssertEx.Equal(" more thought", reasoningChunks[1].Token);
        AssertEx.True(sender.SentReasoningChunks.Any(chunk => chunk.IsComplete));
        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal("Let me think... more thought", sender.SentCompletions[0].FinalReasoning);
        AssertEx.Null(sender.SentCompletions[0].ReasoningTokens);
        await dispatcher.Received().ReportInvocationThinkingChunkAsync(package.InvocationId, Arg.Any<string>());
    }

    [Test]
    public async Task RunAsync_WhenPlainContextReceivesUsageContent_SendsAuthoritativeTokenCounts()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: null),
            (Text: " world", Usage: new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 2,
                TotalTokenCount = 12
            })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal(expected: 10, sender.SentCompletions[0].InputTokens);
        AssertEx.Equal(expected: 2, sender.SentCompletions[0].OutputTokens);
        AssertEx.Equal(expected: 12, sender.SentCompletions[0].TokensUsed);
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
        // BE-01: the terminal usage-finalize must publish the cumulative model-token counter once per direction on the
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

        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
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
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("Hello", " world"));

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
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: ThrowingUpdates());

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
        var sender = new MockHubMessageSender();
        var orchestrationFactory = CreateOrchestrationFactory(OrchestrationParticipantTransitionUpdates(), out _);
        var runner = CreateRunner(sender, orchestrationAgentFactory: orchestrationFactory);

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build());

        var terminal = capture.Terminals.Single();
        AssertEx.Equal("completed", terminal.Outcome);
        AssertEx.Equal(expected: true, terminal.Orchestration);
        AssertEx.Equal(expected: 1L, capture.Handoffs.Single());
    }

    [Test]
    public async Task RunAsync_WhenUsageTokenCountExceedsInt32_SaturatesInsteadOfFaulting()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            // A provider reporting a count past int.MaxValue must clamp, not throw mid-stream and fail the invocation.
            InputTokenCount = (long)int.MaxValue + 100,
            OutputTokenCount = 5,
            TotalTokenCount = (long)int.MaxValue + 105
        })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        AssertEx.Empty(sender.SentFailures);
        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal(expected: int.MaxValue, sender.SentCompletions[0].InputTokens);
        AssertEx.Equal(expected: 5, sender.SentCompletions[0].OutputTokens);
        AssertEx.Equal(expected: int.MaxValue, sender.SentCompletions[0].TokensUsed);
    }

    [Test]
    public async Task RunAsync_WhenTheDerivedTotalExceedsInt32_SaturatesInsteadOfFaulting()
    {
        // The provider reported no total of its own, so the total is DERIVED from input + output. Summing them as
        // checked ints threw OverflowException mid-stream and failed the invocation; the derivation has to clamp
        // exactly as the single-round and accumulated paths already do.
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            InputTokenCount = int.MaxValue,
            OutputTokenCount = 1
        })));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        AssertEx.Empty(sender.SentFailures);
        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal(expected: int.MaxValue, sender.SentCompletions[0].TokensUsed);
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
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

        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal(expected: 3_000, sender.SentCompletions[0].InputTokens);
        AssertEx.Equal(expected: 30, sender.SentCompletions[0].OutputTokens);
        AssertEx.Equal(expected: 8, sender.SentCompletions[0].ReasoningTokens);
        AssertEx.Equal(expected: 3_038, sender.SentCompletions[0].TokensUsed);
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

    [Test]
    public async Task RunAsync_WhenSummedUsageExceedsInt32_SaturatesInsteadOfWrapping()
    {
        // Each round is comfortably inside int range; their SUM is not. The accumulator must clamp exactly as a single
        // oversized round does, rather than wrap negative and report a turn that consumed less than nothing. Asserted on
        // the turn totals, because they are the only place the rounds are added together at all.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
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

        AssertEx.Empty(sender.SentFailures);
        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId,
                            Arg.Any<long?>(),
                            Arg.Is<TurnUsageTotals?>(static usage => usage != null
                                                                     && usage.InputTokens == int.MaxValue
                                                                     && usage.OutputTokens == 10
                                                                     && usage.TotalTokens == int.MaxValue));
    }

    [Test]
    public async Task RunAsync_WhenProviderReportsNoTotal_DerivesTheTotalWithoutDoubleCountingReasoning()
    {
        // Microsoft.Extensions.AI documents ReasoningTokenCount as counted INSIDE OutputTokenCount, so a derived total
        // of input + output + reasoning charged every reasoning token twice whenever the provider omitted its own
        // total. 100 + 40 is the turn, not 100 + 40 + 30.
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 40,
            ReasoningTokenCount = 30
        })));

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal(expected: 140, sender.SentCompletions[0].TokensUsed);
        AssertEx.Equal(expected: 30, sender.SentCompletions[0].ReasoningTokens);
    }

    [Test]
    public async Task RunAsync_WhenProviderReportsOnlyReasoningTokens_LeavesTheTotalUnreported()
    {
        // Null-preserving: with neither input nor output reported there is nothing to derive a total from, and a
        // reasoning-only count is not a turn total.
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdatesWithUsage((Text: "Hello", Usage: new UsageDetails
        {
            ReasoningTokenCount = 30
        })));

        await RunPlainAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Null(sender.SentCompletions[0].TokensUsed, "a turn with no input or output count has no derivable total");
    }

    [Test]
    public async Task RunAsync_WhenPlainContextAndAgentRuntimeThrows_SendsPlainInvocationFailed()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: ThrowingUpdates());
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        AssertEx.Empty(sender.SentEncryptedFailures);
        AssertEx.Equal(expected: 1, sender.SentFailures.Count);
        AssertEx.Equal(package.InvocationId, sender.SentFailures[0].InvocationId);
        AssertEx.Null(sender.SentFailures[0].MessageId);
    }

    [Test]
    public async Task RunAsync_ValidPackage_SendsCompletion()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("Hello", " world"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 1, sender.SentEncryptedCompletions.Count);
        AssertEx.Equal(package.ConversationId, sender.SentEncryptedCompletions[0].ConversationId);
        AssertEx.Equal(expected: 1, sender.SentEncryptedCompletions[0].EpochVersion);
    }

    [Test]
    public async Task RunAsync_ValidationFails_ThrowsInvalidOperationException()
    {
        var sender = new MockHubMessageSender();
        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>(), Arg.Any<bool>()).Returns(new RuntimePackageValidationResult(isValid: false, ["bad package"]));

        var runner = CreateRunner(sender, validator: validator);
        var package = RuntimePackageBuilder.Valid().Build();

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner, package));

        AssertEx.Contains(exception.Message, "bad package");
        AssertEx.Empty(sender.SentEncryptedFailures);
    }

    [Test]
    public async Task RunAsync_WhenStoredHistoryHoldsAnOversizedMessage_StillRunsTheTurn()
    {
        // The poisoned-conversation regression: the per-turn re-validation used to hard-fail on ANY over-cap message in
        // the assembled context, including one already persisted in the conversation. Every later turn then re-validated
        // the same stored row and failed the same way, so the user could only abandon the conversation. The cap belongs
        // to the entry seams; oversized history is the budgeter's problem.
        var sender = new MockHubMessageSender();
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

        var runner = CreateRunner(sender, validator: validator);

        await RunAsync(runner, package);

        AssertEx.Empty(sender.SentEncryptedFailures);
        AssertEx.Equal(expected: 1, sender.SentEncryptedCompletions.Count);
    }

    [Test]
    public async Task RunAsync_NeverEnforcesTheMessageSizeCap()
    {
        // Pins the wiring the healing above depends on: the cap is enforced at the inbound seams (the chat hub and the
        // encrypted-envelope assembler), never on the node's own re-assembled turn context.
        var sender = new MockHubMessageSender();
        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>(), Arg.Any<bool>()).Returns(RuntimePackageValidationResult.Success);

        var runner = CreateRunner(sender, validator: validator);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        validator.Received(1).Validate(Arg.Any<RuntimePackage>(), enforceMessageSizeCap: false);
    }

    [Test]
    public async Task RunAsync_AgentRuntimeThrows_SendsInvocationFailed()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NotSupportedException("factory failed")));

        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId
                                                                         && failure.FailureCategory == nameof(FailureCategory.AgentRuntime)
                                                                         && failure.Error.Contains("Agent runtime error", StringComparison.Ordinal));
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            Arg.Is<string>(message => message.Contains("Agent runtime error", StringComparison.Ordinal)),
            FailureCategory.AgentRuntime);
    }

    [Test]
    public async Task RunAsync_NoChatModelInstalledThrows_ClassifiesModelNotInstalled()
    {
        // MapFailure must classify NoChatModelInstalledException as ModelNotInstalled with the actionable, path-free
        // constant — NOT the generic Unexpected/ProviderUnreachable — so a local-default send with no installed GGUF
        // surfaces a "pull a model" CTA instead of a dead-end provider error.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NoChatModelInstalledException()));

        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId,
            Arg.Is<string>(message => message.Contains("No chat model installed", StringComparison.Ordinal)),
            FailureCategory.ModelNotInstalled);
        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelNotInstalled));
    }

    [Test]
    public async Task RunAsync_RespectsCancellationToken_StopsStreaming()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: BlockingUpdates(gate.Task, started));
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
        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Cancelled));
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_MapsInvocationDefinitionSystemPromptAndSortOrder()
    {
        var sender = new MockHubMessageSender();
        InvocationAgentDefinition? capturedDefinition = null;
        var factory = CreateFactory(CreateUpdates("ok"), definition => capturedDefinition = definition);

        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("late")
                                           .WithConversationMessage(MessageRole.Assistant, "middle", sortOrder: 1)
                                           .WithConversationMessage(MessageRole.User, "early", sortOrder: -1)
                                           .Build();

        var runner = CreateRunner(sender, factory);
        await RunAsync(runner, package);

        var definition = AssertEx.NotNull(capturedDefinition);
        AssertEx.Equal("You are helpful.", definition.Instructions);
        AssertEx.Equal("early", definition.ConversationContext[0].Text);
        AssertEx.Equal("late", definition.ConversationContext[1].Text);
        AssertEx.Equal("middle", definition.ConversationContext[2].Text);
        AssertEx.Empty(definition.Tools);
    }

    [Test]
    public async Task RunAsync_WhenConversationMessageCarriesImages_EmitsDataContentIntoTheAgentContext()
    {
        // A vision turn: a ConversationMessageDto carrying image parts must map to an MEAI DataContent alongside its
        // text, so the model actually receives the image. Proves BuildChatMessages emits the image part.
        var sender = new MockHubMessageSender();
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

        var runner = CreateRunner(sender, factory);
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
        var sender = new MockHubMessageSender();
        var lastObservedSessionWasNull = false;
        var runner = CreateRunner(sender, CreateFactory(CreateUpdates("ok"), onSessionObserved: value => lastObservedSessionWasNull = value));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.True(lastObservedSessionWasNull);
    }

    [Test]
    public async Task RunAsync_WithApiSideAllowedTools_BuildsInvocationDefinitionTools()
    {
        var sender = new MockHubMessageSender();
        InvocationAgentDefinition? capturedDefinition = null;
        var factory = CreateFactory(CreateUpdates("ok"), definition => capturedDefinition = definition);

        var package = RuntimePackageBuilder.Valid()
                                           .WithAllowedTool("approve-job")
                                           .Build();

        var runner = CreateRunner(sender, factory);
        await runner.RunAsync(package);

        var definition = AssertEx.NotNull(capturedDefinition);
        AssertEx.Equal(expected: 1, definition.Tools.Count);
    }

    [Test]
    public async Task RunAsync_ExceedsMaxResponseSize_SendsInvocationFailed()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 1,
            MaxPendingToolCallAgeMinutes = 5
        }, agentUpdates: CreateUpdates(new string(c: 'x', (1024 * 1024) + 1)));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.ConversationId == package.ConversationId && failure.Error.Contains("Response size exceeded", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_ExceedsMaxReasoningSize_SendsInvocationFailed()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 1,
            MaxPendingToolCallAgeMinutes = 5
        }, agentUpdates: CreateMixedUpdates((Text: null, Thinking: new string(c: 'x', (1024 * 1024) + 1))));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.ConversationId == package.ConversationId && failure.Error.Contains("Reasoning size exceeded", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenAlreadyBusy_ThrowsInvalidOperationException()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, agentUpdates: BlockingUpdates(gate.Task, started));

        var firstTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        await started.Task;

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build()));
        AssertEx.Contains(exception.Message, "Worker is busy");

        gate.SetResult();
        await firstTask;
    }

    [Test]
    public async Task Cancel_WhileRunning_TerminatesStream()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender, agentUpdates: BlockingUpdates(gate.Task, started));

        var runTask = RunAsync(runner, package);
        await started.Task;
        runner.Cancel(package.InvocationId);
        gate.TrySetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Cancelled));
    }

    [Test]
    public async Task DrainActiveInvocationsAsync_WhenActiveInvocationCompletes_ReturnsTrue()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender, agentUpdates: BlockingUpdates(gate.Task, started));

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
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender, agentUpdates: BlockingUpdates(gate.Task, started));

        var runTask = RunAsync(runner, package);
        await started.Task;

        var drained = await runner.DrainActiveInvocationsAsync(TimeSpan.FromMilliseconds(10));

        AssertEx.False(drained);
        AssertEx.False(runTask.IsCompleted);
        AssertEx.Equal(expected: 1, runner.ActiveInvocationCount);

        gate.SetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.Equal(expected: 0, runner.ActiveInvocationCount);
        AssertEx.Empty(sender.SentEncryptedFailures);
    }

    [Test]
    public async Task RunAsync_WhenInvocationTimeoutElapses_MapsTimeoutFailureCategory()
    {
        var sender = new MockHubMessageSender();
        // Use a normal (non-zero) invocation timeout so the token is NOT already cancelled when the
        // runner starts: that guarantees the agent is enumerated and signals `started`. WithTimeout(0)
        // raced the timeout against reaching enumeration — under load the token cancelled before the
        // agent began streaming, so `started` never fired and `await started.Task` hung the whole run.
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        // Fire the invocation timeout deterministically now that the agent is streaming. The invocation source is
        // cancelled with no user cancel and a live caller token — the exact observable state the real CancelAfter
        // watchdog leaves behind — so the failure must map to Timeout.
        await AssertEx.NotNull(GetActiveInvocationCancellationTokenSource(runner)).CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Timeout));
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenInvocationTimeoutElapses_ReportsTheNodeMessageRequestTimeoutAsTheReason()
    {
        // The failure-reason breadcrumb: FailureCategory.Timeout alone cannot say WHICH bound fired (the invocation
        // ceiling, the stream-idle watchdog and an HTTP timeout all map to it), so the persisted message must name the
        // node's maximum message request timeout and its configured seconds. Pinned against the shared, unattributable
        // "Invocation timed out or was cancelled" this replaced.
        var sender = new MockHubMessageSender();
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 900).Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)));

        var runTask = RunAsync(runner, package);
        await started.Task;

        await AssertEx.NotNull(GetActiveInvocationCancellationTokenSource(runner)).CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.Timeout)
                       && failure.Error == "Timed out: the response exceeded the node maximum message request timeout (900s).");
    }

    [Test]
    public async Task Cancel_WhileRunning_ReportsTheUserStopAsTheReason()
    {
        // The same breadcrumb from the other side: an operator stop must never read like a timeout. This is the pair
        // that made the "Cancelled at ~550s" report unattributable — user stop, detached-grace reaper and the node
        // watchdog all persisted the identical sentence.
        var sender = new MockHubMessageSender();
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)));

        var runTask = RunAsync(runner, package);
        await started.Task;

        runner.Cancel(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.Cancelled) && failure.Error == "Stopped by user.");
    }

    [Test]
    public async Task CancelDetached_WhileRunning_ReportsTheDisconnectGraceAsTheReason()
    {
        // The detached-run reaper is the third cancellation cause, and the one an operator is least able to guess at:
        // it must name itself rather than share the user-stop sentence.
        var sender = new MockHubMessageSender();
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)));

        var runTask = RunAsync(runner, package);
        await started.Task;

        runner.CancelDetached(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.Cancelled)
                       && failure.Error == "Stopped: no client was attached to this run and the disconnect grace period expired.");
    }

    [Test]
    public async Task RunAsync_WhenTheProviderTimesOutWithNoTokenCancelled_ReportsATimeoutNotAnExternalStop()
    {
        // A provider-side HTTP timeout arrives as a TaskCanceledException whose token is NOT the runner's or the
        // caller's. It used to fall into the origin fallback and persist "Stopped externally (node shutdown or client
        // disconnect)" under the Cancelled category — blaming a disconnect that never happened and hiding a real
        // timeout. Nothing of ours is cancelled in this test, which is exactly the state that must map to Timeout.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var runner = CreateRunner(sender, CreateFactory(ProviderTimeoutUpdates()), eventDispatcher: dispatcher);

        await RunAsync(runner, package);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.Timeout)
                       && failure.Error == "Timed out: the model provider stopped responding before the node's own ceiling was reached.");
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenToolResultTimesOut_KeepsTheToolTimeoutDistinctFromAGenericToolFailure()
    {
        // ToolResultTimeout used to collapse into the same "Worker tool execution failed." every tool error uses, so a
        // turn killed by the tool-result bound was indistinguishable from a tool that simply errored.
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new WorkerToolCallException("read_file", "Tool call timed out waiting for a result.", new TimeoutException("timed out"))));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.AgentToolCall)
                       && failure.Error == "A tool call timed out waiting for its result.");
    }

    [Test]
    public async Task RunAsync_WhenToolCallFailsWithoutATimeout_KeepsTheGenericToolFailureMessage()
    {
        // The guard on the arm above: an ordinary tool error must NOT be relabelled a timeout.
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new WorkerToolCallException("read_file", "boom")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.AgentToolCall) && failure.Error == "Worker tool execution failed.");
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
        var sender = new MockHubMessageSender();
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

        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForRelease(release.Task, started, cancellationToken)), eventDispatcher: dispatcher);

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

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Timeout));
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Timeout);
    }

    [Test]
    public async Task CancelAll_WhileRunning_MapsCancelledFailureCategory()
    {
        // A disconnect-driven CancelAll is an external stop, not the invocation watchdog: it must classify as
        // Cancelled, and it must do so without depending on which token callback happens to run first.
        var sender = new MockHubMessageSender();
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        runner.CancelAll();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Cancelled));
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_WhenProviderUnreachable_MapsFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("offline")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ProviderUnreachable));
    }

    [Test]
    public async Task RunAsync_WhenProviderReturnsNotFound_MapsModelUnavailableFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("not found", inner: null, HttpStatusCode.NotFound)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelUnavailable)
                                                                         && failure.Error == "Selected model is not installed on this node.");
    }

    [Test]
    [Arguments("registry.ollama.ai/library/gemma:12b does not support thinking", "This model does not support reasoning.")]
    [Arguments("this model does not support tools", "This model does not support tool calling.")]
    public async Task RunAsync_WhenModelRejectsCapability_MapsModelCapabilityUnsupportedFailureCategory(string providerMessage, string expectedError)
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException(providerMessage, inner: null, HttpStatusCode.BadRequest)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelCapabilityUnsupported)
                                                                         && failure.Error == expectedError);
    }

    [Test]
    [Arguments("HTTP 400 (invalid_request_error: ) Failed to initialize samplers: failed to parse grammar")]
    [Arguments("Failed to initialize samplers")]
    [Arguments("failed to parse grammar")]
    public async Task RunAsync_WhenToolGrammarFailsToCompile_MapsModelCapabilityUnsupportedFailureCategory(string providerMessage)
    {
        // llama-server reports the sampler/grammar compile failure as an HTTP 400, so it must be classified here and not
        // swallowed by the generic HttpRequestException arm (ProviderUnreachable) or surfaced raw as Unexpected. The
        // model IS tool-capable, so the message must not claim otherwise.
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException(providerMessage, inner: null, HttpStatusCode.BadRequest)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.ModelCapabilityUnsupported)
                       && failure.Error == "The model could not be prepared for tool calling with the current tool set. Retry with tools turned off, or select a different model.");
    }

    [Test]
    public async Task RunAsync_WhenToolGrammarFailsToCompileWith500_WinsOverModelLoadFailedArm()
    {
        // The grammar arm is ordered ahead of ReportsModelLoadFailure, which matches on the status code alone: a 500
        // carrying the grammar signature must still classify as the (actionable) tool-preparation failure.
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("Failed to initialize samplers: failed to parse grammar", inner: null,
                   HttpStatusCode.InternalServerError)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelCapabilityUnsupported));
    }

    [Test]
    public async Task RunAsync_WhenToolGrammarFailureIsWrapped_MapsModelCapabilityUnsupportedFailureCategory()
    {
        // The agent framework wraps the transport exception, so the signature is only visible on an inner exception.
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("The chat client failed.",
                   new HttpRequestException("Failed to initialize samplers: failed to parse grammar", inner: null, HttpStatusCode.BadRequest))));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.FailureCategory == nameof(FailureCategory.ModelCapabilityUnsupported)
                       && failure.Error == "The model could not be prepared for tool calling with the current tool set. Retry with tools turned off, or select a different model.");
    }

    [Test]
    public async Task RunAsync_WhenUnrelatedBadRequest_StillMapsProviderUnreachable()
    {
        // The new grammar arm must not widen: an unrelated HTTP 400 keeps its previous classification.
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("invalid request payload", inner: null, HttpStatusCode.BadRequest)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ProviderUnreachable));
    }

    [Test]
    public async Task RunAsync_WhenModelLoadFailsWith500_MapsModelLoadFailedFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        // The blob path in the message must never reach the surfaced error; the status code alone drives the mapping.
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("unable to load model /root/.ollama/models/blobs/sha256-deadbeef", inner: null,
                   HttpStatusCode.InternalServerError)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelLoadFailed)
                                                                         && failure.Error == "The model could not be loaded or run on the provider.");
    }

    [Test]
    public async Task RunAsync_WhenModelForceEjectedMidRequest_MapsCancelledWithTruthfulMessage()
    {
        // An operator FORCE-eject surfaces as LlamaServerModelEjectedException, which must classify as
        // Cancelled (an operator action, not a generic provider failure) and surface the truthful "ejected" message
        // rather than a generic "provider unreachable".
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        const string EjectMessage = "The model was ejected by the operator while this request was running.";
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new LlamaServerModelEjectedException(EjectMessage)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.Cancelled)
                                                                         && failure.Error == EjectMessage);
    }

    [Test]
    public async Task RunAsync_LocalLlamaCppModel_WarmsToReadinessBeforeGenerating()
    {
        // For a local llama.cpp model the runner warms the model to readiness BEFORE the watched streaming pull
        // begins, so the cold load is never guarded by (and killed by) the stream-idle watchdog.
        var sender = new MockHubMessageSender();
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
        var runner = CreateRunner(sender, factory, providerResolver: resolver);

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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        provider.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(sender, eventDispatcher: dispatcher, providerResolver: resolver, agentUpdates: CreateUpdates("ok"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId, Arg.Is<long?>(static value => value >= 0), Arg.Any<TurnUsageTotals?>());
    }

    [Test]
    public async Task RunAsync_WhenNoLocalRuntimeWarms_ReportsNoModelReadinessDuration()
    {
        // Null, not zero: a remote or Ollama turn warmed nothing, and zero would claim it proved a warm start.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(OllamaLocalModelProvider.OllamaProviderName);

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(OllamaLocalModelProvider.OllamaProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(sender, eventDispatcher: dispatcher, providerResolver: resolver, agentUpdates: CreateUpdates("ok"));
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
        var sender = new MockHubMessageSender();
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(OllamaLocalModelProvider.OllamaProviderName);

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(OllamaLocalModelProvider.OllamaProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(sender, providerResolver: resolver, agentUpdates: CreateUpdates("ok"));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await provider.DidNotReceive().WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenModelLoadFailsWith500_DoesNotLeakBlobPath()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("unable to load model /root/.ollama/models/blobs/sha256-deadbeef", inner: null,
                   HttpStatusCode.InternalServerError)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => !failure.Error.Contains("blobs", StringComparison.Ordinal)
                                                                         && !failure.Error.Contains(".ollama", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenGenericTimeout_MapsSanitizedTimeoutMessageWithoutLeakingDetail()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        // A bare TimeoutException whose framework message names a host/path must NOT be forwarded verbatim.
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new TimeoutException("timed out reaching http://10.0.0.5:11434/api/chat")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.Timeout)
                                                                         && failure.Error == "The operation timed out."
                                                                         && !failure.Error.Contains("10.0.0.5", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenStreamIdleTimeout_KeepsThePathFreeWatchdogMessage()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        // The stream idle watchdog's own message is already a fixed, path-free constant, so it is surfaced verbatim.
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new StreamIdleTimeoutException("The response stream stalled.")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.Timeout)
                                                                         && failure.Error == "The response stream stalled.");
    }

    [Test]
    public async Task RunAsync_WhenProviderRoundIrreduciblyExceedsWindow_ClassifiesContextWindowExceeded()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        // The provider-boundary budgeter rejects a single irreducible over-window round with this typed exception; the
        // runner must classify it as ContextWindowExceeded and surface its fixed, path-free message verbatim (the bounded
        // token/window diagnostics it also carries are never surfaced).
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new ProviderContextWindowExceededException(estimatedTokens: 9000, windowTokens: 4096)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ContextWindowExceeded)
                                                                         && failure.Error == ProviderContextWindowExceededException.RoundExceedsWindowMessage
                                                                         && !failure.Error.Contains("9000", StringComparison.Ordinal)
                                                                         && !failure.Error.Contains("4096", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenUnexpected_MapsFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("boom")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.Unexpected));
    }

    [Test]
    public async Task RunAsync_WhenUnexpected_ClearsInvocationCancellationTokenSource()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("boom")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.Null(GetActiveInvocationCancellationTokenSource(runner));
    }

    [Test]
    public async Task RunAsync_AfterUnexpected_StartsSecondInvocationCleanly()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("boom")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());

        AssertEx.Null(GetActiveInvocationCancellationTokenSource(runner));
    }

    [Test]
    public async Task RunAsync_WhenAgentRuntimeMessageContainsFrameworkType_RedactsFrameworkNames()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NotSupportedException("Microsoft.Agents.AI.ChatClientAgentException: provider blew up")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.AgentRuntime)
                                                                         && !failure.Error.Contains("ChatClientAgentException", StringComparison.Ordinal)
                                                                         && !failure.Error.Contains("Microsoft.Agents.AI", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenUnexpectedMessageIsLong_TruncatesTo512Characters()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        var longMessage = new string(c: 'x', count: 600);
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException(longMessage)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        var failure = sender.SentEncryptedFailures.Single();
        AssertEx.Equal(expected: 512, failure.Error.Length);
    }

    [Test]
    public async Task RunAsync_WhenToolApprovalRequested_SendsApprovalThenResumesAfterDecision()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        // The package must offer a tool: an approval request can only surface for a tool-bearing turn, and the runner
        // only retains the segment updates it folds on resume when the offer list is non-empty (see approvalPossible).
        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home").Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(requestId, Approved: true));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the runner must re-invoke the agent threadlessly after the approval decision");
        await dispatcher.Received(1).ReportApprovalRequestedAsync(Arg.Is<ApprovalRequestPayload>(payload => payload.InvocationId == invocationId));
    }

    [Test]
    public async Task RunAsync_WhenAClientLocalToolRequiresApproval_StillFoldsTheSegmentAndResumes()
    {
        // Companion to the test above (whose offer is ApiSide, i.e. carries no approval metadata to the tool resolver
        // and so counts as approval-possible by fail-closed). This is the OTHER branch of the retention predicate: a
        // ClientLocal offer is judged on its own RequiresApproval flag, so a true one must still retain and fold the
        // segment. A predicate that narrowed to "any tool offered" vs "any tool that can be approval-wrapped" must not
        // lose this case — dropping the retention here would replay an approval resume without its assistant tool-call.
        var sender = new MockHubMessageSender();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory);

        var package = RuntimePackageBuilder.Valid().Build();
        package.AllowedTools.Add(new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = "run_in_agent_home",
            Location = ToolLocation.ClientLocal,
            RequiresApproval = true
        });

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "an approval-required ClientLocal tool must still drive the fold-and-resume segment");
    }

    [Test]
    public async Task ResolveApprovalResult_WhenRequestIdIsUnmatched_DoesNotResumeThePendingApproval()
    {
        var sender = new MockHubMessageSender();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner,
            RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home").Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        runner.ResolveApprovalResult(new ApprovalResolvedEvent($"unmatched-{Guid.NewGuid():N}", Approved: true));

        AssertEx.False(runTask.IsCompleted, "an unmatched approval response must not resume the held invocation");
        AssertEx.Equal(expected: 1, segment, "an unmatched approval response must not start the resume segment");

        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the matching approval response must resume the invocation");
    }

    [Test]
    public async Task ResolveApprovalResult_WhenDecisionIsDuplicated_FirstDecisionWins()
    {
        var sender = new MockHubMessageSender();
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ =>
            {
                segment++;
                return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
            },
            messages => resumeMessages = messages);
        var runner = CreateRunner(sender, factory);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner,
            RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home").Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(requestId, Approved: false));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(requestId, Approved: true));
        await runTask;

        var response = AssertEx.NotNull(resumeMessages)
                               .SelectMany(static message => message.Contents)
                               .OfType<ToolApprovalResponseContent>()
                               .Single();
        AssertEx.False(response.Approved, "a duplicate response must not overwrite the first approval decision");
        AssertEx.Equal(expected: 2, segment, "the duplicate response must not trigger a second resume segment");
    }

    [Test]
    public async Task RunAsync_WhenSkillApprovalIsGrantedForTheSession_SuppressesTheNextPromptAndStillAudits()
    {
        var sender = new MockHubMessageSender();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, SkillApprovalFactory(LoadSkillToolName, SkillName), approvalAuditRecorder: auditRecorder);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        // A SECOND turn in the SAME conversation, on the same skill at the same version: the memo answers it.
        await RunAsync(runner, SkillPackage(conversationId).Build());

        AssertEx.Equal(expected: 1, sender.SentApprovals.Count, "a session-scoped approval must not prompt again for the same skill in the same conversation");
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               LoadSkillToolName,
                               ToolCategory.ReadLocal,
                               "session-scope auto-approve",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
    }

    [Test]
    public async Task RunAsync_WhenSkillApprovalIsDeniedForTheSession_PromptsAgain()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, SkillApprovalFactory(LoadSkillToolName, SkillName));

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: false), ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: false));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "a DENY must never be remembered, whatever scope the operator sent");
    }

    [Test]
    public async Task RunAsync_WhenTheSkillVersionChanges_TheSessionApprovalNoLongerApplies()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, SkillApprovalFactory(LoadSkillToolName, SkillName));

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        // The operator edited the skill (or an import Replaced it) mid-conversation: same name, new content, new version.
        var secondTurn = RunAsync(runner, SkillPackage(conversationId, version: 2).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "a content change must invalidate the memo — the approval is bound to the version the operator saw");
    }

    [Test]
    public async Task RunAsync_WhenADifferentSkillResourceIsRead_TheSessionApprovalNoLongerApplies()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment switch
            {
                1 => SkillApprovalRequestUpdates(ReadSkillResourceToolName, SkillName, "reference.md"),
                3 => SkillApprovalRequestUpdates(ReadSkillResourceToolName, SkillName, "secrets.md"),
                _ => CreateUpdates("done")
            };
        });
        var runner = CreateRunner(sender, factory);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "one approval must cover ONE resource, not every resource the skill carries");
    }

    [Test]
    public async Task RunAsync_WhenRunSkillScriptIsApprovedForTheSession_PromptsAgain()
    {
        var sender = new MockHubMessageSender();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, SkillApprovalFactory(RunSkillScriptToolName, SkillName), approvalAuditRecorder: auditRecorder);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "script execution is outside the memo allow-list and must be approved every single time");

        // The other half of the audit fix: a provider-injected tool that is not in the package offer must still audit
        // under its real risk category rather than the fail-closed Unknown.
        await auditRecorder.Received(2)
                           .RecordAsync(Arg.Any<Guid?>(),
                               RunSkillScriptToolName,
                               ToolCategory.WriteExecute,
                               "approve",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
    }

    [Test]
    public async Task RunAsync_WhenTheSkillIsImported_TheSessionApprovalIsNotRemembered()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, SkillApprovalFactory(LoadSkillToolName, SkillName));

        var firstTurn = RunAsync(runner, SkillPackage(conversationId, imported: true).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId, imported: true).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "third-party skill names are attacker-chosen; a durable approval on one must not be available");
    }

    [Test]
    public async Task RunAsync_WhenFixedCustomToolApprovedForSession_SuppressesTheNextPrompt()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, CustomToolApprovalFactory(CustomToolName));

        var firstTurn = RunAsync(runner, CustomToolPackage(conversationId, isFixed: true).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        // A SECOND turn in the SAME conversation, same Fixed custom tool at the same version: the memo answers it.
        await RunAsync(runner, CustomToolPackage(conversationId, isFixed: true).Build());

        AssertEx.Equal(expected: 1, sender.SentApprovals.Count, "a session-scoped approval on a Fixed custom tool must not prompt again in the same conversation");
    }

    [Test]
    public async Task RunAsync_WhenTheCustomToolVersionChanges_TheSessionApprovalNoLongerApplies()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, CustomToolApprovalFactory(CustomToolName));

        var firstTurn = RunAsync(runner, CustomToolPackage(conversationId, version: 1, isFixed: true).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        // The operator edited the custom tool mid-conversation: same name, new version. The memo is bound to the version.
        var secondTurn = RunAsync(runner, CustomToolPackage(conversationId, version: 2, isFixed: true).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "an edit that bumps the custom tool version must invalidate the memo and re-prompt");
    }

    [Test]
    public async Task RunAsync_WhenParameterizedCustomToolApprovedForSession_PromptsAgain()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var runner = CreateRunner(sender, CustomToolApprovalFactory(CustomToolName));

        var firstTurn = RunAsync(runner, CustomToolPackage(conversationId, isFixed: false).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        // A Parameterized custom tool is once-or-deny only: a session approval must NOT be remembered, so the next turn
        // re-prompts even though the operator clicked "approve for session".
        var secondTurn = RunAsync(runner, CustomToolPackage(conversationId, isFixed: false).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "a Parameterized custom tool must never be session-approvable — one click must not grant open-ended model-chosen execution");
    }

    [Test]
    public async Task RunAsync_WhenApprovalIsRequested_TheEventSaysWhetherSessionScopeCanBeRemembered()
    {
        // The chat approval card used to decide the "Approve for this session" button from the node TOOL CATALOG, which
        // does not carry the MAF skill tools at all — so run_skill_script and imported skills offered a session scope
        // the runner would never memoize, and the click silently degraded to a plain "Once". The runner already resolves
        // the memo key before broadcasting, so it publishes that answer on the approval event itself.
        AssertEx.Equal(expected: false, await CaptureSessionScopeEligibleAsync(SkillApprovalFactory(RunSkillScriptToolName, SkillName), SkillPackage(Guid.NewGuid())),
            "script execution is outside the memo allow-list, so the card must not offer a session scope.");
        AssertEx.Equal(expected: false,
            await CaptureSessionScopeEligibleAsync(SkillApprovalFactory(LoadSkillToolName, SkillName), SkillPackage(Guid.NewGuid(), imported: true)),
            "an imported skill is never session-approvable — a per-CALL narrowing the tool catalog cannot see.");
        AssertEx.Equal(expected: true, await CaptureSessionScopeEligibleAsync(CustomToolApprovalFactory(CustomToolName), CustomToolPackage(Guid.NewGuid(), isFixed: true)),
            "a Fixed custom tool IS memoized, so the button must still be offered.");
    }

    // Runs one approval round-trip and returns the SessionScopeEligible flag the runner published with it.
    private static async Task<bool?> CaptureSessionScopeEligibleAsync(IInvocationAgentFactory factory, RuntimePackageBuilder package)
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var published = new List<ApprovalLifecyclePayload>();
        dispatcher.ReportApprovalLifecycleAsync(Arg.Do<ApprovalLifecyclePayload>(payload => published.Add(payload))).Returns(Task.CompletedTask);
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);

        var turn = RunAsync(runner, package.Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        await turn;

        return published.Single().SessionScopeEligible;
    }

    [Test]
    public async Task RunAsync_WhenTheNodeDisablesSessionScope_TheSkillApprovalIsNotRemembered()
    {
        var sender = new MockHubMessageSender();
        var conversationId = Guid.NewGuid();
        var alwaysPrompt = NodeToolApprovalPolicy.FromSettings(new NodeToolApprovalPolicySettings
        {
            DisableSkillSessionScope = true
        });
        var runner = CreateRunner(sender, SkillApprovalFactory(LoadSkillToolName, SkillName), approvalPolicy: alwaysPrompt);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true), ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await secondTurn;

        AssertEx.Equal(expected: 2, sender.SentApprovals.Count, "the operator's always-prompt switch must turn session scope off entirely");
    }

    [Test]
    public async Task RunAsync_WhenAnUnattendedRunNeedsApproval_FailsImmediatelyWithTheReason()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, SkillApprovalFactory(LoadSkillToolName, SkillName));

        // The runner's pending-approval window is FIVE MINUTES in this fixture (see CreateRunner). Completing at all is
        // the evidence that the unattended run never entered that wait; the elapsed assertion states the bound.
        var elapsed = Stopwatch.StartNew();
        await RunAsync(runner, SkillPackage(Guid.NewGuid()).AsUnattended().Build());
        elapsed.Stop();

        var failure = sender.SentEncryptedFailures.Single();
        AssertEx.Equal(nameof(FailureCategory.AgentRuntime), failure.FailureCategory);
        AssertEx.True(failure.Error.Contains($"approval required in an unattended run: {LoadSkillToolName}", StringComparison.Ordinal),
            $"the failure must name the cause, not read as a generic timeout; got '{failure.Error}'");
        AssertEx.Equal(expected: 0, sender.SentApprovals.Count, "an unattended run must not broadcast an approval request nobody can answer");
        AssertEx.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"the unattended guard must fail fast, not wait out the approval window; took {elapsed.Elapsed}");
    }

    [Test]
    public async Task RunAsync_WhenUnattendedAskUserQuestionSurfaces_ContinuesImmediatelyWithoutAnAnswer()
    {
        // The asymmetry with the approval path, asserted: an unattended APPROVAL fails the turn fast, an unattended
        // QUESTION continues with "not answered" — and neither one waits out the pending-approval window.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher, userQuestionAnswerStash: stash);

        var elapsed = Stopwatch.StartNew();
        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).AsUnattended().Build());
        elapsed.Stop();

        AssertEx.Equal(expected: 2, segment, "the turn must continue threadlessly rather than fail for an unattended run");
        AssertEx.Equal(expected: 0, sender.SentEncryptedFailures.Count, "an unanswered question must never fail the turn, unlike an unattended approval");
        await dispatcher.DidNotReceive().ReportUserQuestionAsync(Arg.Any<UserQuestionLifecyclePayload>()).ConfigureAwait(false);
        AssertEx.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
            $"the unattended run must skip the park, not wait out the 5-minute question cap; took {elapsed.Elapsed}");

        AssertEx.True(stash.TryPop("call-ask-user", out var stashed), "the model still needs a branchable result under the tool call's CallId");
        AssertEx.Contains(stashed, "\"answered\":false");
        AssertEx.Contains(stashed, "\"reason\":\"unattended\"");
    }

    [Test]
    public async Task RunAsync_WhenAskUserQuestionSurfaces_PromptsTheOperatorAndResumesWithTheAnswer()
    {
        // ask_user rides the approval seam for its BLOCKING behaviour, not for a risk verdict: the runner must present
        // the QUESTIONS (not an approve/deny card), park, then always approve so the framework executes the tool and the
        // handler returns the stashed answer as the tool result.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ =>
            {
                segment++;
                return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
            },
            messages => resumeMessages = messages);
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher, userQuestionAnswerStash: stash);

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build());
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));

        var surfaced = AssertEx.NotNull(question);
        AssertEx.Equal(AskUserTool.ToolName, surfaced.ToolName);
        AssertEx.Equal("call-ask-user", surfaced.CallId, "the question card must attach to the tool-call card the model is waiting on");
        AssertEx.Equal("Which auth method?", surfaced.Questions.Single().Question);
        AssertEx.True(surfaced.Questions.Single().Options[0].Recommended);
        AssertEx.False(runTask.IsCompleted, "the turn must hold until the operator answers");

        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent(surfaced.RequestId,
            [new UserQuestionAnswer("Which auth method?", ["OAuth device flow"], Other: null)]));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the answered question must resume the turn threadlessly");
        var response = AssertEx.NotNull(resumeMessages).SelectMany(static message => message.Contents).OfType<ToolApprovalResponseContent>().Single();
        AssertEx.True(response.Approved, "a question round-trip always approves — the answer travels as the TOOL RESULT, not as an approval verdict");

        AssertEx.True(stash.TryPop("call-ask-user", out var stashed), "the answer must be stashed under the tool call's CallId for the handler to pop");
        AssertEx.Contains(stashed, "\"answered\":true");
        AssertEx.Contains(stashed, "OAuth device flow");
    }

    [Test]
    public async Task RunAsync_WhenAskUserArgumentsAreMalformed_NeverPromptsAndTheTurnStillCompletes()
    {
        // Nothing unvalidated may reach a human: ask_user is intercepted before ToolArgumentRepairAIFunction, so the
        // runner is the first guard. A bad call is answered to the MODEL, not shown to the operator.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1
                ? AskUserRequestUpdates(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["questions"] = Array.Empty<object>()
                })
                : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher, userQuestionAnswerStash: stash);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build();

        await RunAsync(runner, package).WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.DidNotReceive().ReportUserQuestionAsync(Arg.Any<UserQuestionLifecyclePayload>());
        AssertEx.Equal(expected: 2, segment, "a malformed call must still resume the turn rather than fail it");
        AssertEx.True(stash.TryPop("call-ask-user", out var stashed));
        AssertEx.Contains(stashed, "\"answered\":false");
        AssertEx.Contains(stashed, UserQuestionResults.MalformedCallReason);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenAskUserCallHasABlankCallId_StillCompletesInsteadOfFaultingTheTurn()
    {
        // ResolveToolCallCardId resolves a blank CallId to the tool name (so the card key matches the streaming
        // lifecycle's), and a blank key would otherwise throw out of the stash and take the whole turn with it.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? BlankCallIdAskUserUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));
        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent(AssertEx.NotNull(question).RequestId, [new UserQuestionAnswer("Q?", ["A"], Other: null)]));
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task ResolveUserQuestionResult_WhenRequestIdIsUnmatched_DoesNotResumeTheParkedTurn()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build());
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));

        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent($"unmatched-{Guid.NewGuid():N}", [new UserQuestionAnswer("Q?", ["A"], Other: null)]));

        AssertEx.False(runTask.IsCompleted, "a stale or unknown answer must be a no-op, never a resume");
        AssertEx.Equal(expected: 1, segment);

        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent(AssertEx.NotNull(question).RequestId, [new UserQuestionAnswer("Q?", ["A"], Other: null)]));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the matching answer must resume the invocation");
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAQuestion_TheOperatorsThinkingTimeIsNotChargedToTheTurnDeadline()
    {
        // Regression guard: the invocation deadline (CancelAfter(InvocationTimeout)) used to keep running while a human
        // was thinking, so the operator got "300 s minus whatever the model already spent" and the 10-minute
        // MaxPendingToolCallAge cap was dead code. Here the turn budget is 1 s and the operator takes ~2 s — without the
        // re-arm the turn dies as a Timeout before the answer can land.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 1).WithAllowedTool(AskUserTool.ToolName).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "the turn must still be parked after the (unextended) invocation deadline would have fired");
        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent(AssertEx.NotNull(question).RequestId, [new UserQuestionAnswer("Q?", ["A"], Other: null)]));
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAToolApproval_TheOperatorsThinkingTimeIsNotChargedToTheTurnDeadline()
    {
        // The same deadline re-arm applies to the shipping tool-approval round-trip — a deliberate, separately reviewable
        // behaviour change to an existing feature, so it gets its own regression test.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home").Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "an operator weighing an approval must not be pre-empted by the model's own turn budget");
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAToolApprovalWhileAttached_KeepsTheFullParkBudget()
    {
        // Pins today's behaviour for the case the disconnect-grace work must NOT change: a browser is watching, so the
        // park still gets MaxPendingToolCallAge on top of the turn budget and the 1 s turn budget cannot pre-empt it.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var invocationId = Guid.NewGuid();
        var tracker = CreateAttachmentTracker();
        using var attachment = tracker.Attach(invocationId);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher, attachmentTracker: tracker);
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home").Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "an attached operator weighing an approval must keep the full park budget");
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAToolApprovalWhileDetached_FallsBackToThePlainInvocationTimeout()
    {
        // The §2-correction-1 fix. A browser that disconnected while the approval card was on screen used to buy the run
        // MaxPendingToolCallAge + InvocationTimeout (~15 min) PER PARK, holding the llama-server lease the whole time,
        // waiting for an answer that can no longer arrive. Detached, the park now gets only the turn budget.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var invocationId = Guid.NewGuid();
        var tracker = CreateAttachmentTracker();

        // Attached, then gone: exactly the "was watching, closed the tab" state. A run that NEVER attached is a
        // different case (scheduled/platform runs) and is covered by InvocationAttachmentTrackerTests.
        tracker.Attach(invocationId).Dispose();

        var runner = CreateRunner(sender, CreateFactory(_ => ApprovalRequestUpdates()), eventDispatcher: dispatcher, attachmentTracker: tracker);
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home").Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        // Nobody answers. The 1 s turn budget — not the 5 min park cap — is what ends this.
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.Received(requiredNumberOfCalls: 1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenAClientReAttachesDuringAPark_RestoresTheFullParkBudget()
    {
        // The reload case. The park started detached (short budget); the operator reloads mid-park, and from that moment
        // the turn must get the full MaxPendingToolCallAge back — otherwise a reload inherits whatever the detached park
        // left behind and the answer arrives too late.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var invocationId = Guid.NewGuid();
        var tracker = CreateAttachmentTracker();
        tracker.Attach(invocationId).Dispose();

        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory, eventDispatcher: dispatcher, attachmentTracker: tracker);
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home").Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        // The reload lands well inside the detached park's 1 s budget and re-arms the deadline via AttachmentChanged.
        using var reattached = tracker.Attach(invocationId);
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "the re-attached park must get the full budget back from the moment of re-attach");
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task CancelDetached_ClassifiesTheTurnAsCancelledNotTimedOut()
    {
        // The reaper's cancel must look like an abandoned turn, not a node timeout: the row terminalizes Cancelled with
        // no error text, exactly as a user stop does.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;
        runner.CancelDetached(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(requiredNumberOfCalls: 1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_WhenTwoToolApprovalsInOneSegment_AnswersBothOnResume()
    {
        // A parallel-tool-call turn surfaces TWO approval requests in one segment. The runner must present and
        // answer BOTH — the scalar this replaced kept only the last, so the first request dangled unanswered forever and
        // its tool call never executed. On resume, the folded history must carry a ToolApprovalResponseContent for EACH.
        var sender = new MockHubMessageSender();
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ =>
            {
                segment++;
                return segment == 1 ? TwoApprovalRequestUpdates() : CreateUpdates("done");
            },
            messages => resumeMessages = messages);
        var runner = CreateRunner(sender, factory);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home").Build());

        // The transport presents approvals one at a time, so answer each as it arrives (present-each-in-turn).
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[0].RequestId, Approved: true));
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[1].RequestId, Approved: true));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the runner must resume only after BOTH approvals resolve");
        var responses = AssertEx.NotNull(resumeMessages)
                                .SelectMany(static message => message.Contents)
                                .OfType<ToolApprovalResponseContent>()
                                .ToList();
        AssertEx.Equal(expected: 2, responses.Count, "both approval requests must receive a ToolApprovalResponseContent on resume");
    }

    [Test]
    public async Task RunAsync_WhenApprovalReEmittedWithoutCallId_PresentedOnce()
    {
        // Hardening: a CallId-less approval re-emitted across streamed chunks must dedup on its Id and be
        // presented exactly ONCE — a blank CallId must never bypass dedup (that would prompt N times for one call and
        // dangle N-1 ambiguous responses).
        var sender = new MockHubMessageSender();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? BlankCallIdApprovalUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(sender, factory);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home").Build());

        // The whole segment (both chunks) drains before approvals are presented, so a bypassed dedup would already have
        // enqueued two; wait for the single presentation, resolve it, and confirm no second one follows.
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[0].RequestId, Approved: true));
        await runTask;

        AssertEx.Equal(expected: 1, sender.SentApprovals.Count, "a CallId-less approval re-emitted across chunks must be presented exactly once");
        AssertEx.Equal(expected: 2, segment, "the run resumes after the single approval resolves");
    }

    [Test]
    public async Task RunAsync_WhenNodeDrainingBegan_RejectsNewLocalInvocation()
    {
        // A local turn admitted AFTER shutdown drain has snapshotted the active set must be rejected, never
        // become an untracked active run the drain never waits for. DrainActiveInvocationsAsync fences local admission;
        // a subsequent local (loopback) RunAsync is rejected with a classified failure and never streams.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: CreateUpdates("should-not-stream"));

        // Drain with nothing active: returns immediately but latches the draining fence.
        var drained = await runner.DrainActiveInvocationsAsync(TimeSpan.FromSeconds(1));
        AssertEx.True(drained, "an empty drain completes immediately");

        var package = RuntimePackageBuilder.Valid()
                                           .WithInvocationId(Guid.NewGuid())
                                           .WithRequestedCapability(LocalChatLoopbackDefaults.RequestedCapability)
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var singleAgentFactory = CreateFactory(CreateUpdates("single-agent-should-not-run"));
        var orchestrationFactory = CreateOrchestrationFactory(OrchestrationTextUpdates("Hello", " world"), out var sessionRef);
        var runner = CreateRunner(sender, singleAgentFactory, eventDispatcher: dispatcher, orchestrationAgentFactory: orchestrationFactory);
        var package = RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build();

        await RunPlainAsync(runner, package);

        await orchestrationFactory.Received(1).CreateAsync(Arg.Any<OrchestrationAgentDefinition>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>());
        await singleAgentFactory.DidNotReceive().CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>());
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        await dispatcher.Received(1).ReportInvocationStreamChunkAsync(package.InvocationId, " world");
        AssertEx.Equal(expected: 1, sender.SentCompletions.Count);
        AssertEx.Equal("Hello world", sender.SentCompletions[0].FinalContent);
        AssertEx.True(sessionRef.Value!.Disposed, "The orchestration session must be disposed after the run.");
    }

    [Test]
    public async Task RunAsync_WhenNoOrchestrationSpec_TakesSingleAgentPath()
    {
        // The single-agent regression guard: a package without a spec must NOT touch the orchestration factory.
        var sender = new MockHubMessageSender();
        var singleAgentFactory = CreateFactory(CreateUpdates("Hello", " world"));
        var orchestrationFactory = Substitute.For<IOrchestrationAgentFactory>();
        var runner = CreateRunner(sender, singleAgentFactory, orchestrationAgentFactory: orchestrationFactory);
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        await orchestrationFactory.DidNotReceive().CreateAsync(Arg.Any<OrchestrationAgentDefinition>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>());
        await singleAgentFactory.Received(1).CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>());
        AssertEx.Equal("Hello world", sender.SentCompletions[0].FinalContent);
    }

    [Test]
    public async Task RunAsync_WhenOrchestrationSurfacesApproval_RoundTripsAndResumesOnSession()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        // The gated session blocks its post-approval text/terminal on ApprovalGate, which only RespondToApprovalAsync
        // completes — so "done" + completion are reached ONLY if the runner actually resumes the held session by key.
#pragma warning disable CA2000 // The runner owns disposal of the session via its `await using`; the test asserts Disposed.
        var gatedSession = new FakeOrchestrationRunSession(session => OrchestrationGatedApprovalThenText(session, "call-1", "run_in_agent_home", "done"));
#pragma warning restore CA2000
        var orchestrationFactory = CreateOrchestrationFactory(gatedSession, out var sessionRef);
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, orchestrationAgentFactory: orchestrationFactory);
        var invocationId = Guid.NewGuid();

        var runTask = RunPlainAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithOrchestrationSpec(SampleSpec()).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(requestId, Approved: true));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportApprovalRequestedAsync(Arg.Is<ApprovalRequestPayload>(payload => payload.InvocationId == invocationId));
        // The approval card must name the tool, not the opaque correlation id (single-agent UX parity).
        AssertEx.Contains(sender.SentApprovals.Single().Description, "run_in_agent_home");
        AssertEx.Equal(expected: 1, sessionRef.Value!.ApprovalResponses.Count);
        AssertEx.True(sessionRef.Value.ApprovalResponses[0].Approved, "An approved decision must be forwarded to the session as approved=true.");
        AssertEx.Equal("call-1", sessionRef.Value.ApprovalResponses[0].RequestId);
        // Reaching this asserts the gated post-approval portion streamed — i.e. the resume drove the held session.
        AssertEx.Equal("done", sender.SentCompletions.Single().FinalContent);
    }

    [Test]
    public async Task RunAsync_WhenOrchestrationFails_SendsInvocationFailedWithoutLeakingRawDetail()
    {
        var sender = new MockHubMessageSender();
        // The raw MAF executor detail must NOT reach the client (logged server-side only); the client sees a constant.
        var orchestrationFactory = CreateOrchestrationFactory(OrchestrationFailure("workflow boom /secret/internal/path"), out _);
        var runner = CreateRunner(sender, orchestrationAgentFactory: orchestrationFactory);
        var package = RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build();

        await RunPlainAsync(runner, package);

        AssertEx.Equal(expected: 0, sender.SentCompletions.Count);
        AssertEx.Equal(expected: 1, sender.SentFailures.Count);
        AssertEx.Equal(package.InvocationId, sender.SentFailures[0].InvocationId);
        AssertEx.False(sender.SentFailures[0].Error.Contains("secret", StringComparison.Ordinal),
            "The raw orchestration failure detail must not be forwarded to the client.");
        AssertEx.Contains(sender.SentFailures[0].Error, "Orchestration run failed");
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenResultResolved_ReturnsResult()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        var task = runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}");
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        var approvalRequestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(approvalRequestId, Approved: true));
        await AssertEx.EventuallyAsync(() => sender.SentToolCalls.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentToolCalls.Single().RequestId;
        runner.ResolveToolCallResult(new ToolCallResultEvent
        {
            RequestId = requestId,
            Result = "done"
        });

        AssertEx.Equal(approvalRequestId, requestId);
        await dispatcher.Received(1).ReportApprovalRequestedAsync(Arg.Is<ApprovalRequestPayload>(payload => payload.InvocationId == invocationId
                                                                                                            && payload.RequestId == requestId
                                                                                                            && payload.Description.Contains("test-tool", StringComparison.Ordinal)));
        await dispatcher.Received(1).ReportToolCallRequestedAsync(Arg.Is<ToolCallRequestPayload>(payload => payload.InvocationId == invocationId
                                                                                                            && payload.RequestId == requestId
                                                                                                            && payload.ToolName == "test-tool"
                                                                                                            && payload.Parameters == "{}"));
        AssertEx.Equal("done", await task);
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenToolReturnsError_ThrowsWorkerToolCallException()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender);
        var invocationId = Guid.NewGuid();

        var task = runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}");
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        var approvalRequestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(approvalRequestId, Approved: true));
        await AssertEx.EventuallyAsync(() => sender.SentToolCalls.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentToolCalls.Single().RequestId;
        runner.ResolveToolCallResult(new ToolCallResultEvent
        {
            RequestId = requestId,
            Result = string.Empty,
            Error = "approval timeout"
        });

        var exception = await AssertEx.ThrowsAsync<WorkerToolCallException>(() => task);
        AssertEx.Contains(exception.Message, "approval timeout");
    }

    [Test]
    public async Task CancelAll_WhenPendingToolCallsExist_CancelsOutstandingCalls()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender);

        var pendingCall = runner.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}");
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        runner.CancelAll();

        var exception = await AssertEx.ThrowsAsync<WorkerToolCallException>(() => pendingCall);
        AssertEx.Contains(exception.Message, "timed out waiting for a result", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task RunAsync_WhenToolBridgeFails_MapsAgentToolCallCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new WorkerToolCallException("approve-job", "approval timeout")));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("approve-job").Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.AgentToolCall));
    }

    [Test]
    public void InvocationFailedPayload_SerializesFailureCategoryAsPascalCaseString()
    {
        var payload = new InvocationFailedPayload
        {
            InvocationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MessageId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Error = "Invocation timed out after 30 seconds.",
            FailureCategory = nameof(FailureCategory.Timeout)
        };

        var json = JsonSerializer.Serialize(payload);
        var roundTrip = JsonSerializer.Deserialize<InvocationFailedPayload>(json);
        var deserialized = AssertEx.NotNull(roundTrip);

        AssertEx.Contains(json, "\"FailureCategory\":\"Timeout\"");
        AssertEx.Contains(json, "\"MessageId\":\"22222222-2222-2222-2222-222222222222\"");
        AssertEx.Equal(nameof(FailureCategory.Timeout), deserialized.FailureCategory);
        AssertEx.Equal(payload.MessageId, deserialized.MessageId);
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenTimedOut_ThrowsTaskCanceledException()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 1
        });
        SetMaxPendingToolCallAge(runner, TimeSpan.Zero);

        var exception = await AssertEx.ThrowsAsync<WorkerToolCallException>(() => runner.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}"));
        AssertEx.Contains(exception.Message, "timed out waiting for a result", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenTimedOut_EmitsCompletedLifecycleWithError()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 1
        });
        SetMaxPendingToolCallAge(runner, TimeSpan.Zero);
        var invocationId = Guid.NewGuid();

        // requiresApproval: false guarantees the Requested lifecycle fires before the result-wait timeout, so the
        // timeout path must emit a matching Completed (IsError=true) to clear the UI card.
        await AssertEx.ThrowsAsync<WorkerToolCallException>(() =>
            Bridge(runner).ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: false));

        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.InvocationId == invocationId
            && payload.ToolName == "test-tool"
            && payload.Phase == ToolCallLifecyclePhase.Requested));
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.InvocationId == invocationId
            && payload.ToolName == "test-tool"
            && payload.Phase == ToolCallLifecyclePhase.Completed
            && payload.IsError
            && !string.IsNullOrWhiteSpace(payload.Result)));
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenApprovalNotRequired_SkipsApprovalAndExecutes()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        var task = Bridge(runner).ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: false);
        await AssertEx.EventuallyAsync(() => sender.SentToolCalls.Count == 1, TimeSpan.FromSeconds(5));

        AssertEx.Equal(expected: 0, sender.SentApprovals.Count);

        var requestId = sender.SentToolCalls.Single().RequestId;
        runner.ResolveToolCallResult(new ToolCallResultEvent
        {
            RequestId = requestId,
            Result = "tool-output"
        });

        var result = await task;
        AssertEx.Equal("tool-output", result);

        await dispatcher.Received().ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Requested
            && !payload.RequiresApproval
            && payload.ToolName == "test-tool"));
        await dispatcher.Received().ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Completed
            && payload.Result == "tool-output"
            && !payload.IsError));
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenApprovalRequired_SendsApprovalBeforeExecuting()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender);
        var invocationId = Guid.NewGuid();

        var task = Bridge(runner).ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: true);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        AssertEx.Equal(expected: 0, sender.SentToolCalls.Count);

        var approvalRequestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(approvalRequestId, Approved: true));
        await AssertEx.EventuallyAsync(() => sender.SentToolCalls.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentToolCalls.Single().RequestId;
        runner.ResolveToolCallResult(new ToolCallResultEvent
        {
            RequestId = requestId,
            Result = "tool-output"
        });

        var result = await task;
        AssertEx.Equal("tool-output", result);
    }

    [Test]
    public async Task CleanupStaleToolCalls_RemovesEntriesOlderThanMaxAge()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 5
        });

        var task = runner.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}");
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.CleanupStaleToolCalls(TimeSpan.Zero);

        var exception = await AssertEx.ThrowsAsync<WorkerToolCallException>(() => task);
        AssertEx.Contains(exception.Message, "timed out during cleanup", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task RunAsync_WhenCompletes_CleansUpStaleToolCalls()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 1
        });
        var pendingToolCall = runner.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}");
        AgePendingToolCalls(runner, TimeSpan.FromMinutes(2));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        var exception = await AssertEx.ThrowsAsync<WorkerToolCallException>(() => pendingToolCall);
        AssertEx.Contains(exception.Message, "timed out during cleanup", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task RunAsync_WhenFaults_CleansUpStaleToolCalls()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender,
            workerOptions: new WorkerNodeOptions
            {
                NodeName = "worker",
                MaxResponseSizeMb = 10,
                MaxPendingToolCallAgeMinutes = 1
            },
            agentUpdates: ThrowingUpdates());
        var pendingToolCall = runner.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}");
        AgePendingToolCalls(runner, TimeSpan.FromMinutes(2));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        var exception = await AssertEx.ThrowsAsync<WorkerToolCallException>(() => pendingToolCall);
        AssertEx.Contains(exception.Message, "timed out during cleanup", StringComparison.OrdinalIgnoreCase);
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
    public async Task RunAsync_WhenStreamStallsBeyondIdleTimeout_MapsTimeoutFailure()
    {
        var sender = new MockHubMessageSender();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Retry disabled so the single stalled attempt trips the 1s inter-chunk idle watchdog promptly (with retry on,
        // the same stall would be retried before finally surfacing as a timeout).
        var resilience = new ProviderStreamResilience(Options.Create(new ProviderResilienceOptions
            {
                RetryEnabled = false,
                CircuitBreakerEnabled = false
            }),
            TimeProvider.System,
            NullLogger<ProviderStreamResilience>.Instance);
        var runner = CreateRunner(sender,
            CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)),
            providerStreamResilience: resilience);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 300, toolCallSeconds: 30, streamIdleSeconds: 1).Build();

        await RunAsync(runner, package).WaitAsync(TimeSpan.FromSeconds(15));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Timeout));
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_DuringActiveInvocation_UsesPackageToolCallTimeoutOverNodeAge()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Node-global pending age is 5 minutes; only the package's 1s ToolCallTimeoutSeconds keeps the result wait short.
        var runner = CreateRunner(sender,
            workerOptions: new WorkerNodeOptions
            {
                NodeName = "worker",
                MaxResponseSizeMb = 10,
                MaxPendingToolCallAgeMinutes = 5
            },
            agentUpdates: BlockingUpdates(gate.Task, started));
        var invocationId = Guid.NewGuid();
        var package = RuntimePackageBuilder.Valid()
                                           .WithInvocationId(invocationId)
                                           .WithTimeout(invocationSeconds: 300, toolCallSeconds: 1, streamIdleSeconds: 60)
                                           .Build();

        var runTask = RunAsync(runner, package);
        await started.Task;

        // If the result wait honoured the 5-minute node age instead of the 1s package timeout, this would not fault
        // within 15s and the WaitAsync would surface a TimeoutException (failing the expected WorkerToolCallException).
        var toolCall = Bridge(runner).ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: false);
        var exception = await AssertEx.ThrowsAsync<WorkerToolCallException>(() => toolCall.WaitAsync(TimeSpan.FromSeconds(15)));
        AssertEx.Contains(exception.Message, "timed out waiting for a result", StringComparison.OrdinalIgnoreCase);

        gate.TrySetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RunAsync_WhenHistoryStillExceedsBudgetAfterTruncation_FailsCleanlyBeforeAnyProviderCall()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        // A capacity this tiny cannot be satisfied by ANY history (even a single protected turn), so the budgeter's
        // two-pass truncation cannot bring the estimate under budget: ExceedsBudget stays true and the runner must
        // hard-stop BEFORE ever touching the agent factory (no agentUpdates are ever consumed).
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
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

    [Test]
    public async Task RunAsync_WhenTheApprovalResumeOverrunsOnReasoningAlone_FailsTheTurnWithoutPass4()
    {
        // The boundary Pass 4 is measured against. On the approval resume the runner re-budgets the FOLDED segment, whose
        // assistant message carries the model's reasoning. Every message of that history is inside the protected recent
        // window, so Passes 1-3 have nothing to reclaim: with the pass off the turn dies before the resumed provider call.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ReasoningHeavyApprovalRequestUpdates(reasoningChars: 100_000) : CreateUpdates("done");
        });
        var runner = CreateRunner(sender,
            factory,
            eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                DefaultContextTokens = 4096,
                ReservedOutputTokenFloor = 0,
                StripProtectedReasoning = false
            });
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("run_in_agent_home").Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ReasoningHeavyApprovalRequestUpdates(reasoningChars: 100_000) : CreateUpdates("done");
        });
        var runner = CreateRunner(sender,
            factory,
            eventDispatcher: dispatcher,
            contextBudgetOptions: new ConversationContextBudgetOptions
            {
                // StripProtectedReasoning is deliberately NOT set: this grades the SHIPPED default, so flipping the pass
                // back off fails here rather than silently reintroducing the failed turn above.
                DefaultContextTokens = 4096,
                ReservedOutputTokenFloor = 0
            });
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("run_in_agent_home").Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
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
    public async Task RunAsync_WhenLaunchedWindowExceedsTheConfiguredDefault_BudgetsAgainstTheRealWindow()
    {
        // The regression: the launched window was only ever allowed to SHRINK the configured default, so a model
        // running a 64k window was budgeted at the 8k default and long conversations failed before any provider call.
        // A default of 1 token cannot admit even a single protected turn, so this run can only survive if the effective
        // window replaced it.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var runner = CreateRunner(sender,
            CreateGenerationSpyFactory(() => generationCalls++),
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var runner = CreateRunner(sender,
            CreateGenerationSpyFactory(() => generationCalls++),
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var runner = CreateRunner(sender,
            CreateGenerationSpyFactory(() => generationCalls++),
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var policy = new MinimumEffectiveContextAdmissionPolicy(requiredContextTokens: 8192);
        var warmFailure = new HttpRequestException("provider leaked /private/model/path", inner: null, HttpStatusCode.InternalServerError);
        var runner = CreateRunner(sender,
            CreateGenerationSpyFactory(() => generationCalls++),
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var generationCalls = 0;
        var runner = CreateRunner(sender,
            CreateGenerationSpyFactory(() => generationCalls++),
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

    /// <summary>
    ///     A resolver whose model is served by the llama.cpp provider (the only one the runner warms) reporting
    ///     <paramref name="effectiveContextTokens" /> as its launched per-slot context window.
    /// </summary>
    private static ILocalModelProviderResolver CreateLlamaCppResolver(int? effectiveContextTokens, Exception? warmFailure = null)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        if (warmFailure is not null)
        {
            provider.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException(warmFailure));
        }

        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(effectiveContextTokens is { } effective
                    ? new LocalModelRuntimeInfo(effective)
                    : null));

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        return resolver;
    }

    /// <summary>
    ///     A llama.cpp resolver whose launched per-slot window DIFFERS per model, so a turn that runs two models can
    ///     assert each send was sized against the window of the model that actually served it.
    /// </summary>
    private static ILocalModelProviderResolver CreatePerModelLlamaCppResolver(IReadOnlyDictionary<string, int> windowsByModel)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(windowsByModel.TryGetValue(callInfo.Arg<string>(), out var window)
                    ? new LocalModelRuntimeInfo(window)
                    : null));

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        return resolver;
    }

    [Test]
    public async Task RunAsync_WhenModelRoutesToCloud_DoesNotWarmALocalProvider()
    {
        // Regression: a cloud model id (e.g. "gpt-5.6-terra") has no entry in the model→provider map, so the resolver
        // defaults it to the local llama.cpp provider. The warm path then tried to cold-load llama-server and failed with
        // "model not installed" for what is actually a cloud send. The warm must honour the same cloud-vs-local routing
        // decision the send makes, so a cloud-selected model warms nothing local.
        var sender = new MockHubMessageSender();

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.IsCloudProviderSelected(Arg.Any<string?>()).Returns(true);

        var runner = CreateRunner(sender,
            providerResolver: resolver,
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        // Force the fallback branch: the requested model never verifies against Ollama, so ResolveModelAsync falls
        // back to the node's default model (Ollama:ChatModel = "qwen3.5:0.8b", wired in CreateRunner) and reports the
        // substitution.
        capabilityReporter.VerifyOllamaAndModelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, capabilityReporter: capabilityReporter, agentUpdates: CreateUpdates("ok"));
        var package = RuntimePackageBuilder.Valid().WithModel("some-unverifiable-model").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.ModelSubstituted
            && payload.Message.Contains("some-unverifiable-model", StringComparison.Ordinal)
            && payload.Message.Contains("qwen3.5:0.8b", StringComparison.Ordinal)));
    }

    // ---- reasoning effort `auto` -------------------------------------------------------------------------------

    /// <summary>
    ///     THE byte-identical-default proof. The dispatcher is registered through a factory that THROWS, so a turn
    ///     whose effort is anything but <c>auto</c> proves it was never RESOLVED — not merely never invoked — because
    ///     the runner opens no scope on that path at all. Registered scoped, exactly as the composition root does.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("none")]
    [Arguments("on")]
    [Arguments("minimal")]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    [Arguments("xhigh")]
    public async Task Dispatch_WhenEffortIsNotAuto_DispatcherIsNeverResolvedOrInvoked(string? reasoningEffort)
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            reasoningEffortDispatcherFactory: static _ => throw new InvalidOperationException("The dispatcher must never be resolved on a non-auto turn."));
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort(reasoningEffort).Build();

        await RunAsync(runner, package);

        // No throw means no resolve. The definition must also be exactly what it was before this feature existed:
        // the authored effort, the resolved model, and untouched sampling.
        var definitionBuilt = AssertEx.NotNull(built);
        AssertEx.True(string.Equals(reasoningEffort, definitionBuilt.ReasoningEffort, StringComparison.Ordinal),
            "the authored effort must reach the definition untouched");
        AssertEx.Equal("qwen3.5:0.8b", definitionBuilt.ModelId);
        AssertEx.Null(definitionBuilt.Sampling?.MaxOutputTokens);
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    /// <summary>
    ///     The other half of the proof: the same throwing factory IS reached on an <c>auto</c> turn, so the test above
    ///     cannot pass by simply never wiring the dispatcher at all.
    /// </summary>
    [Test]
    public async Task Dispatch_WhenEffortIsAuto_ResolvesTheDispatcherFromTheTurnScope()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender,
            agentUpdates: CreateUpdates("ok"),
            reasoningEffortDispatcherFactory: static _ => throw new InvalidOperationException("resolved-on-auto"));
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").Build();

        // The resolution failure surfaces as the turn's failure, which is what proves the resolve happened.
        await RunAsync(runner, package);

        AssertEx.True(sender.SentEncryptedFailures.Count > 0, "an auto turn must reach the dispatcher registration");
    }

    [Test]
    public async Task Dispatch_WhenTierIsNormalAndNoSwap_EmitsNoNotice()
    {
        // The common case. A notice on every ordinary turn would be noise, so NORMAL with the model unchanged is
        // silent — the effort still changes, it is just not worth interrupting the reader for.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Normal, "qwen3.5:0.8b", "medium", ReasoningDispatchReasons.Balanced));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").Build();

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 1, stub.Invocations);
        AssertEx.Equal("medium", AssertEx.NotNull(built).ReasoningEffort, "the dispatched effort replaces `auto` before the definition is built");
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    [Test]
    public async Task Dispatch_WhenModelSwapped_EmitsEffortDispatchedNotice()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        var definitionBuilt = AssertEx.NotNull(built);
        AssertEx.Equal("qwen3-1.7b", definitionBuilt.ModelId, "the turn runs on the model the dispatcher chose");
        AssertEx.Equal("low", definitionBuilt.ReasoningEffort);
        AssertEx.Null(definitionBuilt.Sampling?.MaxOutputTokens, "no tier caps the send's output");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.EffortDispatched
            && payload.Detail == ReasoningDispatchReasons.ShortTurn
            && payload.Message.Contains("qwen3-1.7b", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Dispatch_WhenModelSwapped_RecordsTheServedModelOnTheInvocationState()
    {
        // The invocation state is seeded with the model the PACKAGE named, and BOTH the persisted message row and the
        // run envelope's provider attribution are read from it. A swapped turn that leaves it alone is recorded, and
        // measured, against a model that never saw the turn — the fast model's tokens and latency land on the big
        // model's row.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("ok"), eventDispatcher: dispatcher, reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportServedModelAsync(package.InvocationId, "qwen3-1.7b");
    }

    [Test]
    [Arguments("auto")]
    [Arguments("medium")]
    public async Task Dispatch_WhenTheModelIsNotSwapped_NeverRecordsAServedModel(string reasoningEffort)
    {
        // Both silent shapes: an `auto` turn the dispatcher chose not to swap, and an ordinary turn that never reaches
        // the dispatcher at all. The seeded model is already correct on each, and a redundant report would rewrite the
        // state on every turn for nothing.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3.5:0.8b", "low", ReasoningDispatchReasons.FastModelUnset));
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("ok"), eventDispatcher: dispatcher, reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort(reasoningEffort).Build();

        await RunAsync(runner, package);

        await dispatcher.DidNotReceive().ReportServedModelAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task Dispatch_WhenTheFallbackRerunsOnTheOriginalModel_NeverRecordsTheFastModelAsServed()
    {
        // The other half of the rule: the fast model did not serve this turn, the original one did, and the seeded
        // state already names it. Reporting the fast model here would put a model that produced nothing on the row.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 2, observed.Count, "the re-run must have happened, or this proves nothing");
        await dispatcher.DidNotReceive().ReportServedModelAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task Dispatch_WhenSwappedSendFailsAfterFirstToken_StillEmitsExactlyOneEffortDispatchedNotice()
    {
        // A swapped turn withholds its notice until the send resolves, precisely so a fallback cannot leave the reader
        // with two contradictory rows. When the send streams and THEN fails there is no fallback to announce — and the
        // turn used to end with no effort notice at all, which is the one outcome the ruling forbids. The notice names
        // the model that actually served; the failure itself is reported as on any other turn.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed, emitATokenBeforeFailing: true),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 1, observed.Count, "a turn that has already streamed must not be re-run");
        AssertEx.True(sender.SentEncryptedFailures.Count > 0, "the turn still fails");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EffortDispatched
            && payload.Detail == ReasoningDispatchReasons.ShortTurn
            && payload.Message.Contains("qwen3-1.7b", StringComparison.Ordinal)));
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    [Test]
    public async Task Dispatch_WhenSwapAdmitted_DisposesTheReservationBeforeTheScope()
    {
        // Declaration order is load-bearing: `using` disposes in REVERSE order, so the scope is declared first and
        // released LAST — after the ledger reservation produced by the CapacityService that lives inside it. Reversing
        // the two would tear down the scoped services while a live reservation still referred to them.
        var sender = new MockHubMessageSender();
        using var reservation = new SpyReservation();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn, reservation: reservation));
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("ok"), reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.True(reservation.DisposedAtTicks.HasValue, "the ledger reservation must be released");
        AssertEx.True(stub.DisposedAtTicks.HasValue, "the per-turn scope must be torn down");
        AssertEx.True(reservation.DisposedAtTicks!.Value <= stub.DisposedAtTicks!.Value,
            "the ledger reservation must be released before the scope that produced it");
    }

    [Test]
    public async Task Dispatch_WhenSwappedSendFailsBeforeFirstToken_RetriesOnOriginalModelAtLowAndEmitsFastModelUnavailable()
    {
        // The fast model went away between the capacity probe and the send. Nothing reached the client, so the turn
        // re-runs once on the model it was authorised for — and COMPLETES. A failed turn here would break the
        // dispatcher's "never fails a turn" contract.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 2, observed.Count, "exactly one re-run");
        AssertEx.Equal("qwen3-1.7b", observed[0].ModelId);
        AssertEx.Equal("qwen3.5:0.8b", observed[1].ModelId, "the re-run uses the model the turn was authorised for");
        AssertEx.Equal("low", observed[1].ReasoningEffort, "the re-run keeps the tier the dispatcher chose and gives up only the model");
        AssertEx.Empty(sender.SentEncryptedFailures);
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EffortDispatched && payload.Detail == ReasoningDispatchReasons.FastModelUnavailable));
        // ONE notice for the turn. A swapped turn stays silent until the send has resolved precisely so a fallback
        // does not leave the reader with two contradictory "reasoning effort resolved" rows for one answer.
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    [Test]
    public void AddModelReadiness_SumsEveryWarmAndStaysNullUntilOneHappens()
    {
        // Fixed arithmetic, no clock: the runner test above measures the real two-warm path but reads a wall-clock
        // threshold, which a last-write-wins regression could still clear on a slow worker. This one cannot.
        var stream = new InvocationRunner.StreamState();

        AssertEx.Null(stream.ModelReadinessDurationMs, "no warm happened, and null is what says so — zero would claim a proven warm start");

        stream.AddModelReadiness(1_500d);
        AssertEx.Equal(expected: 1_500d, stream.ModelReadinessDurationMs);

        stream.AddModelReadiness(2_500d);
        AssertEx.Equal(expected: 4_000d, stream.ModelReadinessDurationMs, "the second warm adds to the first rather than replacing it");
    }

    [Test]
    public async Task Dispatch_WhenTheFallbackWarmsASecondTime_ReportsBothWarmsAsOneReadinessTotal()
    {
        // Two local warms in one turn: the dispatched fast model is warmed, its send fails before first output, and the
        // original model is warmed again for the re-run. The whole-turn clock contains BOTH, so assigning the second
        // warm's duration charged the turn only half the cold start it actually paid — the readiness total has to sum.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        // A measurable warm: the summed pair clears the floor below, a single warm cannot. Delays only ever run long
        // under load, so the discrimination holds on a slow machine.
        provider.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.Delay(WarmDelay));

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            eventDispatcher: dispatcher,
            providerResolver: resolver,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 2, observed.Count, "exactly one re-run");
        await provider.Received(2).WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId,
                            Arg.Is<long?>(static value => value >= TwoWarmFloorMs),
                            Arg.Any<TurnUsageTotals?>());
    }

    [Test]
    public async Task Dispatch_WhenSwappedSendFailsAfterFirstToken_DoesNotRetry()
    {
        // Once a token has reached the client there is nothing to re-run into: the turn fails exactly as any other
        // mid-stream failure does.
        var sender = new MockHubMessageSender();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed, emitATokenBeforeFailing: true),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 1, observed.Count, "a turn that has already streamed must not be re-run");
        AssertEx.True(sender.SentEncryptedFailures.Count > 0);
    }

    [Test]
    public async Task Dispatch_WhenFallbackRerunsOnOriginalModel_HoldsNoFastReservation()
    {
        // The fast reservation books the small model's bytes and one of the loaded-process slots. Carrying it into the
        // re-run double-books the ledger against a model that is no longer being loaded, and can starve the original
        // model's own spawn on a node at the process cap — the exact failure the re-run exists to avoid.
        var sender = new MockHubMessageSender();
        using var reservation = new SpyReservation();
        var observed = new List<InvocationAgentDefinition>();
        long? secondRunStartedAt = null;
        var factory = CreateModelRoutedFactory("qwen3-1.7b", observed);
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn, reservation: reservation));
        var spyFactory = Substitute.For<IInvocationAgentFactory>();
        spyFactory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo =>
                  {
                      if (observed.Count == 1)
                      {
                          secondRunStartedAt ??= Stopwatch.GetTimestamp();
                      }

                      return factory.CreateAsync(callInfo.Arg<InvocationAgentDefinition>(), callInfo.Arg<CancellationToken>());
                  });

        var runner = CreateRunner(sender,
            invocationAgentFactory: spyFactory,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.True(reservation.DisposedAtTicks.HasValue, "the fast reservation must be released");
        AssertEx.True(secondRunStartedAt.HasValue, "the re-run must have started");
        AssertEx.True(reservation.DisposedAtTicks!.Value <= secondRunStartedAt!.Value, "the fast reservation must be released BEFORE the re-run begins");
        // Dispose is idempotent at its source, so the `using` at turn end running a second time must not throw.
        AssertEx.Equal(expected: 2, reservation.DisposeCount, "released in the catch, then again by the turn-end using");
    }

    [Test]
    public async Task Dispatch_WhenTheExternalOriginalBindingChangesBeforeTheFallback_RefusesTheFallbackSend()
    {
        // The turn is authorised for a declared-LOCAL external model and swaps to a node-local fast model. The fast
        // send fails before any output, and while it fails the operator flips that connection to Cloud. The fallback
        // re-runs the ORIGINAL model inside the pin scope opened before the swap — so unless that scope also carries
        // the ORIGINAL model's pin, the fallback falls through to the transport's weaker unpinned rule and honours a
        // Local->Cloud escalation the pin exists to refuse.
        var sender = new MockHubMessageSender();
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateExternalFallbackFactory("qwen3-1.7b",
                registry,
                recorder,
                observed,
                onFastModelSend: () => registry.Replace(ExternalProviderTestData.Connection(locality: ExternalProviderLocality.Cloud),
                    ExternalProviderTestData.Model())),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub,
            externalProviderRegistry: registry);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithModel(ExternalProviderTestData.ModelId).WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 2, observed.Count, "the fallback must have been attempted");
        AssertEx.Empty(recorder.Requests, "the prompt must never reach the changed endpoint");
        AssertEx.True(sender.SentEncryptedFailures.Count > 0, "a refused fallback fails the turn rather than sending");
    }

    [Test]
    public async Task Dispatch_WhenTheExternalOriginalBindingIsUnchanged_TheFallbackStillSends()
    {
        // The other half of the pin: an untouched binding must not be turned into a refusal by pinning the original
        // model as well. The fallback sends, on the endpoint the turn was authorised for, and the turn completes.
        var sender = new MockHubMessageSender();
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateExternalFallbackFactory("qwen3-1.7b", registry, recorder, observed),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub,
            externalProviderRegistry: registry);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithModel(ExternalProviderTestData.ModelId).WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 1, recorder.Requests.Count, "the fallback must reach the pinned endpoint");
        AssertEx.Equal("http://127.0.0.1:18099/v1/chat/completions", recorder.LastRequest.Uri?.AbsoluteUri);
        AssertEx.Empty(sender.SentEncryptedFailures);
    }

    [Test]
    public async Task RunAsync_WhenTheTurnNeverSwapsModels_ResolvesExactlyOnePin()
    {
        // Byte-identical guard for the pin set. Resolving BOTH the dispatched and the original model must not widen
        // the ambient set on any turn that did not swap: a non-`auto` turn and an `auto` turn that stayed on its own
        // model both name the same id twice, and the resolver de-duplicates.
        var nonAutoPins = await RunNoSwapTurnAndCapturePinsAsync("medium");
        var autoPins = await RunNoSwapTurnAndCapturePinsAsync("auto");

        foreach (var pins in new[]
                 {
                     nonAutoPins,
                     autoPins
                 })
        {
            AssertEx.Equal(expected: 1, pins.Count, "a turn that never swapped must pin exactly the model it runs");
            AssertEx.Equal(ExternalProviderTestData.ModelId, pins[0].ModelId);
        }
    }

    /// <summary>
    ///     One completed turn on an external model that never swaps, returning the pins in force at its send. The
    ///     `auto` case dispatches to the SAME model, so it is dispatched but not swapped.
    /// </summary>
    private static async Task<IReadOnlyList<ExternalProviderBindingPin>> RunNoSwapTurnAndCapturePinsAsync(string effort)
    {
        var sender = new MockHubMessageSender();
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var pinsAtSend = new List<IReadOnlyList<ExternalProviderBindingPin>>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Normal,
            ExternalProviderTestData.ModelId,
            "medium",
            ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateExternalFallbackFactory("qwen3-1.7b", registry, recorder, [], pinsAtSend: pinsAtSend),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub,
            externalProviderRegistry: registry);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithModel(ExternalProviderTestData.ModelId).WithReasoningEffort(effort).Build());

        AssertEx.Empty(sender.SentEncryptedFailures);
        AssertEx.Equal(expected: 1, pinsAtSend.Count, "the turn must have sent exactly once");
        return pinsAtSend[0];
    }

    [Test]
    public async Task RunAsync_WhenAToolReturnsTheDisabledMarker_SurfacesAToolDisabledNoticeOncePerTool()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: ToolDisabledUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.ToolDisabled
            && payload.Detail == "test-tool"
            && payload.Message.Contains("test-tool", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_WithTheNodeSettingUnset_LeavesTheRelevanceScopeInactiveAndNeverReadsTheCoreSet()
    {
        // The byte-identical pin for the shipped default. The settings read itself is unconditional (it is the LEFT
        // operand of the &&), so a read-count assertion would be wrong; the CORE SET is what the runner touches only
        // when the decision came out active, which makes it the honest negative observable.
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        var runner = CreateRunner(new MockHubMessageSender(), toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        coreSet.DidNotReceive().GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenTheNodeSettingIsOn_SeedsAnActiveRelevanceScopeFromTheStoredValue()
    {
        // The behaviour change this plan exists for: the decision now comes from the node setting, read per turn.
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        coreSet.GetCoreToolNames().Returns(new HashSet<string>(StringComparer.Ordinal));
        var runner = CreateRunner(new MockHubMessageSender(),
            toolRelevanceRead: static _ => Task.FromResult(true),
            toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        coreSet.Received(1).GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenTheAgentOptsOut_KeepsTheScopeInactiveEvenWithTheNodeSettingOn()
    {
        // Guards the operand ORDER of `enabled && !package.DisableToolRelevanceFilter` against a later edit: the
        // per-agent opt-out must still win over the global switch.
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        var runner = CreateRunner(new MockHubMessageSender(),
            toolRelevanceRead: static _ => Task.FromResult(true),
            toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build() with { DisableToolRelevanceFilter = true });

        coreSet.DidNotReceive().GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenCancelledWhileReadingTheRelevanceSetting_EndsTheTurnCancelled()
    {
        // The read takes the INVOCATION token, not the caller's: an operator Cancel (or the whole-turn watchdog) trips
        // that one. Reading on the caller token would leave a stalled settings read uncancellable, which is the failure
        // this pins. Gates only, no sleeps.
        var sender = new MockHubMessageSender();
        var gateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender,
            toolRelevanceRead: async cancellationToken =>
            {
                gateReached.TrySetResult();
                return await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            });

        var runTask = RunAsync(runner, package);
        // Bounded: if the runner ever stops reading the setting, this must fail fast rather than park the whole run.
        await gateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Cancel(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Cancelled));
    }

    [Test]
    public async Task RunAsync_WhenTheStoredValueChangesBetweenTurns_ThePreviouslyBuiltRunnerPicksItUp()
    {
        // ONE runner, two turns. Two separately constructed runners would pass even if the value were captured at
        // construction, which is exactly the no-restart bug this rules out.
        var enabled = false;
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        coreSet.GetCoreToolNames().Returns(new HashSet<string>(StringComparer.Ordinal));
        var runner = CreateRunner(new MockHubMessageSender(),
            // ReSharper disable once AccessToModifiedClosure - reading the CURRENT value per turn is the point.
            toolRelevanceRead: _ => Task.FromResult(enabled),
            toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        coreSet.DidNotReceive().GetCoreToolNames();

        enabled = true;
        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());

        coreSet.Received(1).GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenTheRelevanceHopHeldToolsBack_EmitsOneToolsFilteredNoticeAfterTheFirstAssistantText()
    {
        // The drain the hop cannot do itself: it leaves the pair on the ambient scope several awaited frames below the
        // runner, and the runner turns it into the one counts-only sentence at the end of the FIRST segment — after
        // the assistant text, exactly as HistoryTruncated does.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var chunksSentWhenTheNoticeFired = -1;
        dispatcher.When(static call => call.ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.ToolsFiltered)))
                  .Do(_ => chunksSentWhenTheNoticeFired = sender.SentEncryptedChunks.Count(static chunk => chunk.Kind == EncryptedChunkEnvelopeV1.ContentKind));
        var runner = CreateRunner(sender,
            eventDispatcher: dispatcher,
            agentUpdates: ToolsFilteredUpdates(hidden: 5, total: 12, "Hello"),
            toolRelevanceRead: static _ => Task.FromResult(true));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        // The sentence is asserted verbatim: the ChatNoticeRow test renders whatever the server sends, so this is the
        // only place the wording is pinned.
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.ToolsFiltered
            && payload.Message == "5 of 12 tools were held back from this turn to save context; the assistant can list and use them by calling list_tools."));
        AssertEx.True(chunksSentWhenTheNoticeFired >= 1, "The notice must follow the first assistant text, never precede it.");
    }

    [Test]
    public async Task RunAsync_WhenNoToolWasHeldBack_EmitsNoToolsFilteredNotice()
    {
        // Both silent shapes: the shipped default (the filter never engages) and an ACTIVE filter whose decision hid
        // nothing — "0 of N tools were held back" is a sentence no user should ever see.
        var shippedDefaultDispatcher = Substitute.For<IWorkerEventDispatcher>();
        await RunAsync(CreateRunner(new MockHubMessageSender(), eventDispatcher: shippedDefaultDispatcher, agentUpdates: CreateUpdates("Hello")),
            RuntimePackageBuilder.Valid().Build());

        var nothingHiddenDispatcher = Substitute.For<IWorkerEventDispatcher>();
        await RunAsync(CreateRunner(new MockHubMessageSender(),
                eventDispatcher: nothingHiddenDispatcher,
                agentUpdates: ToolsFilteredUpdates(hidden: 0, total: 12, "Hello"),
                toolRelevanceRead: static _ => Task.FromResult(true)),
            RuntimePackageBuilder.Valid().Build());

        foreach (var dispatcher in new[]
                 {
                     shippedDefaultDispatcher,
                     nothingHiddenDispatcher
                 })
        {
            await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.ToolsFiltered));
        }
    }

    [Test]
    public async Task RunAsync_WhenASecondSegmentFiltersAgain_KeepsTheNoticeToTheFirstSegmentsCounts()
    {
        // The drain is inside `if (isFirstSegment …)`, so an approval resume that rebinds the tool array and computes a
        // second decision must NOT post a second "tools were held back" line under output the user has already read.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1
                ? ToolsFilteredApprovalRequestUpdates(hidden: 5, total: 12)
                : ToolsFilteredUpdates(hidden: 3, total: 9, "done");
        });
        var runner = CreateRunner(sender,
            factory,
            eventDispatcher: dispatcher,
            toolRelevanceRead: static _ => Task.FromResult(true));
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("run_in_agent_home").Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "The resume segment must actually run, or this proves nothing.");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.ToolsFiltered
            && payload.Message.StartsWith("5 of 12 tools", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_OnACompletedTurn_ReportsTheBudgetsToolSchemaTokenEstimateBeforeTheTerminalReport()
    {
        // The columns are populated on the SHIPPED default (tool relevance off): the budget counts the schema either
        // way, and that is what makes the before/after measurable at all. Cumulative across rounds, maximum per round.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var order = new List<string>();
        dispatcher.When(static call => call.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())).Do(_ => order.Add("estimate"));
        dispatcher.When(static call => call.ReportInvocationCompletedAsync(Arg.Any<Guid>(),
                      Arg.Any<int?>(),
                      Arg.Any<int?>(),
                      Arg.Any<int?>(),
                      Arg.Any<int?>(),
                      Arg.Any<long?>(),
                      Arg.Any<string?>(),
                      Arg.Any<InvocationThroughput?>()))
                  .Do(_ => order.Add("completed"));
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: ToolSchemaBudgetedUpdates(640, 300));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportToolSchemaTokensAsync(package.InvocationId, 940L, 640);
        AssertEx.True(order.SequenceEqual(["estimate", "completed"], StringComparer.Ordinal),
            "The estimate must reach the invocation state BEFORE the terminal report, or the envelope row is written without it.");
    }

    [Test]
    public async Task RunAsync_WhenTheTurnIsCancelled_StillReportsTheToolSchemaTokenEstimate()
    {
        // The easiest path to lose, and the most interesting one to keep: a turn that was stopped is exactly where an
        // operator asks what the tool schema was costing.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var order = new List<string>();
        dispatcher.When(static call => call.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())).Do(_ => order.Add("estimate"));
        dispatcher.When(static call => call.ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>())).Do(_ => order.Add("terminal"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => ToolSchemaBudgetedThenParkedUpdates(640, started, cancellationToken)), eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();

        var runTask = RunAsync(runner, package);
        await started.Task;
        runner.Cancel(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportToolSchemaTokensAsync(package.InvocationId, 640L, 640);
        AssertEx.True(order.SequenceEqual(["estimate", "terminal"], StringComparer.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenTheTurnFails_StillReportsTheToolSchemaTokenEstimate()
    {
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var order = new List<string>();
        dispatcher.When(static call => call.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())).Do(_ => order.Add("estimate"));
        dispatcher.When(static call => call.ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>())).Do(_ => order.Add("terminal"));
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: ToolSchemaBudgetedThenThrowingUpdates(640));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportToolSchemaTokensAsync(package.InvocationId, 640L, 640);
        AssertEx.True(order.SequenceEqual(["estimate", "terminal"], StringComparer.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenTheEstimateReportThrows_StillCompletesTheTurn()
    {
        // Telemetry never decides an outcome. The report runs immediately before the terminal report, so an unguarded
        // throw on the completed path would fall into the catch below it and turn a finished turn into a failed one.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())
                  .Returns<Task>(static _ => throw new InvalidOperationException("the estimate seam broke"));
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: CreateUpdates("Hello"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenTheProviderRepeatsTheSameToolCall_EmitsOneRequestedEventPerDistinctCall()
    {
        // A re-emitted FunctionCallContent used to pay a fresh JsonSerializer.Serialize + dispatch + SignalR frame every
        // time. Downstream absorbed the duplicates (the frontend reducer keys tool cards on the call id), but each repeat
        // also displaced a real event from InvocationResumeRegistry's capped tool history.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: RepeatedToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        // Three emissions of call-1 (the same instance twice, then an equal-but-distinct arguments dictionary — the two
        // shapes a provider can re-emit) collapse to one event.
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Requested
            && payload.ToolCallId == "call-1"
            && payload.Arguments != null
            && payload.Arguments.Contains("README.md", StringComparison.Ordinal)));

        // A genuinely distinct call still reports — the dedup is per call id, not per tool.
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Requested
            && payload.ToolCallId == "call-2"));

        // The Completed side still resolves the tool name from what the Requested side recorded.
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Completed
            && payload.ToolCallId == "call-1"
            && payload.ToolName == "test-tool"));
    }

    [Test]
    public async Task RunAsync_WhenARepeatedToolCallChangesItsArguments_EmitsBothRequestedEvents()
    {
        // The guard must never swallow a payload change: the second event is genuinely different on the wire, and the
        // frontend reducer takes the newest arguments for a card.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: ChangedArgumentsToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(2).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Requested
            && payload.ToolCallId == "call-1"));
    }

    [Test]
    public async Task RunAsync_WhenTheProviderStreamsTwoSameNameCallsWithABlankCallId_PairsEachResultWithItsOwnCall()
    {
        // Microsoft.Extensions.AI rejects a NULL CallId in both content constructors (10.9.0), so the id-less shape a
        // provider can actually produce is the empty string — and both halves of the call then carried it. Every
        // consumer that correlates a call with its result drops a blank id (NodeChatPartAccumulator refuses one
        // outright), so an id-less call was recorded nowhere and a caller-managed continuation never replayed it. Two
        // such calls to one tool also collapsed onto a single card.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var lifecycle = new List<ToolCallLifecyclePayload>();
        dispatcher.ReportToolCallLifecycleAsync(Arg.Do<ToolCallLifecyclePayload>(lifecycle.Add)).Returns(Task.CompletedTask);
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: CallIdLessToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        var requested = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Requested).ToList();
        var completed = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Completed).ToList();

        AssertEx.Equal(expected: 2, requested.Count, "Two calls to one tool are two calls, not one card overwritten by the second.");
        AssertEx.Equal(expected: 2, completed.Count);
        AssertEx.NotEqual(requested[0].ToolCallId, requested[1].ToolCallId, "A second call to the same tool must take its own surrogate id.");
        AssertEx.True(completed.TrueForAll(static payload => !string.IsNullOrEmpty(payload.ToolCallId)),
            "An empty id is dropped by every consumer that correlates a result with its call.");

        // Same ids, in call order: results arrive in call order for a provider that emits none.
        AssertEx.Equal(requested[0].ToolCallId, completed[0].ToolCallId);
        AssertEx.Equal(requested[1].ToolCallId, completed[1].ToolCallId);
        AssertEx.Equal("a.txt", completed[0].Result);
        AssertEx.Equal("b.txt", completed[1].Result);
        AssertEx.True(completed.TrueForAll(static payload => string.Equals(payload.ToolName, "test-tool", StringComparison.Ordinal)),
            "The Completed side still resolves the tool name from what the Requested side recorded.");
        AssertEx.Equal("test-tool", requested[0].ToolCallId, "The FIRST id-less call keeps the tool name, which is the id the approval card already resolves.");
    }

    [Test]
    public async Task RunAsync_WhenABlankCallIdToolIsCalledTwiceInSequence_DoesNotReuseTheClosedSurrogate()
    {
        // The sequential shape, with IDENTICAL arguments — the one that reads exactly like a streamed re-emission.
        // Reusing the finished call's key swallowed the second call outright here, and merged the first call's
        // arguments with the last result when the arguments differed. A surrogate is retired by its result.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var lifecycle = new List<ToolCallLifecyclePayload>();
        dispatcher.ReportToolCallLifecycleAsync(Arg.Do<ToolCallLifecyclePayload>(lifecycle.Add)).Returns(Task.CompletedTask);
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: SequentialCallIdLessToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        var requested = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Requested).ToList();
        var completed = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Completed).ToList();

        AssertEx.Equal(expected: 2, requested.Count, "The second call is a call, not a re-emission of the finished one.");
        AssertEx.Equal("test-tool", requested[0].ToolCallId, "The first id-less call keeps the tool name the approval card resolves.");
        AssertEx.Equal("test-tool#2", requested[1].ToolCallId, "A closed surrogate is retired, never reused.");

        AssertEx.Equal(expected: 2, completed.Count);
        AssertEx.Equal("test-tool", completed[0].ToolCallId);
        AssertEx.Equal("test-tool#2", completed[1].ToolCallId);
        AssertEx.Equal("first", completed[0].Result);
        AssertEx.Equal("second", completed[1].Result);
    }

    [Test]
    public async Task RunAsync_ReportsTheLastFinishReasonTheProviderStreamed()
    {
        // The benchmark ranking reads this off the terminal snapshot to tell a truncated answer from a complete one, so
        // an intermediate tool-call segment must not be what the turn is recorded as having stopped for: last wins.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: FinishReasonUpdates(ChatFinishReason.ToolCalls, ChatFinishReason.Length));
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
        // Fail open, never infer: a provider that reports nothing leaves the field null rather than being labelled
        // "stop", which would make an unmeasured turn indistinguishable from a measured complete one.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: FinishReasonUpdates(first: null, last: null));
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: TimingsUpdates());
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: TwoRequestTimingsUpdates());
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
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(sender, eventDispatcher: dispatcher, agentUpdates: FinishReasonUpdates(ChatFinishReason.Stop, ChatFinishReason.Stop));
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

    private static bool IsClose(double? actual, double expected) =>
        actual is { } value && Math.Abs(value - expected) < 0.0001;

    /// <summary>
    ///     One stream carrying TWO terminal readings, i.e. what a tool-calling turn looks like on the wire: the second
    ///     request re-sends the conversation, so its prompt is mostly served from the prompt cache.
    /// </summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> TwoRequestTimingsUpdates()
    {
        await Task.Yield();
        yield return TimingsUpdate("first", cacheN: 0, promptN: 40, promptMs: 12.5, predictedN: 89, predictedMs: 200.25);
        yield return TimingsUpdate("second", cacheN: 2346, promptN: 283, promptMs: 456.5, predictedN: 1720, predictedMs: 1011.5);
    }

    private static AgentResponseUpdate TimingsUpdate(string text, int cacheN, int promptN, double promptMs, int predictedN, double predictedMs)
    {
        var json = string.Create(CultureInfo.InvariantCulture,
            $"{{\"id\":\"chunk\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\","
            + $"\"choices\":[{{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{{}}}}],"
            + $"\"timings\":{{\"cache_n\":{cacheN},\"prompt_n\":{promptN},\"prompt_ms\":{promptMs},"
            + $"\"predicted_n\":{predictedN},\"predicted_ms\":{predictedMs}}}}}");
        var chunk = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))
                    ?? throw new InvalidOperationException("The chunk fixture did not deserialize.");
        return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, text)
        {
            RawRepresentation = chunk
        });
    }

    /// <summary>
    ///     A two-chunk stream shaped like a real llama-server one: the timings ride the FINAL chunk only, on the OpenAI
    ///     SDK object reachable through the agent update's raw representation. Deliberately the real types rather than a
    ///     stub, because the double hop (agent update -> chat update -> OpenAI chunk) is exactly what a package bump can
    ///     break without any compile error.
    /// </summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> TimingsUpdates()
    {
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "part one");

        const string finalChunk = """
                                  {"id":"chunk","object":"chat.completion.chunk","created":1,"model":"m",
                                   "choices":[{"index":0,"finish_reason":"stop","delta":{}}],
                                   "timings":{"cache_n":7,"prompt_n":123,"prompt_ms":456.5,"predicted_n":89,"predicted_ms":1011.5}}
                                  """;
        var chunk = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(finalChunk))
                    ?? throw new InvalidOperationException("The chunk fixture did not deserialize.");
        yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, " part two")
        {
            RawRepresentation = chunk,
            FinishReason = ChatFinishReason.Stop
        });
    }

    /// <summary>
    ///     Two streamed updates carrying <paramref name="first" /> then <paramref name="last" />. Real streams carry a
    ///     finish reason only on the terminal update of each segment, so the accumulator keeps the last NON-NULL one:
    ///     a trailing null must not erase the reason an earlier segment reported.
    /// </summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> FinishReasonUpdates(ChatFinishReason? first, ChatFinishReason? last)
    {
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "part one")
        {
            FinishReason = first
        };
        yield return new AgentResponseUpdate(ChatRole.Assistant, " part two")
        {
            FinishReason = last
        };
    }

    /// <summary>
    ///     The SEQUENTIAL id-less shape: one call, its result, then the same tool called again with byte-identical
    ///     arguments and its own result. Distinct dictionary instances carrying equal content, which is exactly what a
    ///     re-emitted chunk of ONE call also looks like — the closed surrogate is what tells the two apart.
    /// </summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> SequentialCallIdLessToolCallUpdates()
    {
        for (var call = 0; call < 2; call++)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent(string.Empty,
                    "test-tool",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = "README.md"
                    })
            });
            await Task.Yield();

            yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionResultContent(string.Empty, call == 0 ? "first" : "second")
            });
            await Task.Yield();
        }
    }

    /// <summary>
    ///     Two calls to the SAME tool with a blank CallId, then their two results — also blank, exactly as
    ///     Microsoft.Extensions.AI copies the call's id onto the result. Blank rather than null because both content
    ///     constructors throw on null (10.9.0), which makes the empty string the only id-less shape a provider can
    ///     actually stream.
    /// </summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> CallIdLessToolCallUpdates()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(string.Empty,
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "a"
                }),
            new FunctionCallContent(string.Empty,
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "b"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent(string.Empty, "a.txt"),
            new FunctionResultContent(string.Empty, "b.txt")
        });
        await Task.Yield();
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RepeatedToolCallUpdates()
    {
        var repeated = new FunctionCallContent("call-1",
            "test-tool",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = "README.md"
            });

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            repeated
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            repeated
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "README.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-1", "ok")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-2",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "CHANGELOG.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-2", "ok")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ChangedArgumentsToolCallUpdates()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "README.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "CHANGELOG.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    // Two rounds of the SAME tool (matching call ids) both returning ToolArgumentRepairAIFunction's structured
    // "tool_disabled" marker — the real trigger is 3 consecutive invalid calls inside AI.Agent, but the runner's
    // notice logic only inspects the wire shape of the result, so a hand-built marker exercises it without depending
    // on AI.Agent internals.
    private static async IAsyncEnumerable<AgentResponseUpdate> ToolDisabledUpdates()
    {
        const string disabledMarker =
            "{\"error\":\"tool_disabled\",\"reason\":\"Tool 'test-tool' was disabled for this run after repeated invalid-argument calls.\",\"hint\":\"Do not call this tool again during this run; continue without it.\"}";

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "test-tool")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-1", disabledMarker)
        });
        await Task.Yield();

        // A second call to the now-disabled tool returns the identical marker; the notice must fire only once.
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-2", "test-tool")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-2", disabledMarker)
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    private static InvocationRunner CreateRunner(MockHubMessageSender sender,
        IInvocationAgentFactory? invocationAgentFactory = null,
        IRuntimePackageValidator? validator = null,
        ICapabilityReporter? capabilityReporter = null,
        WorkerNodeOptions? workerOptions = null,
        IWorkerEventDispatcher? eventDispatcher = null,
        IAsyncEnumerable<AgentResponseUpdate>? agentUpdates = null,
        IOrchestrationAgentFactory? orchestrationAgentFactory = null,
        ILocalModelProviderResolver? providerResolver = null,
        IActiveCloudChatClientFactory? activeCloudFactory = null,
        IProviderStreamResilience? providerStreamResilience = null,
        ConversationContextBudgetOptions? contextBudgetOptions = null,
        UserQuestionAnswerStash? userQuestionAnswerStash = null,
        IToolApprovalAuditRecorder? approvalAuditRecorder = null,
        IToolApprovalPolicy? approvalPolicy = null,
        IInvocationAttachmentTracker? attachmentTracker = null,
        Func<CancellationToken, Task<bool>>? toolRelevanceRead = null,
        IToolRelevanceCoreSet? toolRelevanceCoreSet = null,
        Func<IServiceProvider, IReasoningEffortDispatcher>? reasoningEffortDispatcherFactory = null,
        IExternalProviderRegistry? externalProviderRegistry = null)
    {
        var resolvedContextBudgetOptions = contextBudgetOptions ?? new ConversationContextBudgetOptions();
        var resolvedFactory = invocationAgentFactory ?? CreateFactory(agentUpdates ?? CreateUpdates("ok"));
        var resolvedOrchestrationFactory = orchestrationAgentFactory ?? Substitute.For<IOrchestrationAgentFactory>();

        var resolvedValidator = validator ?? Substitute.For<IRuntimePackageValidator>();
        if (validator is null)
        {
            resolvedValidator.Validate(Arg.Any<RuntimePackage>(), Arg.Any<bool>()).Returns(RuntimePackageValidationResult.Success);
        }

        var resolvedCapabilityReporter = capabilityReporter ?? Substitute.For<ICapabilityReporter>();
        if (capabilityReporter is null)
        {
            resolvedCapabilityReporter.VerifyOllamaAndModelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        }

        // Default to the Ollama provider so the existing tests keep exercising the VerifyOllamaAndModelAsync preflight
        // path (a non-Ollama provider intentionally bypasses it). Tests can pass their own resolver to cover routing.
        var resolvedProviderResolver = providerResolver ?? Substitute.For<ILocalModelProviderResolver>();
        if (providerResolver is null)
        {
            resolvedProviderResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                                    .Returns(Task.FromResult(OllamaLocalModelProvider.OllamaProviderName));
        }

        // Default: no cloud provider selected (IsCloudProviderSelected returns false), so the warm path resolves the
        // local provider exactly as before. A cloud-routing test injects one that returns true to prove the warm is skipped.
        var resolvedActiveCloudFactory = activeCloudFactory ?? Substitute.For<IActiveCloudChatClientFactory>();

        var resolvedEventDispatcher = eventDispatcher ?? Substitute.For<IWorkerEventDispatcher>();

        // Default to a real resilience with retry/backoff/breaker at defaults: success-path tests establish the stream
        // on the first attempt, so the wrapper is transparent. Resilience-specific behaviour is covered directly in
        // ProviderStreamResilienceTests; a test can still inject its own to exercise the wired path.
        var resolvedProviderStreamResilience = providerStreamResilience
                                               ?? new ProviderStreamResilience(Options.Create(new ProviderResilienceOptions()),
                                                   TimeProvider.System,
                                                   NullLogger<ProviderStreamResilience>.Instance);

        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Ollama:ChatModel"] = "qwen3.5:0.8b"
                            })
                            .Build();

        var resolvedWorkerOptions = workerOptions ?? new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 5
        };
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithMaxResponseSizeMb(resolvedWorkerOptions.MaxResponseSizeMb)
                                                     .WithMaxPendingToolCallAgeMinutes(resolvedWorkerOptions.MaxPendingToolCallAgeMinutes)
                                                     // Tool relevance stays OFF by default here: every existing assertion in this file is a
                                                     // byte-identical-offer assertion. The notice-drain tests pass their own enabled read.
                                                     .WithToolRelevanceEnabled(toolRelevanceRead ?? (static _ => Task.FromResult(false)))
                                                     .Build();

        // One registry instance shared by the runner and all three collaborators, exactly as the DI graph wires it: a
        // second copy would let a call be registered in one dictionary and resolved against another.
        var pendingToolCallRegistry = new PendingToolCallRegistry();

        return new InvocationRunner(new Lazy<IHubMessageSender>(() => sender),
            new Lazy<IWorkerEventDispatcher>(() => resolvedEventDispatcher),
            resolvedFactory,
            resolvedOrchestrationFactory,
            new EnvelopeCryptoService(new AesGcmNodeAeadCipher()),
            resolvedValidator,
            resolvedCapabilityReporter,
            resolvedProviderResolver,
            new LocalRuntimeWarmer(resolvedProviderResolver, resolvedActiveCloudFactory, new FakeModelTrustResolver(), NullLogger<LocalRuntimeWarmer>.Instance),
            Substitute.For<IDeadLetterStore>(),
            resolvedProviderStreamResilience,
            new ConversationContextBudgeter(new HeuristicTokenEstimator(), Options.Create(resolvedContextBudgetOptions)),
            Options.Create(resolvedContextBudgetOptions),
            Options.Create(new ProviderResilienceOptions()),
            Options.Create(new AgentToolPipelineOptions()),
            Options.Create(new ProviderCallBudgetOptions()),
            toolRelevanceCoreSet ?? new FakeToolRelevanceCoreSet(),
            configuration,
            runtimeSettings,
            Options.Create(new SpawnOptions()),
            pendingToolCallRegistry,
            new ToolApprovalCoordinator(new Lazy<IHubMessageSender>(() => sender),
                new Lazy<IWorkerEventDispatcher>(() => resolvedEventDispatcher),
                pendingToolCallRegistry,
                approvalAuditRecorder ?? Substitute.For<IToolApprovalAuditRecorder>(),
                approvalPolicy ?? NodeToolApprovalPolicy.FromSettings(settings: null),
                userQuestionAnswerStash ?? new UserQuestionAnswerStash(TimeProvider.System),
                runtimeSettings,
                NullLogger<ToolApprovalCoordinator>.Instance),
            new ApiToolCallBridge(new Lazy<IHubMessageSender>(() => sender),
                new Lazy<IWorkerEventDispatcher>(() => resolvedEventDispatcher),
                pendingToolCallRegistry,
                runtimeSettings),
            new InvocationLifecycleTracker(attachmentTracker ?? CreateAttachmentTracker(), pendingToolCallRegistry, runtimeSettings),
            externalProviderRegistry ?? new FakeExternalProviderRegistry(),
            // The runner opens ONE scope per `auto` turn and resolves the dispatcher from it. The default provider
            // registers nothing, so a test that never sends `auto` proves — by not throwing — that no scope is used.
            CreateScopeFactory(reasoningEffortDispatcherFactory),
            NullLogger<InvocationRunner>.Instance);
    }

    /// <summary>
    ///     A real container holding only the reasoning-effort dispatcher, registered SCOPED exactly as the composition
    ///     root registers it — so a test drives the same resolve-from-a-scope path the product does.
    /// </summary>
    private static IServiceScopeFactory CreateScopeFactory(Func<IServiceProvider, IReasoningEffortDispatcher>? dispatcherFactory)
    {
        var services = new ServiceCollection();
        if (dispatcherFactory is not null)
        {
            services.AddScoped(dispatcherFactory);
        }

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task RunAsync(InvocationRunner runner,
        RuntimePackage package,
        IInvocationGenerationAdmissionPolicy? generationAdmissionPolicy = null,
        CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.Create(package,
            Guid.NewGuid(),
            epochVersion: 1,
            new byte[32],
            generationAdmissionPolicy);
        await runner.RunAsync(context, cancellationToken);
    }

    private static async Task RunPlainAsync(InvocationRunner runner, RuntimePackage package, CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
        await runner.RunAsync(context, cancellationToken);
    }

    // A REAL tracker, not a substitute: the deadline tests turn on its ref-counting and its AttachmentChanged event
    // firing exactly on the zero boundaries, and a stub that got either wrong would make them pass for the wrong reason.
    private static InvocationAttachmentTracker CreateAttachmentTracker()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        return new InvocationAttachmentTracker(new Lazy<IWorkerEventDispatcher>(() => dispatcher), TimeProvider.System);
    }

    // The active turn's linked source now lives on InvocationLifecycleTracker; reach it through the runner's own
    // tracker instance so these tests still observe the source the turn under test armed.
    private static CancellationTokenSource? GetActiveInvocationCancellationTokenSource(InvocationRunner runner)
    {
        var tracker = GetPrivateField(runner, "_lifecycleTracker");
        var field = AssertEx.NotNull(tracker.GetType().GetField("_invocationCancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic));
        return (CancellationTokenSource?)field.GetValue(tracker);
    }

    // The pending-call wait budget is read once at construction by four singletons — the runner, ToolApprovalCoordinator,
    // ApiToolCallBridge and InvocationLifecycleTracker (whose park deadline adds it on top of the turn budget) — so
    // shortening only one of them would silently miss the path under test.
    private static void SetMaxPendingToolCallAge(InvocationRunner runner, TimeSpan maxPendingToolCallAge)
    {
        SetPrivateField(runner, "_maxPendingToolCallAge", maxPendingToolCallAge);
        SetPrivateField(GetPrivateField(runner, "_toolApprovalCoordinator"), "_maxPendingToolCallAge", maxPendingToolCallAge);
        SetPrivateField(GetPrivateField(runner, "_apiToolCallBridge"), "_maxPendingToolCallAge", maxPendingToolCallAge);
        SetPrivateField(GetPrivateField(runner, "_lifecycleTracker"), "_maxPendingToolCallAge", maxPendingToolCallAge);
    }

    // The per-tool RequiresApproval overload now lives on ApiToolCallBridge; the runner only implements the
    // approval-gated IInvocationRunner signature. Reach the collaborator the runner was built with so these tests keep
    // exercising the SAME instance (and therefore the same shared pending-call registry) the turn would use.
    private static ApiToolCallBridge Bridge(InvocationRunner runner)
    {
        return (ApiToolCallBridge)GetPrivateField(runner, "_apiToolCallBridge");
    }

    private static object GetPrivateField(object target, string name)
    {
        var field = AssertEx.NotNull(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic));
        return AssertEx.NotNull(field.GetValue(target));
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        var field = AssertEx.NotNull(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic));
        field.SetValue(target, value);
    }

    private static void AgePendingToolCalls(InvocationRunner runner, TimeSpan age)
    {
        var pendingToolCallsField = AssertEx.NotNull(typeof(InvocationRunner).GetField("_pendingToolCalls", BindingFlags.Instance | BindingFlags.NonPublic));
        var pendingToolCalls = (IEnumerable)AssertEx.NotNull(pendingToolCallsField.GetValue(runner));

        foreach (var pendingToolCallEntry in pendingToolCalls)
        {
            var pendingToolCall = AssertEx.NotNull(pendingToolCallEntry.GetType().GetProperty("Value")?.GetValue(pendingToolCallEntry));
            var createdAtField = AssertEx.NotNull(pendingToolCall.GetType().GetField("<CreatedAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic));
            createdAtField.SetValue(pendingToolCall, DateTimeOffset.UtcNow - age);
        }
    }

    [Test]
    [Arguments(32768)]
    [Arguments(4096)]
    public async Task Dispatch_WhenTierIsFast_LeavesTheSendsSamplingUntouched(int effectiveContextTokens)
    {
        // No tier caps the output. A FAST turn differs from a non-`auto` turn only in its effort, on a wide window as
        // much as on a narrow one — which is what keeps both context budgeters' output RESERVATION where it was and
        // keeps a small window from being starved by a reservation a non-`auto` turn would never have made.
        var sender = new MockHubMessageSender();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3.5:0.8b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens),
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").Build();

        await RunAsync(runner, package);

        AssertEx.Null(AssertEx.NotNull(built).Sampling?.MaxOutputTokens);
        AssertEx.Empty(sender.SentEncryptedFailures);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task Dispatch_WhenTheFallbackRerunsOnTheOriginalModel_UsesTheOriginalModelsWindow()
    {
        // The swapped model was warmed at 4096 and the turn policy was sized against THAT window. Re-running the
        // original model on it would measure a long conversation against the fast model's window and drop history the
        // authorised model would have kept — and would thread 4096 as the num_ctx of a process launched at 32768.
        var sender = new MockHubMessageSender();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            providerResolver: CreatePerModelLlamaCppResolver(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["qwen3-1.7b"] = 4096,
                ["qwen3.5:0.8b"] = 32768
            }),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 2, observed.Count, "exactly one re-run");
        AssertEx.Equal(expected: 4096, observed[0].EffectiveContextTokens, "the swapped send is sized against the fast model's window");
        AssertEx.Equal(expected: 32768, observed[1].EffectiveContextTokens, "the re-run is sized against the window of the model it actually runs on");
        AssertEx.Empty(sender.SentEncryptedFailures);
    }

    [Test]
    public async Task Dispatch_WhenASwappedTurnWouldOfferTools_DoesNotRetry()
    {
        // The re-run re-enters RunSingleAgentAsync, which owns the tool-relevance drain and its ToolsFiltered notice.
        // The dispatcher's own gate refuses a swap on any tool-bearing turn, so this shape is unreachable through the
        // real dispatcher; the guard makes that dependency explicit rather than load-bearing by coincidence.
        var sender = new MockHubMessageSender();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(sender,
            invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().WithAllowedTool("test-tool").Build());

        AssertEx.Equal(expected: 1, observed.Count, "a tool-bearing turn must not be re-run");
        AssertEx.True(sender.SentEncryptedFailures.Count > 0);
    }

    // ---- reasoning-effort dispatch helpers -----------------------------------------------------------------------

    /// <summary>
    ///     A dispatcher that answers with one fixed decision. Records how many times it was invoked so a test can
    ///     assert both "never" and "exactly once".
    /// </summary>
    private sealed class StubReasoningEffortDispatcher(ReasoningDispatchDecision decision) : IReasoningEffortDispatcher, IDisposable
    {
        public int Invocations { get; private set; }

        /// <summary>Set when the SCOPE that produced this instance is torn down — this is a scoped registration.</summary>
        public long? DisposedAtTicks { get; private set; }

        public Task<ReasoningDispatchDecision> DispatchAsync(ReasoningDispatchRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(decision);
        }

        public void Dispose()
        {
            DisposedAtTicks ??= Stopwatch.GetTimestamp();
        }
    }

    /// <summary>A stand-in for the capacity ledger reservation, recording WHEN it was released.</summary>
    private sealed class SpyReservation : IDisposable
    {
        public int DisposeCount { get; private set; }

        public long? DisposedAtTicks { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            DisposedAtTicks ??= Stopwatch.GetTimestamp();
        }
    }

    private static ReasoningDispatchDecision Decision(ReasoningTier tier,
        string model,
        string effort,
        string reasonCode,
        IDisposable? reservation = null)
    {
        return new ReasoningDispatchDecision(tier, model, effort, MaxOutputTokens: null, SupportsThinking: true, ReasoningBudgetEnforceable: true, reasonCode, reservation);
    }

    /// <summary>
    ///     A factory whose stream fails for ONE model and succeeds for every other, recording each definition it was
    ///     asked to build. That is how a swapped send is made to fail while the original model's re-run completes.
    /// </summary>
    private static IInvocationAgentFactory CreateModelRoutedFactory(string failingModel, List<InvocationAgentDefinition> observed, bool emitATokenBeforeFailing = false)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   observed.Add(definition);
                   var fails = string.Equals(definition.ModelId, failingModel, StringComparison.Ordinal);
                   Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updates = fails
                       ? _ => emitATokenBeforeFailing ? TokenThenThrowingUpdates() : ThrowingUpdates()
                       : _ => CreateUpdates("ok");

                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updates, onSessionObserved: null),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    /// <summary>
    ///     Like <see cref="CreateModelRoutedFactory" />, except every non-failing model sends through the REAL
    ///     <see cref="ExternalOpenAiChatClient" /> over a recording transport — so the pin check that refuses a changed
    ///     binding is the production one, running inside the runner's own ambient pin scope, rather than a copy of its
    ///     rule restated in the test.
    /// </summary>
    private static IInvocationAgentFactory CreateExternalFallbackFactory(string failingModel,
        IExternalProviderRegistry registry,
        OpenAiWireRecorder recorder,
        List<InvocationAgentDefinition> observed,
        Action? onFastModelSend = null,
        List<IReadOnlyList<ExternalProviderBindingPin>>? pinsAtSend = null)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   observed.Add(definition);
                   var fails = string.Equals(definition.ModelId, failingModel, StringComparison.Ordinal);
                   Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updates = fails
                       ? _ => FailingFastModelUpdates(onFastModelSend)
                       : token => ExternalSendUpdates(registry, definition.ModelId, recorder, pinsAtSend, token);

                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updates, onSessionObserved: null),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    /// <summary>The fast model going away at the send boundary, with the operator's edit landing as it does.</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> FailingFastModelUpdates(Action? onSend)
    {
        onSend?.Invoke();
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("the fast model is gone"));
    }

    /// <summary>One real external send, made from inside the turn's ambient pin scope.</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> ExternalSendUpdates(IExternalProviderRegistry registry,
        string modelId,
        OpenAiWireRecorder recorder,
        List<IReadOnlyList<ExternalProviderBindingPin>>? pinsAtSend,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        pinsAtSend?.Add(ExternalProviderBindingPinScope.Current);
        using var client = new ExternalOpenAiChatClient(registry, modelId, recorder.CreateHandler);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, cancellationToken).ConfigureAwait(false);
        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text);
    }

    /// <summary>Streams one chunk and then fails, so the turn has recorded first output before the failure.</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> TokenThenThrowingUpdates()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, "partial");
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("stream failed"));
    }

    /// <summary>Retry OFF, so a failing stream surfaces once and the test observes exactly the runner's own behaviour.</summary>
    private static IProviderStreamResilience NoRetryResilience()
    {
        return new ProviderStreamResilience(Options.Create(new ProviderResilienceOptions
            {
                RetryEnabled = false
            }),
            TimeProvider.System,
            NullLogger<ProviderStreamResilience>.Instance);
    }

    private static IInvocationAgentFactory CreateFactory(IAsyncEnumerable<AgentResponseUpdate> updates, Action<InvocationAgentDefinition>? onCreate = null, Action<bool>? onSessionObserved = null)
    {
        return CreateFactory(_ => updates, onCreate, onSessionObserved);
    }

    private static IInvocationAgentFactory CreateFactory(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory, Action<InvocationAgentDefinition>? onCreate = null,
        Action<bool>? onSessionObserved = null)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   onCreate?.Invoke(definition);
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updatesFactory, onSessionObserved),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    private static IInvocationAgentFactory CreateGenerationSpyFactory(Action onGeneration)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(_ => CreateUpdates("ok"), onStreamingRun: onGeneration),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> CreateUpdates(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }
    }

    // Records "stream" the moment the agent's streaming is first pulled, so a test can assert the readiness (warm) phase
    // ran BEFORE any streaming began.
    private static async IAsyncEnumerable<AgentResponseUpdate> WarmOrderingUpdates(ConcurrentQueue<string> events)
    {
        events.Enqueue("stream");
        yield return new AgentResponseUpdate(ChatRole.Assistant, "ok");
        await Task.Yield();
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ApprovalRequestUpdates()
    {
        // Stands in for FunctionInvokingChatClient surfacing an approval request for an ApprovalRequiredAIFunction:
        // the runner must detect this, run the approval round-trip, and resume threadlessly.
        var toolCall = new ToolCallContent("call-run-in-agent-home");
        var approvalRequest = new ToolApprovalRequestContent("approval-run-in-agent-home", toolCall);
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            approvalRequest
        });
        await Task.Yield();
    }

    // An approval-gated segment whose assistant turn carries a very large chunk of model reasoning, so the folded history
    // the tool-loop re-budgets on the approval resume overruns the window by that reasoning alone.
    private static async IAsyncEnumerable<AgentResponseUpdate> ReasoningHeavyApprovalRequestUpdates(int reasoningChars)
    {
        var toolCall = new ToolCallContent("call-run-in-agent-home");
        var approvalRequest = new ToolApprovalRequestContent("approval-run-in-agent-home", toolCall);
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent(new string(c: 'r', reasoningChars)),
            approvalRequest
        });
        await Task.Yield();
    }

    // A package carrying ONE resolved skill, plus the offered tool the approval path needs (the runner only retains the
    // segment updates it folds on resume when the offer list is non-empty — see approvalPossible). The skill tools
    // themselves are never in the offer: they reach the model through MAF's context provider.
    private static RuntimePackageBuilder SkillPackage(Guid conversationId, int version = 1, bool imported = false)
    {
        return RuntimePackageBuilder.Valid()
                                    .WithConversationId(conversationId)
                                    .WithAllowedTool("run_in_agent_home")
                                    .WithSkills(new ResolvedSkill(SkillId, SkillName, "A skill.", "Skill body.", version, imported));
    }

    private static IInvocationAgentFactory SkillApprovalFactory(string toolName, string skillName)
    {
        // Segments 1 and 3 are the first segment of turn one and turn two; the even segments are the resumes that follow
        // each approval decision. A suppressed prompt still resumes, so the segment count is the same either way.
        var segment = 0;
        return CreateFactory(_ =>
        {
            segment++;
            return segment is 1 or 3 ? SkillApprovalRequestUpdates(toolName, skillName) : CreateUpdates("done");
        });
    }

    // Stands in for MAF's AgentSkillsProvider surfacing an approval request for one of its ApprovalRequiredAIFunction
    // skill tools. Unlike ApprovalRequestUpdates above, the tool call is a concrete FunctionCallContent carrying the
    // model's arguments, because the skill and resource names the memo key is built from live nowhere else.
    private static async IAsyncEnumerable<AgentResponseUpdate> SkillApprovalRequestUpdates(string toolName, string skillName, string? resourceName = null)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["skillName"] = skillName
        };

        if (resourceName is not null)
        {
            arguments["resourceName"] = resourceName;
        }

        var toolCall = new FunctionCallContent($"call-{toolName}", toolName, arguments);
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new ToolApprovalRequestContent($"approval-{toolName}", toolCall)
        });
        await Task.Yield();
    }

    private const string CustomToolName = "custom__weather";

    private static RuntimePackageBuilder CustomToolPackage(Guid conversationId, int version = 1, bool isFixed = true)
    {
        return RuntimePackageBuilder.Valid()
                                    .WithConversationId(conversationId)
                                    .WithAllowedTool(CustomToolName)
                                    .WithCustomTools(new ResolvedCustomTool(CustomToolName, version, isFixed));
    }

    private static IInvocationAgentFactory CustomToolApprovalFactory(string toolName)
    {
        var segment = 0;
        return CreateFactory(_ =>
        {
            segment++;
            return segment is 1 or 3 ? CustomToolApprovalRequestUpdates(toolName) : CreateUpdates("done");
        });
    }

    // Stands in for the FunctionInvokingChatClient surfacing the approval request for the approval-wrapped custom tool.
    // The custom-tool memo reads only the tool NAME (not the arguments), so the args here are incidental.
    private static async IAsyncEnumerable<AgentResponseUpdate> CustomToolApprovalRequestUpdates(string toolName)
    {
        var toolCall = new FunctionCallContent($"call-{toolName}", toolName, new Dictionary<string, object?>(StringComparer.Ordinal));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new ToolApprovalRequestContent($"approval-{toolName}", toolCall)
        });
        await Task.Yield();
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> TwoApprovalRequestUpdates()
    {
        // A parallel-tool-call turn surfacing TWO approval requests in ONE segment: the runner must present
        // and answer BOTH, not just the last. Distinct tool-call CallIds so the dedup keeps them separate.
        var firstApproval = new ToolApprovalRequestContent("approval-1", new ToolCallContent("call-1"));
        var secondApproval = new ToolApprovalRequestContent("approval-2", new ToolCallContent("call-2"));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            firstApproval,
            secondApproval
        });
        await Task.Yield();
    }

    // Stands in for FunctionInvokingChatClient surfacing the approval request for the approval-wrapped ask_user tool.
    // The concrete FunctionCallContent is what carries the tool NAME and the model's raw arguments, and both are what
    // the runner branches and parses on.
    private static async IAsyncEnumerable<AgentResponseUpdate> AskUserRequestUpdates(IDictionary<string, object?> arguments)
    {
        var approvalRequest = new ToolApprovalRequestContent("approval-ask-user", new FunctionCallContent("call-ask-user", AskUserTool.ToolName, arguments));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            approvalRequest
        });
        await Task.Yield();
    }

    // An ask_user call whose CallId is a non-null EMPTY STRING — the shape the approval dedup already guards against.
    private static async IAsyncEnumerable<AgentResponseUpdate> BlankCallIdAskUserUpdates()
    {
        var approvalRequest = new ToolApprovalRequestContent("approval-ask-user", new FunctionCallContent(string.Empty, AskUserTool.ToolName, ValidAskUserArguments()));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            approvalRequest
        });
        await Task.Yield();
    }

    // A well-formed ask_user call, shaped exactly as a provider hands it back (JsonElement values in the argument bag).
    private static Dictionary<string, object?> ValidAskUserArguments()
    {
        const string Json = """
                            {
                              "questions": [
                                {
                                  "header": "Auth method",
                                  "question": "Which auth method?",
                                  "options": [
                                    { "label": "OAuth device flow", "description": "Works headless.", "recommended": true },
                                    { "label": "API key" }
                                  ]
                                }
                              ]
                            }
                            """;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(Json, AskUserArgumentOptions)!;
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> BlankCallIdApprovalUpdates()
    {
        // The SAME approval (a stable Id, but NO CallId) surfaced as two DISTINCT instances across two streamed chunks —
        // exercises the Id-based dedup fallback (reference identity alone would not collapse two instances). Dedup must
        // present it exactly once; a blank CallId must never bypass dedup and prompt twice for one call.
        var first = new ToolApprovalRequestContent("approval-no-callid", new FunctionCallContent(string.Empty, "run_in_agent_home"));
        var second = new ToolApprovalRequestContent("approval-no-callid", new FunctionCallContent(string.Empty, "run_in_agent_home"));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            first
        });
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            second
        });
        await Task.Yield();
    }

    // A factory that records the messages passed to EACH streaming segment (so a resume test can assert the folded
    // approval responses reached the agent) while returning per-segment updates from the supplied factory.
    private static IInvocationAgentFactory CreateMessageCapturingFactory(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory,
        Action<IReadOnlyList<ChatMessage>> onMessages)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updatesFactory, onSessionObserved: null, onMessagesObserved: onMessages),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> CreateMixedUpdates(params (string? Text, string? Thinking)[] chunks)
    {
        foreach (var (text, thinking) in chunks)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(thinking))
            {
                contents.Add(new TextReasoningContent(thinking));
            }

            if (!string.IsNullOrEmpty(text))
            {
                contents.Add(new TextContent(text));
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, contents);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> CreateUpdatesWithUsage(params (string Text, UsageDetails? Usage)[] chunks)
    {
        foreach (var (text, usage) in chunks)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(text))
            {
                contents.Add(new TextContent(text));
            }

            if (usage is not null)
            {
                contents.Add(new UsageContent(usage));
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, contents);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> BlockingUpdates(Task gate, TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "chunk");
        await gate.WaitAsync(cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "tail");
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> WaitForCancellation(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    // Parks the stream on an EXTERNAL gate rather than on the cancellation token, so the test alone decides when the
    // parked run is released. That is what makes the reverse-registration-order test deterministic: the release comes
    // from a callback the test registers after the runner's, and no registration of this stream's own can run earlier.
    private static async IAsyncEnumerable<AgentResponseUpdate> WaitForRelease(Task release,
        TaskCompletionSource started,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await release;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ThrowingUpdates()
    {
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("stream failed"));
    }

    // The four helpers below stand in for the two seams that write during a turn but sit several awaited frames BELOW
    // the runner: the provider-call budget middleware, which registers each round's tool-schema token estimate, and the
    // send-time relevance hop, which leaves the notice counts on the ambient scope. Both are AsyncLocal, so a fake
    // agent stream — enumerated inside the runner's own scopes — reaches exactly the instances the turn under test
    // seeded, which is the whole point: neither seam can be injected from out here.
    private static async IAsyncEnumerable<AgentResponseUpdate> ToolsFilteredUpdates(int hidden, int total, string text)
    {
        RecordRelevanceDecision(hidden, total);
        yield return new AgentResponseUpdate(ChatRole.Assistant, text);
        await Task.Yield();
    }

    // A first segment that both filters and requests an approval, so the resume segment's own decision can be shown not
    // to re-drain.
    private static async IAsyncEnumerable<AgentResponseUpdate> ToolsFilteredApprovalRequestUpdates(int hidden, int total)
    {
        RecordRelevanceDecision(hidden, total);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "working on it");
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new ToolApprovalRequestContent("approval-run-in-agent-home", new ToolCallContent("call-run-in-agent-home"))
        });
        await Task.Yield();
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ToolSchemaBudgetedUpdates(params int[] toolSchemaTokensPerRound)
    {
        RegisterProviderRounds(toolSchemaTokensPerRound);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "Hello");
        await Task.Yield();
    }

    // Rounds are registered BEFORE the park, so the estimate the cancelled path reports is a real one rather than zero.
    private static async IAsyncEnumerable<AgentResponseUpdate> ToolSchemaBudgetedThenParkedUpdates(int toolSchemaTokens,
        TaskCompletionSource started,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        RegisterProviderRounds([toolSchemaTokens]);
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> ToolSchemaBudgetedThenThrowingUpdates(int toolSchemaTokens)
    {
        RegisterProviderRounds([toolSchemaTokens]);
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("stream failed"));
    }

    private static void RegisterProviderRounds(IReadOnlyList<int> toolSchemaTokensPerRound)
    {
        var budget = AssertEx.NotNull(ProviderCallBudget.Current, "The runner seeds the provider-call budget before the agent runs.");
        foreach (var toolSchemaTokens in toolSchemaTokensPerRound)
        {
            budget.RegisterProviderRound(estimatedInputTokens: 10, toolSchemaTokens);
        }
    }

    // What the hop's single-flight factory does once one array's decision is computed: Interlocked.Exchange, so the
    // pair the runner drains always describes ONE array rather than a sum across two.
    private static void RecordRelevanceDecision(int hidden, int total)
    {
        var state = AssertEx.NotNull(ToolRelevanceScope.Current, "The runner seeds the relevance scope before the agent runs.");
        Interlocked.Exchange(ref state.PendingNoticeHiddenCount, hidden);
        Interlocked.Exchange(ref state.PendingNoticeTotalCount, total);
    }

    // The shape an HTTP client timeout takes: a TaskCanceledException on a token this node does not own, raised while
    // every runner/caller token is still live.
    private static async IAsyncEnumerable<AgentResponseUpdate> ProviderTimeoutUpdates()
    {
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout elapsing."));
    }

    private static IOrchestrationAgentFactory CreateOrchestrationFactory(IAsyncEnumerable<OrchestrationUpdate> updates, out Ref<FakeOrchestrationRunSession> sessionRef)
    {
#pragma warning disable CA2000 // The runner owns disposal of the session via its `await using`; the test asserts Disposed.
        return CreateOrchestrationFactory(new FakeOrchestrationRunSession(updates), out sessionRef);
#pragma warning restore CA2000
    }

    private static IOrchestrationAgentFactory CreateOrchestrationFactory(FakeOrchestrationRunSession session, out Ref<FakeOrchestrationRunSession> sessionRef)
    {
        var capturedRef = new Ref<FakeOrchestrationRunSession>
        {
            Value = session
        };
        sessionRef = capturedRef;

        var factory = Substitute.For<IOrchestrationAgentFactory>();
        factory.CreateAsync(Arg.Any<OrchestrationAgentDefinition>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromResult<IOrchestrationRunSession>(capturedRef.Value));

        return factory;
    }

    private static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationTextUpdates(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return OrchestrationUpdate.TextFragment(chunk, "a", "Triage");
            await Task.Yield();
        }

        yield return OrchestrationUpdate.Terminal();
    }

    private static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationParticipantTransitionUpdates()
    {
        yield return OrchestrationUpdate.TextFragment("triage", "a", "Triage");
        await Task.Yield();
        yield return OrchestrationUpdate.TextFragment("specialist", "b", "Specialist");
        yield return OrchestrationUpdate.Terminal();
    }

    // The gated approval stream: yields the approval request, then BLOCKS on the session's ApprovalGate before the
    // post-approval text/terminal. The gate is only completed by RespondToApprovalAsync, so if the runner drops or
    // mis-keys the resume the enumeration hangs and the test times out — proving the resume actually fired.
    private static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationGatedApprovalThenText(FakeOrchestrationRunSession session,
        string requestId,
        string toolName,
        string finalText)
    {
        yield return OrchestrationUpdate.Approval(requestId, toolName, "a", "Triage");
        await session.ApprovalGate;
        yield return OrchestrationUpdate.TextFragment(finalText, "a", "Triage");
        yield return OrchestrationUpdate.Terminal();
    }

    private static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationFailure(string message)
    {
        await Task.Yield();
        yield return OrchestrationUpdate.Failed(message, "a", "Triage");
    }

    private static OrchestrationSpec SampleSpec()
    {
        return new OrchestrationSpec
        {
            TriageParticipantKey = "a",
            MaxTurnsPerAgent = 6,
            ReturnToPrevious = false,
            Participants =
            [
                new OrchestrationSpecParticipant
                {
                    Key = "a",
                    Name = "Triage",
                    Description = "Routes work",
                    Instructions = "You are the triage agent.",
                    ModelId = "qwen3:8b",
                    Tools = []
                },
                new OrchestrationSpecParticipant
                {
                    Key = "b",
                    Name = "Specialist",
                    Description = "Does the work",
                    Instructions = "You are the specialist.",
                    ModelId = "qwen3:8b",
                    Tools = []
                }
            ],
            Edges =
            [
                new OrchestrationSpecEdge
                {
                    FromKey = "a",
                    ToKey = "b",
                    Reason = "specialist work"
                }
            ]
        };
    }

    private sealed class Ref<T>
    {
        public T? Value { get; set; }
    }

    private readonly record struct HarnessTerminalMeasurement(long Value, string? Provider, string? Outcome, bool? Orchestration);

    private sealed class HarnessMetricCapture : IDisposable
    {
        private readonly ConcurrentBag<long> _handoffs = [];
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<HarnessTerminalMeasurement> _terminals = [];

        public HarnessMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, NodeMetrics.MeterName, StringComparison.Ordinal)
                    && instrument.Name is "agent_harness_invocation_total" or "agent_harness_handoffs")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (string.Equals(instrument.Name, "agent_harness_handoffs", StringComparison.Ordinal))
                {
                    _handoffs.Add(measurement);
                    return;
                }

                string? provider = null;
                string? outcome = null;
                bool? orchestration = null;
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, "provider", StringComparison.Ordinal))
                    {
                        provider = tag.Value as string;
                    }
                    else if (string.Equals(tag.Key, "outcome", StringComparison.Ordinal))
                    {
                        outcome = tag.Value as string;
                    }
                    else if (string.Equals(tag.Key, "orchestration", StringComparison.Ordinal))
                    {
                        orchestration = tag.Value as bool?;
                    }
                }

                _terminals.Add(new HarnessTerminalMeasurement(measurement, provider, outcome, orchestration));
            });
            _listener.Start();
        }

        public IReadOnlyList<long> Handoffs => [.. _handoffs];

        public IReadOnlyList<HarnessTerminalMeasurement> Terminals => [.. _terminals];

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    private sealed class FakeOrchestrationRunSession : IOrchestrationRunSession
    {
        // Completed by RespondToApprovalAsync. A gated update stream awaits this before yielding its post-approval
        // portion, so the approval test only reaches its terminal if the runner actually resumes the held session —
        // a dropped or mis-keyed RespondToApprovalAsync leaves the gate uncompleted and the test times out (fails).
        private readonly TaskCompletionSource<(bool Approved, string? Reason)> _approvalGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Func<FakeOrchestrationRunSession, IAsyncEnumerable<OrchestrationUpdate>> _updatesFactory;

        public FakeOrchestrationRunSession(IAsyncEnumerable<OrchestrationUpdate> updates)
        {
            _updatesFactory = _ => updates;
        }

        public FakeOrchestrationRunSession(Func<FakeOrchestrationRunSession, IAsyncEnumerable<OrchestrationUpdate>> updatesFactory)
        {
            _updatesFactory = updatesFactory;
        }

        public bool Disposed { get; private set; }

        public List<(string RequestId, bool Approved, string? Reason)> ApprovalResponses { get; } = [];

        // The gated update stream awaits this; it only resolves once RespondToApprovalAsync is called for the matching
        // RequestId, proving the runner's resume actually drove the held session.
        public Task<(bool Approved, string? Reason)> ApprovalGate => _approvalGate.Task;

        public IAsyncEnumerable<OrchestrationUpdate> WatchAsync(CancellationToken cancellationToken = default)
        {
            return _updatesFactory(this);
        }

        public Task RespondToApprovalAsync(string requestId, bool approved, string? reason, CancellationToken cancellationToken = default)
        {
            ApprovalResponses.Add((requestId, approved, reason));
            _approvalGate.TrySetResult((approved, reason));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAIAgent : AIAgent
    {
        private readonly Action<IReadOnlyList<ChatMessage>>? _onMessagesObserved;
        private readonly Action<bool>? _onSessionObserved;
        private readonly Action? _onStreamingRun;
        private readonly Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> _updatesFactory;

        public FakeAIAgent(IAsyncEnumerable<AgentResponseUpdate> updates)
            : this(_ => updates)
        {
        }

        public FakeAIAgent(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory,
            Action<bool>? onSessionObserved = null,
            Action<IReadOnlyList<ChatMessage>>? onMessagesObserved = null,
            Action? onStreamingRun = null)
        {
            _updatesFactory = updatesFactory;
            _onSessionObserved = onSessionObserved;
            _onMessagesObserved = onMessagesObserved;
            _onStreamingRun = onStreamingRun;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new FakeAgentSession());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new FakeAgentSession());
        }

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _onSessionObserved?.Invoke(session is null);
            _onMessagesObserved?.Invoke(messages.ToList());
            _onStreamingRun?.Invoke();
            return _updatesFactory(cancellationToken);
        }
    }

    private sealed class MinimumEffectiveContextAdmissionPolicy(int requiredContextTokens) : IInvocationGenerationAdmissionPolicy
    {
        public const string EffectiveContextUnavailableMessage = "Effective context unavailable.";

        public InvocationGenerationAdmissionContext? LastContext { get; private set; }

        public Task<InvocationGenerationAdmissionDecision> EvaluateAsync(InvocationGenerationAdmissionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastContext = context;

            if (context.EffectiveContextTokens is null)
            {
                return Task.FromResult(InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable));
            }

            if (context.EffectiveContextTokens < requiredContextTokens)
            {
                return Task.FromResult(InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient));
            }

            return Task.FromResult(InvocationGenerationAdmissionDecision.Allow);
        }
    }

    private sealed class RejectingAdmissionPolicy(string reasonCode) : IInvocationGenerationAdmissionPolicy
    {
        public Task<InvocationGenerationAdmissionDecision> EvaluateAsync(InvocationGenerationAdmissionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(InvocationGenerationAdmissionDecision.Reject(reasonCode));
        }
    }

    private sealed class FakeAgentSession : AgentSession
    {
    }
}
