namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Net;
using Microsoft.Agents.AI;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Invocation.InvocationRunnerTestSupport;

[Category(TestCategories.Unit)]
public sealed class InvocationRunnerFailureTests
{
    [Test]
    [Arguments("registry.ollama.ai/library/gemma:12b does not support thinking", "This model does not support reasoning.")]
    [Arguments("this model does not support tools", "This model does not support tool calling.")]
    public async Task RunAsync_WhenModelRejectsCapability_MapsModelCapabilityUnsupportedFailureCategory(string providerMessage, string expectedError)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException(providerMessage, inner: null, HttpStatusCode.BadRequest)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == expectedError), FailureCategory.ModelCapabilityUnsupported);
    }

    [Test]
    public async Task RunAsync_WhenProviderReturnsNotFound_MapsModelUnavailableFailureCategory()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("not found", inner: null, HttpStatusCode.NotFound)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1)
                        .ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == "Selected model is not installed on this node."), FailureCategory.ModelUnavailable);
    }

    [Test]
    public async Task RunAsync_WhenProviderUnreachable_MapsFailureCategory()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("offline")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), FailureCategory.ProviderUnreachable);
    }

    [Test]
    public async Task RunAsync_WhenToolBridgeFails_MapsAgentToolCallCategory()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new WorkerToolCallException("approve-job", "approval timeout")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("approve-job").Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), FailureCategory.AgentToolCall);
    }

    [Test]
    public async Task RunAsync_WhenToolCallFailsWithoutATimeout_KeepsTheGenericToolFailureMessage()
    {
        // The guard on the arm above: an ordinary tool error must NOT be relabelled a timeout.
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new WorkerToolCallException("read_file", "boom")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == "Worker tool execution failed."), FailureCategory.AgentToolCall);
    }

    [Test]
    public async Task RunAsync_WhenToolGrammarFailsToCompileWith500_WinsOverModelLoadFailedArm()
    {
        // The grammar arm is ordered ahead of ReportsModelLoadFailure, which matches on the status code alone: a 500
        // carrying the grammar signature must still classify as the (actionable) tool-preparation failure.
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("Failed to initialize samplers: failed to parse grammar", inner: null,
                   HttpStatusCode.InternalServerError)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), FailureCategory.ModelCapabilityUnsupported);
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
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException(providerMessage, inner: null, HttpStatusCode.BadRequest)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(),
            Arg.Is<string>(message => message == "The model could not be prepared for tool calling with the current tool set. Retry with tools turned off, or select a different model."),
            FailureCategory.ModelCapabilityUnsupported);
    }

    [Test]
    public async Task RunAsync_WhenJinjaTextArrivesWithoutAProvider400_IsNotRewordedAsATemplateFailure()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("Jinja Exception: quoted by a local component.")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), FailureCategory.ModelCapabilityUnsupported);
    }

    [Test]
    public async Task RunAsync_WhenTheChatTemplateRejectsTheConversation_MapsAFixedReasonInsteadOfTheRawJinjaText()
    {
        // llama-server's 400 for a template that raised carries the Jinja source and exception text, which the user
        // must never see verbatim. A wrapped status-400 ClientResultException is the shape the OpenAI adapter produces.
        const string ProviderMessage = "HTTP 400 (invalid_request_error: ) Unable to generate parser for this template. Automatic parser generation failed: "
                                       + "While executing CallExpression at line 85 ... Error: Jinja Exception: System message must be at the beginning.";
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("The chat client failed.",
                   new HttpRequestException(ProviderMessage, inner: null, HttpStatusCode.BadRequest))));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(),
            Arg.Is<string>(message => message == "The model's chat template could not render this conversation. Select a different model, or retry without the agent's skills or tools."),
            FailureCategory.ModelCapabilityUnsupported);
    }

    [Test]
    public async Task RunAsync_WhenToolGrammarFailureIsWrapped_MapsModelCapabilityUnsupportedFailureCategory()
    {
        // The agent framework wraps the transport exception, so the signature is only visible on an inner exception.
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("The chat client failed.",
                   new HttpRequestException("Failed to initialize samplers: failed to parse grammar", inner: null, HttpStatusCode.BadRequest))));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(),
            Arg.Is<string>(message => message == "The model could not be prepared for tool calling with the current tool set. Retry with tools turned off, or select a different model."),
            FailureCategory.ModelCapabilityUnsupported);
    }

    [Test]
    public async Task RunAsync_WhenToolResultTimesOut_KeepsTheToolTimeoutDistinctFromAGenericToolFailure()
    {
        // ToolResultTimeout must not collapse into the same "Worker tool execution failed." every tool error uses, or a
        // turn killed by the tool-result bound is indistinguishable from a tool that simply errored.
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new WorkerToolCallException("read_file", "Tool call timed out waiting for a result.", new TimeoutException("timed out"))));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == "A tool call timed out waiting for its result."), FailureCategory.AgentToolCall);
    }

    [Test]
    public async Task RunAsync_WhenUnexpected_MapsFailureCategory()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("boom")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), FailureCategory.Unexpected);
    }

    [Test]
    public async Task RunAsync_WhenUnrelatedBadRequest_StillMapsProviderUnreachable()
    {
        // The new grammar arm must not widen: an unrelated HTTP 400 keeps its previous classification.
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("invalid request payload", inner: null, HttpStatusCode.BadRequest)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), FailureCategory.ProviderUnreachable);
    }

    [Test]
    public async Task RunAsync_WhenAgentRuntimeMessageContainsFrameworkType_RedactsFrameworkNames()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NotSupportedException("Microsoft.Agents.AI.ChatClientAgentException: provider blew up")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => !message.Contains("ChatClientAgentException", StringComparison.Ordinal)),
            FailureCategory.AgentRuntime);
    }

    [Test]
    public async Task RunAsync_WhenGenericTimeout_MapsSanitizedTimeoutMessageWithoutLeakingDetail()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        // A bare TimeoutException whose framework message names a host/path must NOT be forwarded verbatim.
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new TimeoutException("timed out reaching http://10.0.0.5:11434/api/chat")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == "The operation timed out."), FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenModelLoadFailsWith500_DoesNotLeakBlobPath()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("unable to load model /root/.ollama/models/blobs/sha256-deadbeef", inner: null,
                   HttpStatusCode.InternalServerError)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => !message.Contains("blobs", StringComparison.Ordinal)), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenModelLoadFailsWith500_MapsModelLoadFailedFailureCategory()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        // The blob path in the message must never reach the surfaced error; the status code alone drives the mapping.
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("unable to load model /root/.ollama/models/blobs/sha256-deadbeef", inner: null,
                   HttpStatusCode.InternalServerError)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == "The model could not be loaded or run on the provider."),
            FailureCategory.ModelLoadFailed);
    }

    [Test]
    public async Task RunAsync_WhenCapacityRefusesTheModelLaunch_MapsInsufficientCapacity_AndSurfacesTheRefusalVerbatim()
    {
        const string refusal = "Insufficient capacity for 'big/model-GGUF:Q4_K_M' (Chat): not enough free memory for another model. "
                               + "Loaded now: 'small/model-GGUF:Q4_K_M' (Chat). Eject one of them or pick a loaded model.";
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new LlamaCapacityRefusedException(refusal)));
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), refusal, FailureCategory.InsufficientCapacity);
    }

    [Test]
    public async Task RunAsync_WhenOrchestrationFails_SendsInvocationFailedWithoutLeakingRawDetail()
    {
        // The raw MAF executor detail must NOT reach the client (logged server-side only); the client sees a constant.
        var orchestrationFactory = CreateOrchestrationFactory(OrchestrationFailure("workflow boom /secret/internal/path"), out _);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(orchestrationAgentFactory: orchestrationFactory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithOrchestrationSpec(SampleSpec()).Build();

        await RunPlainAsync(runner, package);

        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationCompletedAsync(Arg.Any<Guid>());
        await dispatcher.Received(1)
                        .ReportInvocationFailedAsync(package.InvocationId,
                            Arg.Is<string>(message => !message.Contains("secret", StringComparison.Ordinal)
                                                      && message.Contains("Orchestration run failed", StringComparison.Ordinal)),
                            Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task Cancel_WhileRunning_TerminatesStream()
    {
        // The stream-termination half: a stop while the provider is parked mid-stream must end the run rather than
        // leave it blocked on the gate, and must classify Cancelled. The sibling below pins the breadcrumb message.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(agentUpdates: BlockingUpdates(gate.Task, started), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;
        runner.Cancel(package.InvocationId);
        gate.TrySetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task Cancel_WhileRunning_ReportsTheUserStopAsTheReason()
    {
        // The same breadcrumb from the other side: an operator stop must never read like a timeout. This is the pair
        // that made the "Cancelled at ~550s" report unattributable — user stop, detached-grace reaper and the node
        // watchdog all persisted the identical sentence.
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        runner.Cancel(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == "Stopped by user."), FailureCategory.Cancelled);
    }

    [Test]
    public async Task CancelDetached_WhileRunning_ReportsTheDisconnectGraceAsTheReason()
    {
        // The detached-run reaper is the third cancellation cause, and the one an operator is least able to guess at:
        // it must name itself rather than share the user-stop sentence.
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;

        runner.CancelDetached(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(),
            Arg.Is<string>(message => message == "Stopped: no client was attached to this run and the disconnect grace period expired."), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_WhenCancelledWhileReadingTheRelevanceSetting_EndsTheTurnCancelled()
    {
        // The read takes the INVOCATION token, not the caller's: an operator Cancel (or the whole-turn watchdog) trips
        // that one. Reading on the caller token would leave a stalled settings read uncancellable, which is the failure
        // this pins. Gates only, no sleeps.
        var gateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().Build();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            toolRelevanceRead: async cancellationToken =>
            {
                gateReached.TrySetResult();
                return await hold.Task.WaitAsync(cancellationToken);
            });

        var runTask = RunAsync(runner, package);
        // Bounded: if the runner ever stops reading the setting, this must fail fast rather than park the whole run.
        await gateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Cancel(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_WhenModelForceEjectedMidRequest_MapsCancelledWithTruthfulMessage()
    {
        // An operator FORCE-eject surfaces as LlamaServerModelEjectedException, which must classify as
        // Cancelled (an operator action, not a generic provider failure) and surface the truthful "ejected" message
        // rather than a generic "provider unreachable".
        var factory = Substitute.For<IInvocationAgentFactory>();
        const string EjectMessage = "The model was ejected by the operator while this request was running.";
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new LlamaServerModelEjectedException(EjectMessage)));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == EjectMessage), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_WhenStreamIdleTimeout_KeepsThePathFreeWatchdogMessage()
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        // The stream idle watchdog's own message is already a fixed, path-free constant, so it is surfaced verbatim.
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new StreamIdleTimeoutException("The response stream stalled.")));

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(message => message == "The response stream stalled."), FailureCategory.Timeout);
    }

    /// <summary>
    ///     A provider that never produces its first output is not cut by the idle bound, since that wait is prefill, but the whole-turn deadline still
    ///     ends it and names itself.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenTheProviderNeverStartsItsRound_TheTurnDeadlineEndsIt()
    {
        var clock = new ManualTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            timeProvider: clock);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 300, streamIdleSeconds: 60).Build();

        var run = RunAsync(runner, package);
        await AssertEx.CompletesAsync(started.Task, TestBudgets.Contended, "the provider was never pulled");
        await AdvanceIdleWindowsUntilCompleteAsync(clock, run);
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the silent first round never hit the turn deadline");

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(),
            "Timed out: the response exceeded the node maximum message request timeout (300s).",
            FailureCategory.Timeout);
    }

    /// <summary>
    ///     A round's wait for its first output is prefill, not a stall: a re-prefill after a tool result took 43 s on a fast GPU, so a slower box lost
    ///     the turn at the 60 s idle bound. A segment's first round is the same wait.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_WhenARoundPrefillsLongerThanTheIdleTimeout_TheTurnCompletes(bool afterToolResult)
    {
        var clock = new ManualTimeProvider();
        var prefillDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefillStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(token => SlowPrefillUpdates(afterToolResult, prefillDone.Task, prefillStarted, token)),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            timeProvider: clock);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("slow-tool").WithTimeout(invocationSeconds: IdleOnlyTurnSeconds, streamIdleSeconds: 60).Build();

        var run = RunAsync(runner, package);
        await AssertEx.CompletesAsync(prefillStarted.Task, TestBudgets.Contended, "the provider round never started its prefill");
        for (var window = 0; window < 3; window++)
        {
            await WaitForIdleAndTurnDeadlineAsync(clock, "the idle deadline is armed");
            clock.Advance(TimeSpan.FromSeconds(61));
        }

        await WaitForIdleAndTurnDeadlineAsync(clock, "the deadline re-armed during the prefill");
        prefillDone.SetResult();
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the turn did not complete once the round streamed");

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    /// <summary>The negative control: once the round after a tool result has streamed, its silence is a stall again.</summary>
    [Test]
    public async Task RunAsync_WhenTheRoundAfterAToolResultStallsAfterItsFirstOutput_TheIdleDeadlineStillFails()
    {
        var clock = new ManualTimeProvider();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var textYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(token => ToolRoundThenSilentAfterTextUpdates(never.Task, textYielded, token)),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            timeProvider: clock);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("slow-tool").WithTimeout(invocationSeconds: IdleOnlyTurnSeconds, streamIdleSeconds: 60).Build();

        var run = RunAsync(runner, package);
        await AssertEx.CompletesAsync(textYielded.Task, TestBudgets.Contended, "the provider never pulled past the second round's text");
        var stallStartedAt = clock.GetUtcNow();
        await AdvanceIdleWindowsUntilCompleteAsync(clock, run);
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the second round's stall never hit the idle deadline");

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(static message => IsStreamIdleMessage(message)), FailureCategory.Timeout);
        AssertEx.Equal(TimeSpan.FromSeconds(61), clock.GetUtcNow() - stallStartedAt, "the stall took more than one idle window");
    }

    /// <summary>
    ///     A server-side tool that runs past the stream-idle timeout completes: the watchdog bounds provider silence, not tool execution.
    ///     An AgentHome run longer than 60 s therefore neither fails the turn nor cancels the run at the idle deadline.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenAToolRunsLongerThanTheIdleTimeout_TheTurnCompletes()
    {
        var clock = new ManualTimeProvider();
        var toolDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(token => SlowToolUpdates(toolDone.Task, toolStarted, token)),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            timeProvider: clock);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("slow-tool").WithTimeout(streamIdleSeconds: 60).Build();

        var run = RunAsync(runner, package);
        await AssertEx.CompletesAsync(toolStarted.Task, TestBudgets.Contended, "the provider never pulled past the tool call");
        for (var window = 0; window < 3; window++)
        {
            await WaitForIdleAndTurnDeadlineAsync(clock, "the idle deadline is armed");
            clock.Advance(TimeSpan.FromSeconds(61));
        }

        await WaitForIdleAndTurnDeadlineAsync(clock, "the deadline re-armed while the tool ran");
        toolDone.SetResult();
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the turn did not complete once the tool returned");

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Completed && payload.ToolCallId == "call-slow"));
    }

    /// <summary>The negative control on the same clock: provider silence with no tool open still times out.</summary>
    [Test]
    public async Task RunAsync_WhenTheProviderIsSilentWithNoToolOpen_TheIdleDeadlineStillFails()
    {
        var clock = new ManualTimeProvider();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var textYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(token => SilentAfterTextUpdates(never.Task, textYielded, token)),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            timeProvider: clock);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: IdleOnlyTurnSeconds, streamIdleSeconds: 60).Build();

        var run = RunAsync(runner, package);
        await AssertEx.CompletesAsync(textYielded.Task, TestBudgets.Contended, "the provider never pulled past its text");
        var stallStartedAt = clock.GetUtcNow();
        await AdvanceIdleWindowsUntilCompleteAsync(clock, run);
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the silent provider never hit the idle deadline");

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(static message => IsStreamIdleMessage(message)), FailureCategory.Timeout);
        AssertEx.Equal(TimeSpan.FromSeconds(61), clock.GetUtcNow() - stallStartedAt, "the stall took more than one idle window");
    }

    /// <summary>
    ///     Tool time is not charged to the turn: 50 s of model time plus 122 s of tool time outlives a 150 s turn budget, so an AgentHome run whose
    ///     own bound equals the turn's is not cut.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenModelAndToolTimeTogetherExceedTheTurnBudget_TheTurnCompletes()
    {
        var clock = new ManualTimeProvider();
        var modelDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thinking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(CreateFactory(token => ThinkThenSlowToolUpdates(modelDone.Task, toolDone.Task, thinking, toolStarted, token)),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            timeProvider: clock);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("slow-tool").WithTimeout(invocationSeconds: 150, streamIdleSeconds: 60).Build();

        var run = RunAsync(runner, package);
        await AssertEx.CompletesAsync(thinking.Task, TestBudgets.Contended, "the provider never pulled past its first text");
        await WaitForIdleAndTurnDeadlineAsync(clock, "the model is thinking");
        clock.Advance(TimeSpan.FromSeconds(50));
        modelDone.SetResult();
        await AssertEx.EventuallyAsync(() => dispatcher.ReceivedCalls().Any(static call => call.GetMethodInfo().Name == nameof(IWorkerEventDispatcher.ReportToolCallLifecycleAsync)),
            TimeSpan.FromSeconds(10),
            "the tool call was requested");
        await AssertEx.CompletesAsync(toolStarted.Task, TestBudgets.Contended, "the provider never pulled past the tool call");
        for (var window = 0; window < 2; window++)
        {
            await WaitForIdleAndTurnDeadlineAsync(clock, "the tool is running");
            clock.Advance(TimeSpan.FromSeconds(61));
        }

        await WaitForIdleAndTurnDeadlineAsync(clock, "still running");
        toolDone.SetResult();
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the turn did not complete once the tool returned");

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    /// <summary>
    ///     An approved call whose result the framework never streams must not keep the idle bound off: the resume segment's provider text proves the
    ///     call finished, so a stall after it still times out.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenAnApprovedCallIsNeverResulted_AStallAfterProviderTextStillTimesOut()
    {
        var clock = new ManualTimeProvider();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeTextYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var segment = 0;
        var factory = CreateFactory(token =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : SilentAfterTextUpdates(never.Task, resumeTextYielded, token);
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, providerStreamResilience: NoRetryResilience(), timeProvider: clock);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: IdleOnlyTurnSeconds, streamIdleSeconds: 60).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var run = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(10));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await AssertEx.CompletesAsync(resumeTextYielded.Task, TestBudgets.Contended, "the resume segment never pulled past its text");
        var stallStartedAt = clock.GetUtcNow();
        await AdvanceIdleWindowsUntilCompleteAsync(clock, run);
        await AssertEx.CompletesAsync(run, TestBudgets.Contended, "the resume stall never hit the idle deadline");

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Is<string>(static message => IsStreamIdleMessage(message)), FailureCategory.Timeout);
        AssertEx.Equal(TimeSpan.FromSeconds(61), clock.GetUtcNow() - stallStartedAt, "the stall took more than one idle window");
    }
}
