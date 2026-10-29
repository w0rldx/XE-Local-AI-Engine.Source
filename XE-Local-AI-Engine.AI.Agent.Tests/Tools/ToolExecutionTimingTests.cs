namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The function invoker set in the decorated pipeline times the tool alone, from the registered
///     <see cref="TimeProvider" />, into <see cref="ToolExecutionTimingScope" />, and leaves error handling to the framework.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ToolExecutionTimingTests
{
    private const string ToolName = "timed_tool";

    [Test]
    public async Task FunctionInvoker_RecordsElapsedFromTimeProviderUnderTheCallId()
    {
        var time = new ManualTimeProvider();
        var callId = $"call-{Guid.NewGuid():N}";
        var tool = AIFunctionFactory.Create(() =>
            {
                time.Advance(TimeSpan.FromMilliseconds(250));
                return "tool result";
            },
            ToolName);
        using var model = new ScriptedChatClient(callId, () => time.Advance(TimeSpan.FromSeconds(5)));
        await using var provider = BuildProvider(model, time);

        long? recorded;
        using (ToolExecutionTimingScope.Open())
        {
            _ = await SendAsync(provider.GetRequiredService<IChatClient>(), tool);
            recorded = ToolExecutionTimingScope.GetOrNull(callId);
        }

        AssertEx.Equal(expected: 2, model.CallCount, "a tool round and an answer round must reach the model");
        AssertEx.Equal(expected: 250L, recorded, "the tool's own time, not the model's 5 s around it");
    }

    [Test]
    public async Task FunctionInvoker_WhenToolThrows_RecordsAndRethrows()
    {
        var time = new ManualTimeProvider();
        var callId = $"call-{Guid.NewGuid():N}";
        var failure = new InvalidOperationException("tool failed");
        var tool = AIFunctionFactory.Create(string () =>
            {
                time.Advance(TimeSpan.FromMilliseconds(40));
                throw failure;
            },
            ToolName);
        using var model = new ScriptedChatClient(callId, onToolRound: null);
        await using var provider = BuildProvider(model, time);

        long? recorded;
        using (ToolExecutionTimingScope.Open())
        {
            _ = await SendAsync(provider.GetRequiredService<IChatClient>(), tool);
            recorded = ToolExecutionTimingScope.GetOrNull(callId);
        }

        AssertEx.Equal(expected: 40L, recorded, "a failing call is still timed");
        var result = AssertEx.NotNull(model.ReturnedResult, "the framework must hand the failed result back to the model");
        AssertEx.True(ReferenceEquals(failure, result.Exception), "the invoker must rethrow the tool's own exception for the framework to report");
    }

    [Test]
    public async Task FunctionInvoker_OutsideAScope_RecordsNothing()
    {
        var time = new ManualTimeProvider();
        var callId = $"call-{Guid.NewGuid():N}";
        var executions = 0;
        var tool = AIFunctionFactory.Create(() =>
            {
                executions++;
                time.Advance(TimeSpan.FromMilliseconds(10));
                return "tool result";
            },
            ToolName);
        using var model = new ScriptedChatClient(callId, onToolRound: null);
        await using var provider = BuildProvider(model, time);

        var response = await SendAsync(provider.GetRequiredService<IChatClient>(), tool);

        AssertEx.Equal("done", response.Text);
        AssertEx.Equal(expected: 1, executions, "the tool still runs with no scope open");
        AssertEx.Null(ToolExecutionTimingScope.GetOrNull(callId), "no scope means nothing to record into and nothing to read");
    }

    [Test]
    public async Task Pipeline_KeepsAllowConcurrentInvocationFalse()
    {
        using var model = new ScriptedChatClient("unused", onToolRound: null);
        await using var provider = BuildProvider(model, new ManualTimeProvider());

        var functionInvoking = AssertEx.NotNull(provider.GetRequiredService<IChatClient>().GetService<FunctionInvokingChatClient>());

        // Serial execution is what lets the per-run tool time be a plain sum of call durations.
        AssertEx.False(functionInvoking.AllowConcurrentInvocation);
        AssertEx.NotNull(functionInvoking.FunctionInvoker, "the timing invoker must be installed");
    }

    private static ServiceProvider BuildProvider(IChatClient model, TimeProvider time)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _ = services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _ = services.AddSingleton(time);
        _ = services.AddSingleton(model);
        _ = services.DecorateChatClientPipeline();
        return services.BuildServiceProvider();
    }

    private static Task<ChatResponse> SendAsync(IChatClient client, AITool tool)
    {
        return client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "call the tool")], new ChatOptions
        {
            Tools = [tool]
        }).ToChatResponseAsync();
    }

    // Round one calls the tool under the given id; round two records the result it was handed and answers.
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly string _callId;
        private readonly Action? _onToolRound;

        public ScriptedChatClient(string callId, Action? onToolRound)
        {
            _callId = callId;
            _onToolRound = onToolRound;
        }

        public int CallCount { get; private set; }

        public FunctionResultContent? ReturnedResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReturnedResult = messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().FirstOrDefault();
            if (ReturnedResult is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }

            _onToolRound?.Invoke();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(_callId, ToolName)])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}
