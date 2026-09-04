namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
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

    /// <summary>
    ///     The budget keeps the NAME only when the request offered a tool by that name. A model can emit any string in
    ///     a function call, and what the budget's name set feeds is durable — the persisted work-session step detail,
    ///     and from there an event detail and a node-run column — so an identifier nothing offered would be recorded as
    ///     a tool this run reached for. The call is still counted; only the name is dropped.
    /// </summary>
    [Test]
    public async Task GetStreamingResponseAsync_RecordsOnlyToolNamesTheRequestActuallyOffered()
    {
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions());
        var budget = ProviderCallBudget.Current!;
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new FakeChatClient(callId, "definitely_not_offered");
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, new ListLogger<ToolInvocationObservabilityChatClient>());
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(static () => "ok", "approve-job")]
        };

        await foreach (var update in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], options))
        {
            GC.KeepAlive(update);
        }

        AssertEx.Empty(budget.ToolNames, "A name that resolved against no offered tool must not be recorded as a tool this run called.");
        AssertEx.Equal(expected: 1, budget.CaptureEfficiencySnapshot().ToolCallsRequested, "The call itself still counts — it happened.");
    }

    /// <summary>The other arm: a name the request DID offer is recorded, so the gate does not simply record nothing.</summary>
    [Test]
    public async Task GetStreamingResponseAsync_RecordsAToolNameTheRequestOffered()
    {
        using var scope = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions());
        var budget = ProviderCallBudget.Current!;
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new FakeChatClient(callId, "approve-job");
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, new ListLogger<ToolInvocationObservabilityChatClient>());
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(static () => "ok", "approve-job")]
        };

        await foreach (var update in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], options))
        {
            GC.KeepAlive(update);
        }

        AssertEx.Equal("approve-job", string.Join(",", budget.ToolNames));
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
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_CreatesToolCallRequestedActivity()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new FakeChatClient(callId, "approve-job");
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);
        // The production ActivitySource is process-static and TUnit runs tests in parallel, so this listener sees EVERY
        // test's spans on other threads too — a ConcurrentQueue is required, a plain List corrupts under concurrent Add
        // (see sibling tests below, which filter by CallId and use ConcurrentQueue for the same reason).
        var stoppedActivities = new ConcurrentQueue<(string OperationName, string? CallId, string? ToolName)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Enqueue((
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
            activity => string.Equals(activity.OperationName, "AgentRun.ToolCallRequested", StringComparison.Ordinal)
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
        // would otherwise be captured here too. A ConcurrentQueue is required (not a plain List): other tests' spans
        // stop concurrently on other threads and race a non-thread-safe Add.
        var stoppedActivities = new ConcurrentQueue<(string OperationName, string? CallId)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Enqueue((activity.OperationName, activity.GetTagItem("tool.call_id")?.ToString()))
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
            activity => string.Equals(activity.OperationName, "AgentRun.ToolCallRequested", StringComparison.Ordinal)
                        && string.Equals(activity.CallId, callId, StringComparison.Ordinal));
    }

    [Test]
    public async Task GetStreamingResponseAsync_WhenResultFollowsCall_EmitsSuccessOutcomeSpanWithMatchingCallId()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new CallThenResultChatClient(callId, "approve-job", result: "done", exception: null);
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);
        // The production ActivitySource is process-static and TUnit runs tests in parallel, so this listener sees EVERY
        // test's spans. Capture ONLY this test's completion span (filtered by our unique CallId) into a thread-safe
        // queue, so a sibling test's concurrent span can neither race the collection nor satisfy the assertion.
        var completed = new ConcurrentQueue<(string? Outcome, double? DurationMs)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(activity.OperationName, "AgentRun.ToolCallCompleted", StringComparison.Ordinal)
                    && string.Equals(activity.GetTagItem("tool.call_id")?.ToString(), callId, StringComparison.Ordinal))
                {
                    completed.Enqueue((activity.GetTagItem("tool.outcome")?.ToString(),
                        activity.GetTagItem("tool.duration_ms") as double?));
                }
            }
        };

        ActivitySource.AddActivityListener(listener);

        using (var probeSource = new ActivitySource("XE.LocalAiEngine.AI.Agent"))
        {
            using var probeActivity = probeSource.StartActivity("Probe");
            AssertEx.NotNull(probeActivity, "Expected ActivityListener probe activity to be created.");
        }

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        AssertEx.ContainsSingle(completed, entry => string.Equals(entry.Outcome, "success", StringComparison.Ordinal)
                                                    && entry.DurationMs is >= 0);
    }

    [Test]
    public async Task GetStreamingResponseAsync_WhenResultCarriesException_EmitsErrorOutcomeSpan()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new CallThenResultChatClient(callId, "approve-job", result: null, exception: new InvalidOperationException("boom"));
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);
        // Capture only this test's completion span (unique CallId) into a thread-safe queue — see the success test.
        var completed = new ConcurrentQueue<string?>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(activity.OperationName, "AgentRun.ToolCallCompleted", StringComparison.Ordinal)
                    && string.Equals(activity.GetTagItem("tool.call_id")?.ToString(), callId, StringComparison.Ordinal))
                {
                    completed.Enqueue(activity.GetTagItem("tool.outcome")?.ToString());
                }
            }
        };

        ActivitySource.AddActivityListener(listener);

        using (var probeSource = new ActivitySource("XE.LocalAiEngine.AI.Agent"))
        {
            using var probeActivity = probeSource.StartActivity("Probe");
            AssertEx.NotNull(probeActivity, "Expected ActivityListener probe activity to be created.");
        }

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            GC.KeepAlive(_);
        }

        AssertEx.ContainsSingle(completed, outcome => string.Equals(outcome, "error", StringComparison.Ordinal));
        // The raw result telemetry must never leak the exception message.
        AssertEx.True(logger.Messages.TrueForAll(message => !message.Contains("boom", StringComparison.Ordinal)),
            "The tool result span/log must never carry the raw exception content.");
    }

    [Test]
    public async Task GetStreamingResponseAsync_WhenResultFollowsCall_RecordsLogicalToolCostOnce()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        using var innerClient = new CallThenResultChatClient(callId, "approve-job", result: "done", exception: null);
        var logger = new ListLogger<ToolInvocationObservabilityChatClient>();
        using var sut = new ToolInvocationObservabilityChatClient(innerClient, logger);
        ProviderCallEfficiencySnapshot snapshot;

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions()))
        {
            await foreach (var update in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                GC.KeepAlive(update);
            }

            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal(expected: 1, snapshot.ToolCallsRequested);
        AssertEx.Equal(expected: 1, snapshot.ToolCallsCompleted);
        AssertEx.Equal(expected: 0, snapshot.ToolCallsFailed);
        AssertEx.True(snapshot.ToolResultBytes > 0);
        AssertEx.True(snapshot.ToolRequestToResultMs >= 0);
        AssertEx.True(snapshot.TimeToFirstToolRequestMs is >= 0);
    }

    private sealed class CallThenResultChatClient : IChatClient
    {
        private readonly string _callId;
        private readonly string _toolName;
        private readonly object? _result;
        private readonly Exception? _exception;

        public CallThenResultChatClient(string callId, string toolName, object? result, Exception? exception)
        {
            _callId = callId ?? throw new ArgumentNullException(nameof(callId));
            _toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
            _result = result;
            _exception = exception;
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
                        ["decision"] = true
                    })
                ]
            };

            // The function-invocation middleware would execute the tool below this hop; simulate the result update it
            // then streams back so the observability client can correlate and time the call.
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionResultContent(_callId, _result)
                    {
                        Exception = _exception
                    }
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
