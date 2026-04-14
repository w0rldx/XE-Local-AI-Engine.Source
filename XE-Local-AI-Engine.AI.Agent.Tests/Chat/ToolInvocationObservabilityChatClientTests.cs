namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolInvocationObservabilityChatClientTests
{
    [Test]
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_LogsToolInvocation()
    {
        using var innerClient = new FakeChatClient();
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("AgentRunToolInvoked", StringComparison.Ordinal)
                                                            && message.Contains("approve-job", StringComparison.Ordinal));
    }

    [Test]
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_CreatesToolInvocationActivity()
    {
        using var innerClient = new FakeChatClient();
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);
        var startedActivities = new List<string>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => startedActivities.Add(activity.OperationName)
        };

        ActivitySource.AddActivityListener(listener);

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        AssertEx.ContainsSingle(startedActivities, name => string.Equals(name, "AgentRun.ToolInvocation", StringComparison.Ordinal));
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionCallContent("call-1", "approve-job", new Dictionary<string, object?>
                    {
                        ["decision"] = true
                    })
                ]
            };
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

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
