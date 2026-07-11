namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolInvocationObservabilityChatClientTests
{
    [Test]
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_LogsToolInvocation()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new FakeChatClient(callId, "approve-job");
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
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_NeverLogsRawArgumentValues()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        const string secretArgumentValue = "super-secret-file-contents";
        using var innerClient = new FakeChatClient(callId, "approve-job", secretArgumentValue);
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        AssertEx.True(logger.Messages.Count > 0, "Expected at least one log message.");
        AssertEx.True(logger.Messages.TrueForAll(message => !message.Contains(secretArgumentValue, StringComparison.Ordinal)),
            "Raw tool argument value leaked into a log message.");
    }

    [Test]
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_LogsArgumentsLengthAndHashPrefix()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        const string argumentValue = "some-arg-value";
        using var innerClient = new FakeChatClient(callId, "approve-job", argumentValue);
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        var serializedArguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["decision"] = argumentValue
        });
        var expectedHashPrefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedArguments)))[..12];

        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("ArgsLength=", StringComparison.Ordinal)
                                                            && message.Contains($"ArgsHash={expectedHashPrefix}", StringComparison.Ordinal));
    }

    [Test]
    public async Task GetStreamingResponseAsync_LogsArgumentsLengthAsUtf8ByteCount()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        const string argumentValue = "some-arg-value";
        using var innerClient = new FakeChatClient(callId, "approve-job", argumentValue);
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        var serializedArguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["decision"] = argumentValue
        });
        var expectedByteCount = Encoding.UTF8.GetByteCount(serializedArguments);
        AssertEx.ContainsSingle(logger.Messages, message => message.Contains($"ArgsLength={expectedByteCount}", StringComparison.Ordinal));
    }

    [Test]
    public async Task GetStreamingResponseAsync_WhenArgumentsAreNotSerializable_LogsSentinelWithoutFaultingTheStream()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        // A reference cycle cannot be serialized; summarizing must fall back to the sentinel rather than throw.
        var cyclic = new List<object?>();
        cyclic.Add(cyclic);
        using var innerClient = new FakeChatClient(callId, "approve-job", cyclic);
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);

        var updateCount = 0;
        await foreach (var update in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(update);
            updateCount++;
        }

        AssertEx.True(updateCount > 0, "the response stream must still flow through when summarizing fails");
        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("ArgsLength=-1", StringComparison.Ordinal)
                                                            && message.Contains("ArgsHash=unserializable", StringComparison.Ordinal));
    }

    [Test]
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_CreatesToolInvocationActivity()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new FakeChatClient(callId, "approve-job");
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);
        var stoppedActivities = new List<(string OperationName, string? CallId, string? ToolName)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add((
                activity.OperationName,
                activity.GetTagItem("tool.call_id")?.ToString(),
                activity.GetTagItem("tool.name")?.ToString()))
        };

        ActivitySource.AddActivityListener(listener);

        // Prime the listener in-process before the wrapped client emits from the static production source.
        using (var probeSource = new ActivitySource("XE.LocalAiEngine.AI.Agent"))
        {
            using var probeActivity = probeSource.StartActivity("Probe");
            AssertEx.NotNull(probeActivity, "Expected ActivityListener probe activity to be created.");
        }

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        AssertEx.ContainsSingle(stoppedActivities,
            activity => string.Equals(activity.OperationName, "AgentRun.ToolInvocation", StringComparison.Ordinal)
                        && string.Equals(activity.CallId, callId, StringComparison.Ordinal)
                        && string.Equals(activity.ToolName, "approve-job", StringComparison.Ordinal));
    }

    [Test]
    public async Task GetStreamingResponseAsync_WhenSameCallIdSpansMultipleUpdates_LogsAndSpansOnce()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new RepeatingFunctionCallChatClient(callId, "approve-job", updateCount: 4);
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);
        // Filter by this test's unique CallId: the production ActivitySource is static, so a parallel test's spans
        // would otherwise be captured here too.
        var stoppedActivities = new List<(string OperationName, string? CallId)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add((activity.OperationName, activity.GetTagItem("tool.call_id")?.ToString()))
        };

        ActivitySource.AddActivityListener(listener);

        // Prime the listener in-process before the wrapped client emits from the static production source.
        using (var probeSource = new ActivitySource("XE.LocalAiEngine.AI.Agent"))
        {
            using var probeActivity = probeSource.StartActivity("Probe");
            AssertEx.NotNull(probeActivity, "Expected ActivityListener probe activity to be created.");
        }

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        // Four streamed updates carry one logical call under a single CallId: exactly one log line and one span.
        AssertEx.ContainsSingle(logger.Messages, message => message.Contains("AgentRunToolInvoked", StringComparison.Ordinal)
                                                            && message.Contains(callId, StringComparison.Ordinal));
        AssertEx.ContainsSingle(stoppedActivities,
            activity => string.Equals(activity.OperationName, "AgentRun.ToolInvocation", StringComparison.Ordinal)
                        && string.Equals(activity.CallId, callId, StringComparison.Ordinal));
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _callId;
        private readonly string _toolName;
        private readonly object? _argumentValue;

        public FakeChatClient(string callId, string toolName, object? argumentValue = null)
        {
            _callId = callId ?? throw new ArgumentNullException(nameof(callId));
            _toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
            _argumentValue = argumentValue ?? true;
        }

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
                    new FunctionCallContent(_callId, _toolName, new Dictionary<string, object?>
                    {
                        ["decision"] = _argumentValue
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

    private sealed class RepeatingFunctionCallChatClient : IChatClient
    {
        private readonly string _callId;
        private readonly string _toolName;
        private readonly int _updateCount;

        public RepeatingFunctionCallChatClient(string callId, string toolName, int updateCount)
        {
            _callId = callId ?? throw new ArgumentNullException(nameof(callId));
            _toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
            _updateCount = updateCount;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            // One logical tool call whose argument fragments stream across several updates under a single CallId.
            for (var index = 0; index < _updateCount; index++)
            {
                await Task.Yield();

                yield return new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent(_callId, _toolName, new Dictionary<string, object?>
                        {
                            ["decision"] = true
                        })
                    ]
                };
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
