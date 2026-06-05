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
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
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
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
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
        // The authoritative token counts are asserted exactly; the new wall-clock duration arg is matched with Arg.Any
        // because the elapsed value is non-deterministic.
        await dispatcher.Received(1)
                        .ReportInvocationCompletedAsync(package.InvocationId, Arg.Is<int?>(10), Arg.Is<int?>(2), Arg.Is<int?>(12), Arg.Is<int?>(static value => value == null), Arg.Any<long?>());
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
    [Arguments("registry.ollama.ai/library/gemma:12b does not support thinking", "This model does not support reasoning.")]
    [Arguments("this model does not support tools", "This model does not support tool calling.")]
    public async Task RunAsync_WhenModelRejectsCapability_MapsModelCapabilityUnsupportedFailureCategory(string providerMessage, string expectedError)
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException(providerMessage, null, HttpStatusCode.BadRequest)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelCapabilityUnsupported)
                                                                         && failure.Error == expectedError);
    }

    [Test]
    public async Task RunAsync_WhenModelLoadFailsWith500_MapsModelLoadFailedFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        // The blob path in the message must never reach the surfaced error; the status code alone drives the mapping.
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("unable to load model /root/.ollama/models/blobs/sha256-deadbeef", null,
                   HttpStatusCode.InternalServerError)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => failure.FailureCategory == nameof(FailureCategory.ModelLoadFailed)
                                                                         && failure.Error == "The model could not be loaded or run on the provider.");
    }

    [Test]
    public async Task RunAsync_WhenModelLoadFailsWith500_DoesNotLeakBlobPath()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("unable to load model /root/.ollama/models/blobs/sha256-deadbeef", null,
                   HttpStatusCode.InternalServerError)));

        var runner = CreateRunner(sender, factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentEncryptedFailures, failure => !failure.Error.Contains("blobs", StringComparison.Ordinal)
                                                                         && !failure.Error.Contains(".ollama", StringComparison.Ordinal));
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

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).Build());
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        var requestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(requestId, true));
        await runTask;

        AssertEx.Equal(2, segment, "the runner must re-invoke the agent threadlessly after the approval decision");
        await dispatcher.Received(1).ReportApprovalRequestedAsync(Arg.Is<ApprovalRequestPayload>(payload => payload.InvocationId == invocationId));
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
        AssertEx.Equal(1, sender.SentCompletions.Count);
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
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(requestId, true));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportApprovalRequestedAsync(Arg.Is<ApprovalRequestPayload>(payload => payload.InvocationId == invocationId));
        // The approval card must name the tool, not the opaque correlation id (single-agent UX parity).
        AssertEx.Contains(sender.SentApprovals.Single().Description, "run_in_agent_home");
        AssertEx.Equal(1, sessionRef.Value!.ApprovalResponses.Count);
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

        AssertEx.Equal(0, sender.SentCompletions.Count);
        AssertEx.Equal(1, sender.SentFailures.Count);
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
        await AssertEx.ThrowsAsync<InvocationRunner.WorkerToolCallException>(() =>
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

        AssertEx.Equal(0, sender.SentApprovals.Count);

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

        AssertEx.Equal(0, sender.SentToolCalls.Count);

        var approvalRequestId = sender.SentApprovals.Single().RequestId;
        runner.ResolveApprovalResult(new ApprovalResolvedEvent(approvalRequestId, true));
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
        IAsyncEnumerable<AgentResponseUpdate>? agentUpdates = null,
        IOrchestrationAgentFactory? orchestrationAgentFactory = null)
    {
        var resolvedFactory = invocationAgentFactory ?? CreateFactory(agentUpdates ?? CreateUpdates("ok"));
        var resolvedOrchestrationFactory = orchestrationAgentFactory ?? Substitute.For<IOrchestrationAgentFactory>();

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
            resolvedOrchestrationFactory,
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
