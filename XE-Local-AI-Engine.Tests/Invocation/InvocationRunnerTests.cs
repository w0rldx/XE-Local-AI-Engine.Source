namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
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
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class InvocationRunnerTests
{
    private const string SkillName = "demo";

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
                            Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null), Arg.Any<long?>());
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
                            Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null), Arg.Any<long?>());
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
                            Arg.Is<int?>(static value => value == null), Arg.Is<int?>(static value => value == null), Arg.Any<long?>());
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
                        .ReportInvocationCompletedAsync(package.InvocationId, Arg.Is<int?>(10), Arg.Is<int?>(2), Arg.Is<int?>(12), Arg.Is<int?>(static value => value == null), Arg.Any<long?>());
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
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(new RuntimePackageValidationResult(isValid: false, ["bad package"]));

        var runner = CreateRunner(sender, validator: validator);
        var package = RuntimePackageBuilder.Valid().Build();

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => RunAsync(runner, package));

        AssertEx.Contains(exception.Message, "bad package");
        AssertEx.Empty(sender.SentEncryptedFailures);
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

        var runTask = RunAsync(runner, package, cancellationTokenSource.Token);
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
        // AUD4-04: an operator FORCE-eject surfaces as LlamaServerModelEjectedException, which must classify as
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
        // AUD4-01: for a local llama.cpp model the runner warms the model to readiness BEFORE the watched streaming pull
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
    public async Task RunAsync_OllamaModel_DoesNotWarmViaReadinessPhase()
    {
        // AUD4-01: the readiness (warm) phase is llama.cpp-only. An Ollama model must NOT be warmed here (it warms
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
        // QUESTION continues with "not answered" (D4) — and neither one waits out the pending-approval window.
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

        AssertEx.Equal(expected: 2, segment, "the turn must continue threadlessly, not fail — D4 holds for an unattended run too");
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
        // ResolveToolCallCardId preserves a non-null EMPTY-STRING CallId (so the card key matches the streaming
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
        // D5 regression. The invocation deadline (CancelAfter(InvocationTimeout)) used to keep running while a human
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
        // The same D5 re-arm applies to the SHIPPING tool-approval round-trip — a deliberate, separately reviewable
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
            runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: false));

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

        var task = runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: false);
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

        var task = runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: true);
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
        // CallId wins, a null CallId falls back to the tool name, and — the previously-divergent case — a non-null
        // EMPTY-STRING CallId resolves to the same empty string on both paths rather than one using the tool name.
        AssertEx.Equal("call-1", InvocationRunner.ResolveToolCallCardId("call-1", "run_in_agent_home"));
        AssertEx.Equal("run_in_agent_home", InvocationRunner.ResolveToolCallCardId(callId: null, "run_in_agent_home"));
        AssertEx.Equal(string.Empty, InvocationRunner.ResolveToolCallCardId(string.Empty, "run_in_agent_home"));
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
        var toolCall = runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}", requiresApproval: false);
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
        IProviderStreamResilience? providerStreamResilience = null,
        ConversationContextBudgetOptions? contextBudgetOptions = null,
        UserQuestionAnswerStash? userQuestionAnswerStash = null,
        IToolApprovalAuditRecorder? approvalAuditRecorder = null,
        IToolApprovalPolicy? approvalPolicy = null)
    {
        var resolvedContextBudgetOptions = contextBudgetOptions ?? new ConversationContextBudgetOptions();
        var resolvedFactory = invocationAgentFactory ?? CreateFactory(agentUpdates ?? CreateUpdates("ok"));
        var resolvedOrchestrationFactory = orchestrationAgentFactory ?? Substitute.For<IOrchestrationAgentFactory>();

        var resolvedValidator = validator ?? Substitute.For<IRuntimePackageValidator>();
        if (validator is null)
        {
            resolvedValidator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);
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
                                                     .Build();

        return new InvocationRunner(new Lazy<IHubMessageSender>(() => sender),
            new Lazy<IWorkerEventDispatcher>(() => resolvedEventDispatcher),
            resolvedFactory,
            resolvedOrchestrationFactory,
            new EnvelopeCryptoService(new AesGcmNodeAeadCipher()),
            resolvedValidator,
            resolvedCapabilityReporter,
            resolvedProviderResolver,
            Substitute.For<IDeadLetterStore>(),
            resolvedProviderStreamResilience,
            new ConversationContextBudgeter(new HeuristicTokenEstimator(), Options.Create(resolvedContextBudgetOptions)),
            Options.Create(resolvedContextBudgetOptions),
            Options.Create(new ProviderResilienceOptions()),
            Options.Create(new AgentToolPipelineOptions()),
            Options.Create(new ProviderCallBudgetOptions()),
            configuration,
            runtimeSettings,
            Options.Create(new SpawnOptions()),
            approvalAuditRecorder ?? Substitute.For<IToolApprovalAuditRecorder>(),
            approvalPolicy ?? NodeToolApprovalPolicy.FromSettings(settings: null),
            userQuestionAnswerStash ?? new UserQuestionAnswerStash(TimeProvider.System),
            NullLogger<InvocationRunner>.Instance);
    }

    private static async Task RunAsync(InvocationRunner runner, RuntimePackage package, CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.Create(package, Guid.NewGuid(), epochVersion: 1, new byte[32]);
        await runner.RunAsync(context, cancellationToken);
    }

    private static async Task RunPlainAsync(InvocationRunner runner, RuntimePackage package, CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
        await runner.RunAsync(context, cancellationToken);
    }

    private static CancellationTokenSource? GetActiveInvocationCancellationTokenSource(InvocationRunner runner)
    {
        var field = AssertEx.NotNull(typeof(InvocationRunner).GetField("_invocationCancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic));
        return (CancellationTokenSource?)field.GetValue(runner);
    }

    private static void SetMaxPendingToolCallAge(InvocationRunner runner, TimeSpan maxPendingToolCallAge)
    {
        var field = AssertEx.NotNull(typeof(InvocationRunner).GetField("_maxPendingToolCallAge", BindingFlags.Instance | BindingFlags.NonPublic));
        field.SetValue(runner, maxPendingToolCallAge);
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
               .Returns(_ => Task.FromResult<IOrchestrationRunSession>(capturedRef.Value!));

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
        private readonly Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> _updatesFactory;

        public FakeAIAgent(IAsyncEnumerable<AgentResponseUpdate> updates)
            : this(_ => updates)
        {
        }

        public FakeAIAgent(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory,
            Action<bool>? onSessionObserved = null,
            Action<IReadOnlyList<ChatMessage>>? onMessagesObserved = null)
        {
            _updatesFactory = updatesFactory;
            _onSessionObserved = onSessionObserved;
            _onMessagesObserved = onMessagesObserved;
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
            return _updatesFactory(cancellationToken);
        }
    }

    private sealed class FakeAgentSession : AgentSession
    {
    }
}
