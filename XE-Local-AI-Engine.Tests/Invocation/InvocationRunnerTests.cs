namespace XE_Local_AI_Engine.Tests.Invocation;

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
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Invocation;
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

        await runner.RunAsync(package);

        AssertEx.Contains(sender.AcceptedInvocations, package.InvocationId);
    }

    [Test]
    public async Task RunAsync_ValidPackage_StreamsChunks()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("Hello", " world"));

        await runner.RunAsync(RuntimePackageBuilder.Valid().Build());

        AssertEx.True(sender.SentChunks.Count >= 1);
        AssertEx.True(sender.SentChunks.Any(chunk => chunk.IsComplete));
    }

    [Test]
    public async Task RunAsync_ValidPackage_SendsCompletion()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("Hello", " world"));
        var package = RuntimePackageBuilder.Valid().Build();

        await runner.RunAsync(package);

        AssertEx.Equal(1, sender.SentCompletions.Count);
        AssertEx.Equal(package.InvocationId, sender.SentCompletions[0].InvocationId);
        AssertEx.Equal("Hello world", sender.SentCompletions[0].FinalContent);
    }

    [Test]
    public async Task RunAsync_ValidationFails_ThrowsInvalidOperationException()
    {
        var sender = new MockHubMessageSender();
        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(new RuntimePackageValidationResult(false, ["bad package"]));

        var runner = CreateRunner(sender, validator: validator);
        var package = RuntimePackageBuilder.Valid().Build();

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(package));

        AssertEx.Contains(exception.Message, "bad package");
        AssertEx.Empty(sender.SentFailures);
    }

    [Test]
    public async Task RunAsync_AgentRuntimeThrows_SendsInvocationFailed()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NotSupportedException("factory failed")));

        var runner = CreateRunner(sender, factory);
        var package = RuntimePackageBuilder.Valid().Build();

        await runner.RunAsync(package);

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId
                                                                && failure.FailureCategory == nameof(FailureCategory.AgentRuntime)
                                                                && failure.Error.Contains("Agent runtime error", StringComparison.Ordinal));
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

        var runTask = runner.RunAsync(package, cancellationTokenSource.Token);
        await started.Task;
        await cancellationTokenSource.CancelAsync();
        gate.TrySetCanceled();
        await runTask;

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.FailureCategory == nameof(FailureCategory.Cancelled));
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
        await runner.RunAsync(package);

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
        var runner = CreateRunner(sender, agentUpdates: CreateUpdates("ok"));

        await runner.RunAsync(RuntimePackageBuilder.Valid().Build());

        AssertEx.True(FakeAIAgent.LastObservedSessionWasNull);
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

        await runner.RunAsync(package);

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.Error.Contains("Response size exceeded", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenAlreadyBusy_ThrowsInvalidOperationException()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, agentUpdates: BlockingUpdates(gate.Task, started));

        var firstTask = runner.RunAsync(RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        await started.Task;

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build()));
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

        var runTask = runner.RunAsync(package);
        await started.Task;
        runner.Cancel(package.InvocationId);
        gate.TrySetCanceled();
        await runTask;

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.FailureCategory == nameof(FailureCategory.Cancelled));
    }

    [Test]
    public async Task RunAsync_WhenInvocationTimeoutElapses_MapsTimeoutFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().WithTimeout(0).Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, agentUpdates: BlockingUpdates(gate.Task, started));

        await started.Task;
        await runner.RunAsync(package);

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.FailureCategory == nameof(FailureCategory.Timeout));
    }

    [Test]
    public async Task RunAsync_WhenProviderUnreachable_MapsFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new HttpRequestException("offline")));

        var runner = CreateRunner(sender, factory);

        await runner.RunAsync(RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.FailureCategory == nameof(FailureCategory.ProviderUnreachable));
    }

    [Test]
    public async Task RunAsync_WhenUnexpected_MapsFailureCategory()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new InvalidOperationException("boom")));

        var runner = CreateRunner(sender, factory);

        await runner.RunAsync(RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.FailureCategory == nameof(FailureCategory.Unexpected));
    }

    [Test]
    public async Task RunAsync_WhenAgentRuntimeMessageContainsFrameworkType_RedactsFrameworkNames()
    {
        var sender = new MockHubMessageSender();
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<InvocationAgentContext>(new NotSupportedException("Microsoft.Agents.AI.ChatClientAgentException: provider blew up")));

        var runner = CreateRunner(sender, factory);

        await runner.RunAsync(RuntimePackageBuilder.Valid().Build());

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.FailureCategory == nameof(FailureCategory.AgentRuntime)
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

        await runner.RunAsync(RuntimePackageBuilder.Valid().Build());

        var failure = sender.SentFailures.Single();
        AssertEx.Equal(512, failure.Error.Length);
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenResultResolved_ReturnsResult()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender);
        var invocationId = Guid.NewGuid();

        var task = runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}");
        await Task.Delay(20);

        var requestId = sender.SentToolCalls.Single().RequestId;
        runner.ResolveToolCallResult(new ToolCallResultEvent
        {
            RequestId = requestId,
            Result = "done"
        });

        AssertEx.Equal("done", await task);
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenToolReturnsError_ThrowsWorkerToolCallException()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender);
        var invocationId = Guid.NewGuid();

        var task = runner.ExecuteApiToolCallAsync(invocationId, "test-tool", "{}");
        await Task.Delay(20);

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
        await Task.Delay(20);

        runner.CancelAll();

        await AssertEx.ThrowsAsync<TaskCanceledException>(() => pendingCall);
    }

    [Test]
    public async Task RunAsync_WhenToolBridgeFails_MapsAgentToolCallCategory()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, agentUpdates: CreateToolCallingUpdates("approve-job", "{\"decision\":true}"));
        var package = RuntimePackageBuilder.Valid()
                                           .WithAllowedTool("approve-job")
                                           .Build();

        var runTask = runner.RunAsync(package);
        await Task.Delay(20);

        var requestId = sender.SentToolCalls.Single().RequestId;
        runner.ResolveToolCallResult(new ToolCallResultEvent
        {
            RequestId = requestId,
            Result = string.Empty,
            Error = "approval timeout"
        });

        await runTask;

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.FailureCategory == nameof(FailureCategory.AgentToolCall));
    }

    [Test]
    public void InvocationFailedPayload_SerializesFailureCategoryAsPascalCaseString()
    {
        var payload = new InvocationFailedPayload
        {
            InvocationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Error = "Invocation timed out after 30 seconds.",
            FailureCategory = nameof(FailureCategory.Timeout)
        };

        var json = JsonSerializer.Serialize(payload);
        var roundTrip = JsonSerializer.Deserialize<InvocationFailedPayload>(json);

        AssertEx.Contains(json, "\"failureCategory\":\"Timeout\"");
        AssertEx.Equal(nameof(FailureCategory.Timeout), AssertEx.NotNull(roundTrip).FailureCategory);
    }

    [Test]
    public async Task ExecuteApiToolCallAsync_WhenTimedOut_ThrowsTaskCanceledException()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, workerOptions: new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 0
        });

        await AssertEx.ThrowsAsync<TaskCanceledException>(() => runner.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}"));
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
        await Task.Delay(20);
        runner.CleanupStaleToolCalls(TimeSpan.Zero);

        await AssertEx.ThrowsAsync<TimeoutException>(() => task);
    }

    private static InvocationRunner CreateRunner(MockHubMessageSender sender,
        IInvocationAgentFactory? invocationAgentFactory = null,
        IRuntimePackageValidator? validator = null,
        ICapabilityReporter? capabilityReporter = null,
        WorkerNodeOptions? workerOptions = null,
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

        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Ollama:ChatModel"] = "qwen3.5:9b"
                            })
                            .Build();

        return new InvocationRunner(new Lazy<IHubMessageSender>(() => sender),
            resolvedFactory,
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

    private static IInvocationAgentFactory CreateFactory(IAsyncEnumerable<AgentResponseUpdate> updates, Action<InvocationAgentDefinition>? onCreate = null)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   onCreate?.Invoke(definition);
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updates),
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

    private static async IAsyncEnumerable<AgentResponseUpdate> BlockingUpdates(Task gate, TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "chunk");
        await gate.WaitAsync(cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "tail");
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> CreateToolCallingUpdates(string toolName,
        string arguments,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        yield return new AgentResponseUpdate
        {
            Contents =
            [
                new FunctionCallContent(Guid.NewGuid().ToString("N"), toolName, new Dictionary<string, object?>
                {
                    ["arguments"] = arguments
                })
            ]
        };

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class FakeAIAgent : AIAgent
    {
        private readonly IAsyncEnumerable<AgentResponseUpdate> _updates;

        public FakeAIAgent(IAsyncEnumerable<AgentResponseUpdate> updates)
        {
            _updates = updates;
        }

        public static bool LastObservedSessionWasNull { get; private set; }

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
            LastObservedSessionWasNull = session is null;
            return _updates;
        }
    }

    private sealed class FakeAgentSession : AgentSession
    {
    }
}
