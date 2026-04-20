namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalAgentChatServiceTests
{
    private static readonly IDisposable NoopScope = new NoopDisposable();

    [Test]
    public async Task SetModelAsync_UpdatesSelectedModel()
    {
        using var chatClient = new FakeChatClient();
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        await sut.SetModelAsync("custom-model");

        AssertEx.Equal("custom-model", sut.SelectedModel);
    }

    [Test]
    public async Task SendMessageAsync_UsesPendingModelBeforeFirstSend()
    {
        using var chatClient = new FakeChatClient();
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        await sut.SetModelAsync("custom-model");
        await DrainAsync(sut.SendMessageAsync("hello"));

        AssertEx.Equal("custom-model", chatClient.Requests.Single().Options?.ModelId);
    }

    [Test]
    public async Task SetModelAsync_AfterFirstSend_ResetsHistoryForNextRun()
    {
        using var chatClient = new FakeChatClient();
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        await DrainAsync(sut.SendMessageAsync("first"));
        await sut.SetModelAsync("other-model");
        await DrainAsync(sut.SendMessageAsync("second"));

        AssertEx.Equal(2, chatClient.Requests.Count);
        AssertEx.Equal("other-model", chatClient.Requests[1].Options?.ModelId);
    }

    [Test]
    public async Task SendMessageAsync_RejectsConcurrentCalls()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var chatClient = new FakeChatClient(async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return ["done"];
        });
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        var firstSend = ConsumeAsync(sut.SendMessageAsync("first"));
        await Task.Delay(50);

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(sut.SendMessageAsync("second")));

        AssertEx.Contains(exception.Message, "already in progress");

        release.SetResult();
        await firstSend;
    }

    [Test]
    public async Task DisposeAsync_AfterSend_LogsDisposeReset()
    {
        using var chatClient = new FakeChatClient();
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        await DrainAsync(sut.SendMessageAsync("hello"));

        await sut.DisposeAsync();

        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("AgentSessionReset", StringComparison.Ordinal)
                                                            && message.Contains("Dispose", StringComparison.Ordinal));

        var exception = await AssertEx.ThrowsAsync<ObjectDisposedException>(() => sut.SetModelAsync("other-model"));
        AssertEx.Contains(exception.ObjectName ?? string.Empty, nameof(LocalAgentChatService));
    }

    [Test]
    public async Task ResetSessionAsync_BeforeFirstSend_IsNoOp()
    {
        using var chatClient = new FakeChatClient();
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        await sut.ResetSessionAsync();

        AssertEx.Equal("qwen3.5:0.8b", sut.SelectedModel);
        AssertEx.False(logger.Messages.Any(message => message.Contains("AgentSessionReset", StringComparison.Ordinal)));
    }

    [Test]
    public async Task SendMessageAsync_LogsRunCompletedMetrics()
    {
        using var chatClient = new FakeChatClient();
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        await DrainAsync(sut.SendMessageAsync("hello"));

        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("AgentRunCompleted", StringComparison.Ordinal)
                                                            && message.Contains("TokenCount", StringComparison.Ordinal)
                                                            && message.Contains("DurationMs", StringComparison.Ordinal));
    }

    [Test]
    public async Task SendMessageAsync_WhenStreamFails_LogsRunFailed()
    {
        using var chatClient = new FakeChatClient(_ => throw new InvalidOperationException("stream failed"));
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => DrainAsync(sut.SendMessageAsync("hello")));

        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("AgentRunFailed", StringComparison.Ordinal));
    }

    [Test]
    public async Task SendMessageAsync_WhenStreamHasNoVisibleText_YieldsFallbackMessage()
    {
        using var chatClient = new FakeChatClient(_ => Task.FromResult<IReadOnlyList<ChatResponseUpdate>>([
            new ChatResponseUpdate { Contents = [] }
        ]));
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        var updates = await CollectAsync(sut.SendMessageAsync("hello"));

        AssertEx.Equal(1, updates.Count);
        AssertEx.Contains(updates[0], "without returning visible text");
        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("AgentRunCompletedWithoutVisibleText", StringComparison.Ordinal));
    }

    [Test]
    public async Task SendMessageAsync_WhenStreamHasNonTextContent_LogsNonTextUpdate()
    {
        using var chatClient = new FakeChatClient(_ => Task.FromResult<IReadOnlyList<ChatResponseUpdate>>([
            new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionCallContent("call-1", "approve-job", new Dictionary<string, object?>())
                ]
            }
        ]));
        var logger = new ListLogger<LocalAgentChatService>();
        await using var sut = CreateSut(chatClient, logger);

        var updates = await CollectAsync(sut.SendMessageAsync("hello"));

        AssertEx.Equal(1, updates.Count);
        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("AgentRunReceivedNonTextUpdate", StringComparison.Ordinal)
                                                            && message.Contains("FunctionCallContent", StringComparison.Ordinal));
    }

    private static ILocalAgentChatService CreateSut(FakeChatClient chatClient, ILogger<LocalAgentChatService> logger)
    {
        var options = new LocalChatAgentOptions();
        return new LocalAgentChatService(chatClient,
            Options.Create(options),
            new FakeInstructionProvider(),
            new FakeToolRegistry(),
            logger,
            NullLoggerFactory.Instance,
            FakeServiceProvider.Instance);
    }

    private static async Task DrainAsync(IAsyncEnumerable<string> updates)
    {
        await foreach (var text in updates)
        {
            GC.KeepAlive(text);
        }
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<string> updates)
    {
        await foreach (var text in updates)
        {
            GC.KeepAlive(text);
        }
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> updates)
    {
        var values = new List<string>();

        await foreach (var text in updates)
        {
            values.Add(text);
        }

        return values;
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        public static FakeServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    private sealed class FakeInstructionProvider : IAgentInstructionProvider
    {
        public string GetLocalChatInstructions()
        {
            return "You are helpful.";
        }
    }

    private sealed class FakeToolRegistry : IAgentToolRegistry
    {
        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return [];
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>>? _onStream;

        public FakeChatClient(Func<CancellationToken, Task<IReadOnlyList<string>>>? onStream)
            : this(onStream is null ? null : async cancellationToken =>
            {
                var chunks = await onStream(cancellationToken);
                return chunks.Select(static chunk => new ChatResponseUpdate(ChatRole.Assistant, chunk)).ToArray();
            })
        {
        }

        public FakeChatClient(Func<CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>>? onStream = null)
        {
            _onStream = onStream;
        }

        public List<ChatRequestRecord> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new ChatRequestRecord(messages.ToList(), options));

            var updates = _onStream is null
                ? [new ChatResponseUpdate(ChatRole.Assistant, "ok")]
                : await _onStream(cancellationToken);

            foreach (var update in updates)
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    private sealed record ChatRequestRecord(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NoopScope;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
