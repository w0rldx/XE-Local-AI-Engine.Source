namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
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
        var runner = CreateRunner(sender, chatUpdates: CreateUpdates("Hello", " world"));
        var package = RuntimePackageBuilder.Valid().Build();

        await runner.RunAsync(package);

        AssertEx.Contains(sender.AcceptedInvocations, package.InvocationId);
    }

    [Test]
    public async Task RunAsync_ValidPackage_StreamsChunks()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, chatUpdates: CreateUpdates("Hello", " world"));

        await runner.RunAsync(RuntimePackageBuilder.Valid().Build());

        AssertEx.True(sender.SentChunks.Count >= 1);
        AssertEx.True(sender.SentChunks.Any(chunk => chunk.IsComplete));
    }

    [Test]
    public async Task RunAsync_ValidPackage_SendsCompletion()
    {
        var sender = new MockHubMessageSender();
        var runner = CreateRunner(sender, chatUpdates: CreateUpdates("Hello", " world"));
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
    public async Task RunAsync_ChatClientThrows_SendsInvocationFailed()
    {
        var sender = new MockHubMessageSender();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                  .Returns(_ => ThrowingUpdates(new InvalidOperationException("chat failed")));

        var runner = CreateRunner(sender, chatClient);
        var package = RuntimePackageBuilder.Valid().Build();

        await runner.RunAsync(package);

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.Error.Contains("chat failed", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_RespectsCancellationToken_StopsStreaming()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, chatUpdates: BlockingUpdates(gate.Task));
        var package = RuntimePackageBuilder.Valid().Build();
        using var cancellationTokenSource = new CancellationTokenSource();

        var runTask = runner.RunAsync(package, cancellationTokenSource.Token);
        await Task.Delay(20);
        await cancellationTokenSource.CancelAsync();
        gate.TrySetCanceled();
        await runTask;

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.Error.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task RunAsync_MapsSystemPromptToSystemRole()
    {
        var sender = new MockHubMessageSender();
        List<ChatMessage>? capturedMessages = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo =>
                  {
                      capturedMessages = callInfo.Arg<IEnumerable<ChatMessage>>().ToList();
                      return CreateUpdates("ok");
                  });

        var runner = CreateRunner(sender, chatClient);

        await runner.RunAsync(RuntimePackageBuilder.Valid().WithSystemPrompt("system prompt").Build());

        var first = AssertEx.NotNull(capturedMessages)[0];
        AssertEx.Equal(ChatRole.System, first.Role);
        AssertEx.Equal("system prompt", first.Text);
    }

    [Test]
    public async Task RunAsync_MapsConversationContextInSortOrder()
    {
        var sender = new MockHubMessageSender();
        List<ChatMessage>? capturedMessages = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo =>
                  {
                      capturedMessages = callInfo.Arg<IEnumerable<ChatMessage>>().ToList();
                      return CreateUpdates("ok");
                  });

        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("late")
                                           .WithConversationMessage(MessageRole.Assistant, "middle", 1)
                                           .WithConversationMessage(MessageRole.User, "early", -1)
                                           .Build();

        var runner = CreateRunner(sender, chatClient);
        await runner.RunAsync(package);

        var messages = AssertEx.NotNull(capturedMessages);
        AssertEx.Equal("You are helpful.", messages[0].Text);
        AssertEx.Equal("early", messages[1].Text);
        AssertEx.Equal("late", messages[2].Text);
        AssertEx.Equal("middle", messages[3].Text);
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
        }, chatUpdates: CreateUpdates(new string('x', (1024 * 1024) + 1)));
        var package = RuntimePackageBuilder.Valid().Build();

        await runner.RunAsync(package);

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.Error.Contains("Response size exceeded", StringComparison.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenAlreadyBusy_ThrowsInvalidOperationException()
    {
        var sender = new MockHubMessageSender();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(sender, chatUpdates: BlockingUpdates(gate.Task));

        var firstTask = runner.RunAsync(RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        await Task.Delay(20);

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
        var package = RuntimePackageBuilder.Valid().Build();
        var runner = CreateRunner(sender, chatUpdates: BlockingUpdates(gate.Task));

        var runTask = runner.RunAsync(package);
        await Task.Delay(20);
        runner.Cancel(package.InvocationId);
        gate.TrySetCanceled();
        await runTask;

        AssertEx.ContainsSingle(sender.SentFailures, failure => failure.InvocationId == package.InvocationId && failure.Error.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
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
        IChatClient? chatClient = null,
        IRuntimePackageValidator? validator = null,
        ICapabilityReporter? capabilityReporter = null,
        WorkerNodeOptions? workerOptions = null,
        IAsyncEnumerable<ChatResponseUpdate>? chatUpdates = null)
    {
        var resolvedChatClient = chatClient ?? Substitute.For<IChatClient>();
        if (chatUpdates is not null)
        {
            resolvedChatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                              .Returns(chatUpdates);
        }

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
            resolvedChatClient,
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

    private static async IAsyncEnumerable<ChatResponseUpdate> CreateUpdates(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> BlockingUpdates(Task gate, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk");
        await gate.WaitAsync(cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "tail");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowingUpdates(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
