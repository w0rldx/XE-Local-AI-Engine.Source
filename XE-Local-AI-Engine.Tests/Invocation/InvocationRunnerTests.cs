namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections;
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
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class InvocationRunnerTests
{
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
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId);
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
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId);
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

        AssertEx.Equal(2, contentChunks.Count);
        AssertEx.Equal(2, reasoningChunks.Count);
        AssertEx.Equal(1, reasoningChunks[0].Sequence);
        AssertEx.Equal(2, reasoningChunks[1].Sequence);
        AssertEx.Equal(1, sender.SentEncryptedCompletions.Count);
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
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId);
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

        AssertEx.Equal(1, sender.SentCompletions.Count);
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
        AssertEx.Equal(2, reasoningChunks.Count);
        AssertEx.Equal("Let me think...", reasoningChunks[0].Token);
        AssertEx.Equal(" more thought", reasoningChunks[1].Token);
        AssertEx.True(sender.SentReasoningChunks.Any(chunk => chunk.IsComplete));
        AssertEx.Equal(1, sender.SentCompletions.Count);
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

        AssertEx.Equal(1, sender.SentCompletions.Count);
        AssertEx.Equal(10, sender.SentCompletions[0].InputTokens);
        AssertEx.Equal(2, sender.SentCompletions[0].OutputTokens);
        AssertEx.Equal(12, sender.SentCompletions[0].TokensUsed);
        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId, 10, 2, 12, null);
    }

    [Test]
    public async Task RunAsync_WhenPlainContextAndAgentRuntimeThrows_SendsPlainInvocationFailed()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: ThrowingUpdates());
        var package = RuntimePackageBuilder.Valid().Build();

        await RunPlainAsync(runner, package);

        AssertEx.Empty(sender.SentEncryptedFailures);
        AssertEx.Equal(1, sender.SentFailures.Count);
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

        AssertEx.Equal(1, sender.SentEncryptedCompletions.Count);
        AssertEx.Equal(package.ConversationId, sender.SentEncryptedCompletions[0].ConversationId);
        AssertEx.Equal(1, sender.SentEncryptedCompletions[0].EpochVersion);
    }

    [Test]
    public async Task RunAsync_ValidationFails_ThrowsInvalidOperationException()
    {
        var sender = new MockHubMessageSender();
        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(new RuntimePackageValidationResult(false, ["bad package"]));

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
    public async Task RunAsync_RespectsCancellationToken_StopsStreaming()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, agentUpdates: BlockingUpdates(gate.Task, started));
        var package = RuntimePackageBuilder.Valid().Build();
        using var cancellationTokenSource = new CancellationTokenSource();

        var runTask = RunAsync(runner, package, cancellationTokenSource.Token);
        await started.Task;
        await cancellationTokenSource.CancelAsync();
        gate.TrySetCanceled();
        await runTask;

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Timeout));
    }

    [Test]
    public async Task RunAsync_MapsInvocationDefinitionSystemPromptAndSortOrder()
    {
        var sender = new MockHubMessageSender();
        InvocationAgentDefinition? capturedDefinition = null;
        var factory = CreateFactory(CreateUpdates("ok"), definition => capturedDefinition = definition);

        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("late")
                                           .WithConversationMessage(MessageRole.Assistant, "middle", 1)
                                           .WithConversationMessage(MessageRole.User, "early", -1)
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
        AssertEx.Equal(1, definition.Tools.Count);
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
        }, agentUpdates: CreateUpdates(new string('x', (1024 * 1024) + 1)));
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
        }, agentUpdates: CreateMixedUpdates((Text: null, Thinking: new string('x', (1024 * 1024) + 1))));
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
        AssertEx.Equal(1, runner.ActiveInvocationCount);

        var drainTask = runner.DrainActiveInvocationsAsync(TimeSpan.FromSeconds(2));
        AssertEx.False(drainTask.IsCompleted);

        gate.SetResult();

        AssertEx.True(await drainTask);
        await runTask;
        AssertEx.Equal(0, runner.ActiveInvocationCount);
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
        AssertEx.Equal(1, runner.ActiveInvocationCount);

        gate.SetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.Equal(0, runner.ActiveInvocationCount);
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
        var runner = CreateRunner(sender, CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)));

        var runTask = RunAsync(runner, package);
        await started.Task;

        // Fire the invocation timeout deterministically now that the agent is streaming. Cancelling the
        // invocation token source without a prior user-cancel sets the runner's timeout flag, so the
        // failure is mapped to the Timeout category — the same path the real CancelAfter timer triggers.
        await AssertEx.NotNull(GetActiveInvocationCancellationTokenSource(runner)).CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.ConversationId == package.ConversationId && failure.FailureCategory == nameof(FailureCategory.Timeout));
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
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("not found", null, HttpStatusCode.NotFound)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelUnavailable)
                                                                         && failure.Error == "Selected model is not installed on this node.");
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
        var longMessage = new string('x', 600);
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException(longMessage)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        var failure = sender.SentEncryptedFailures.Single();
        AssertEx.Equal(512, failure.Error.Length);
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
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(approvalRequestId, true));
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
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(approvalRequestId, true));
        await AssertEx.EventuallyAsync(() => sender.SentToolCalls.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentToolCalls.Single().RequestId;
        runner.ResolveToolCallResult(new ToolCallResultEvent
        {
            RequestId = requestId,
            Result = string.Empty,
            Error = "approval timeout"
        });

        var exception = await AssertEx.ThrowsAsync<InvocationRunner.WorkerToolCallException>(() => task);
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

        var exception = await AssertEx.ThrowsAsync<InvocationRunner.WorkerToolCallException>(() => pendingCall);
        AssertEx.Contains(exception.Message, "timed out waiting for a result", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task RunAsync_WhenToolBridgeFails_MapsAgentToolCallCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvocationRunner.WorkerToolCallException("approve-job", "approval timeout")));

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

        var exception = await AssertEx.ThrowsAsync<InvocationRunner.WorkerToolCallException>(() => runner.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}"));
        AssertEx.Contains(exception.Message, "timed out waiting for a result", StringComparison.OrdinalIgnoreCase);
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

        var exception = await AssertEx.ThrowsAsync<InvocationRunner.WorkerToolCallException>(() => task);
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

        var exception = await AssertEx.ThrowsAsync<InvocationRunner.WorkerToolCallException>(() => pendingToolCall);
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

        var exception = await AssertEx.ThrowsAsync<InvocationRunner.WorkerToolCallException>(() => pendingToolCall);
        AssertEx.Contains(exception.Message, "timed out during cleanup", StringComparison.OrdinalIgnoreCase);
    }

    private static InvocationRunner CreateRunner(MockHubMessageSender sender,
        IInvocationAgentFactory? invocationAgentFactory = null,
        IRuntimePackageValidator? validator = null,
        ICapabilityReporter? capabilityReporter = null,
        WorkerNodeOptions? workerOptions = null,
        IWorkerEventDispatcher? eventDispatcher = null,
        IAsyncEnumerable<AgentResponseUpdate>? agentUpdates = null)
    {
        var resolvedFactory = invocationAgentFactory ?? CreateFactory(agentUpdates ?? CreateUpdates("ok"));

        var resolvedValidator = validator ?? Substitute.For<IRuntimePackageValidator>();
        if (validator is null)
        {
            resolvedValidator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);
        }

        var resolvedCapabilityReporter = capabilityReporter ?? Substitute.For<ICapabilityReporter>();
        resolvedCapabilityReporter.VerifyOllamaAndModelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var resolvedEventDispatcher = eventDispatcher ?? Substitute.For<IWorkerEventDispatcher>();

        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Ollama:ChatModel"] = "qwen3.5:0.8b"
                            })
                            .Build();

        return new InvocationRunner(new Lazy<IHubMessageSender>(() => sender),
            new Lazy<IWorkerEventDispatcher>(() => resolvedEventDispatcher),
            resolvedFactory,
            new EnvelopeCryptoService(),
            resolvedValidator,
            resolvedCapabilityReporter,
            Substitute.For<IDeadLetterStore>(),
            configuration,
            Options.Create(workerOptions ?? new WorkerNodeOptions
            {
                NodeName = "worker",
                MaxResponseSizeMb = 10,
                MaxPendingToolCallAgeMinutes = 5
            }),
            NullLogger<InvocationRunner>.Instance);
    }

    private static async Task RunAsync(InvocationRunner runner, RuntimePackage package, CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.Create(package, Guid.NewGuid(), 1, new byte[32]);
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

    private static async IAsyncEnumerable<AgentResponseUpdate> ThrowingUpdates()
    {
        await Task.Yield();
        throw new InvalidOperationException("stream failed");
#pragma warning disable CS0162
        yield return new AgentResponseUpdate(ChatRole.Assistant, "unreachable");
#pragma warning restore CS0162
    }

    private sealed class FakeAIAgent : AIAgent
    {
        private readonly Action<bool>? _onSessionObserved;
        private readonly Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> _updatesFactory;

        public FakeAIAgent(IAsyncEnumerable<AgentResponseUpdate> updates)
            : this(_ => updates)
        {
        }

        public FakeAIAgent(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory, Action<bool>? onSessionObserved = null)
        {
            _updatesFactory = updatesFactory;
            _onSessionObserved = onSessionObserved;
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
            return _updatesFactory(cancellationToken);
        }
    }

    private sealed class FakeAgentSession : AgentSession
    {
    }
}
