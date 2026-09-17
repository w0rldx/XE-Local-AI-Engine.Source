namespace XE_Local_AI_Engine.AI.Agent.Tests.DependencyInjection;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class AgentToolPipelinePolicyTests
{
    private const string ToolName = "harmless_tool";

    // real-timer: failure deadline for an in-memory controlled-gate deadlock; ordering is asserted through the gates.
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HarmlessToolThenAnswer_CompletesWithinBudget(bool streaming)
    {
        var executions = 0;
        var tool = AIFunctionFactory.Create(() =>
            {
                _ = Interlocked.Increment(ref executions);
                return "tool result";
            },
            ToolName);
        using var inner = new ScriptedChatClient((call, _, _) =>
            call == 1 ? FunctionCall(ToolName, call) : FinalAnswer());
        using var provider = BuildProvider(inner);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;
        ChatResponse response;

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
        {
            response = await SendAsync(client, Options(tool), streaming);
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal("done", response.Text);
        AssertEx.Equal(expected: 1, executions, "the harmless tool must execute exactly once");
        AssertEx.Equal(expected: 2, inner.CallCount, "one tool round and one final-answer round must reach the provider");
        AssertEx.Equal(expected: 2, snapshot.ProviderCalls, "both accepted provider rounds must be recorded");
        AssertEx.Equal(expected: 0, snapshot.ProviderRoundsRejected, "the successful trajectory must not reject a provider round");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IterationLimit_AllowsFinalFunctionFreeRequest(bool streaming)
    {
        var executions = 0;
        var tool = AIFunctionFactory.Create(() =>
            {
                _ = Interlocked.Increment(ref executions);
                return "tool result";
            },
            ToolName);
        using var inner = new ScriptedChatClient((call, _, options) =>
            options?.Tools?.OfType<AIFunction>().Any() == true ? FunctionCall(ToolName, call) : FinalAnswer());
        using var provider = BuildProvider(inner, maximumIterations: 2);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;
        ChatResponse response;

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
        {
            response = await SendAsync(client, Options(tool), streaming);
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal("done", response.Text);
        AssertEx.Equal(expected: 2, executions, "N=2 must permit exactly two callable iterations");
        AssertEx.Equal(expected: 3, inner.CallCount, "the function-free final request is separate from the two callable iterations");
        AssertEx.Equal(expected: 3, snapshot.ProviderCalls, "all three accepted provider requests must be counted");
        AssertEx.Equal(expected: 0, snapshot.ProviderRoundsRejected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProviderCap_RejectsBeforeFinalRequest(bool streaming)
    {
        var executions = 0;
        var tool = AIFunctionFactory.Create(() =>
            {
                _ = Interlocked.Increment(ref executions);
                return "tool result";
            },
            ToolName);
        using var inner = new ScriptedChatClient((call, _, options) =>
            options?.Tools?.OfType<AIFunction>().Any() == true ? FunctionCall(ToolName, call) : FinalAnswer());
        using var provider = BuildProvider(inner, maximumIterations: 2);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;

        using (ProviderCallBudget.BeginScope(BudgetOptions(maxProviderCalls: 2)))
        {
            _ = await AssertEx.ThrowsAsync<ProviderCallBudgetExceededException>(async () =>
                _ = await SendAsync(client, Options(tool), streaming));
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal(expected: 2, executions, "both allowed tool iterations must finish before the final request is refused");
        AssertEx.Equal(expected: 2, inner.CallCount, "the rejected final request must not reach the scripted provider");
        AssertEx.Equal(expected: 2, snapshot.ProviderCalls, "accepted and rejected provider attempts must not be conflated");
        AssertEx.Equal(expected: 1, snapshot.ProviderRoundsRejected, "the otherwise-final provider request must be recorded as rejected");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownFunction_ReturnsRecoveryWithoutExecutingAHandler(bool streaming)
    {
        var executions = 0;
        var knownTool = AIFunctionFactory.Create(() =>
            {
                _ = Interlocked.Increment(ref executions);
                return "must not run";
            },
            ToolName);
        using var inner = new ScriptedChatClient((call, _, _) =>
            call == 1 ? FunctionCall("unknown_tool", call) : FinalAnswer());
        using var provider = BuildProvider(inner);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;
        ChatResponse response;

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
        {
            response = await SendAsync(client, Options(knownTool), streaming);
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal("done", response.Text);
        AssertEx.Equal(expected: 0, executions, "an unknown name must not dispatch any known handler");
        AssertEx.Equal(expected: 2, inner.CallCount, "the not-found result must be sent back for one recovery round");
        AssertEx.Equal(expected: 2, snapshot.ProviderCalls);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HandlerExceptions_AbortAtPinnedConsecutiveErrorLimit(bool streaming)
    {
        const string Secret = "handler-secret-detail";
        var executions = 0;
        var tool = AIFunctionFactory.Create((Func<string>)(() =>
            {
                _ = Interlocked.Increment(ref executions);
                throw new InvalidOperationException(Secret);
            }),
            ToolName);
        using var inner = new ScriptedChatClient((call, _, _) => FunctionCall(ToolName, call));
        using var provider = BuildProvider(inner, maximumIterations: 10);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;
        InvalidOperationException terminalException;

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
        {
            terminalException = await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
                _ = await SendAsync(client, Options(tool), streaming));
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        var sentFailures = inner.ReceivedMessages
                                .SelectMany(static messages => messages)
                                .SelectMany(static message => message.Contents)
                                .OfType<FunctionResultContent>()
                                .GroupBy(static result => result.CallId, StringComparer.Ordinal)
                                .Select(static group => group.First())
                                .ToList();
        AssertEx.Equal(expected: 4, executions, "three failed iterations are recoverable; the fourth must abort the request");
        AssertEx.Equal(expected: 4, inner.CallCount, "no fifth provider request may start after the fourth consecutive error");
        AssertEx.Equal(expected: 4, snapshot.ProviderCalls);
        AssertEx.Equal(Secret, terminalException.Message, "the API caller must receive the original terminal exception");
        AssertEx.Equal(expected: 3, sentFailures.Count, "exactly three recoverable failures must be returned before the terminal error");
        AssertEx.True(sentFailures.All(result => !(result.Result?.ToString() ?? string.Empty).Contains(Secret, StringComparison.Ordinal)),
            "detailed handler exception text must not be included in provider-visible tool results");
        AssertEx.True(sentFailures.All(result => result.Exception?.Message.Contains(Secret, StringComparison.Ordinal) == true),
            "the application-visible FunctionResultContent must retain the original exception");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MultipleFunctions_AreInvokedSequentially(bool streaming)
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = 0;
        var secondObservedFirstFinished = false;
        var first = AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
            {
                _ = firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                _ = Interlocked.Exchange(ref firstFinished, 1);
                return "first result";
            },
            "first_tool");
        var second = AIFunctionFactory.Create(() =>
            {
                secondObservedFirstFinished = Volatile.Read(ref firstFinished) == 1;
                _ = secondStarted.TrySetResult();
                return "second result";
            },
            "second_tool");
        using var inner = new ScriptedChatClient((call, _, _) =>
            call == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-first", first.Name),
                    new FunctionCallContent("call-second", second.Name)
                ]))
                : FinalAnswer());
        using var provider = BuildProvider(inner);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
        {
            // CA2025 false positive: this task must run concurrently to expose the gate; finally releases and directly
            // awaits it before either the ambient budget or provider can be disposed.
#pragma warning disable CA2025
            var send = SendAsync(client, Options(first, second), streaming);
#pragma warning restore CA2025
            try
            {
                await AssertEx.CompletesAsync(firstStarted.Task, CompletionTimeout, "the first function never reached its controlled gate");
                await AssertEx.StaysIncompleteAsync(secondStarted.Task, "the second function started while the first function was still blocked");
            }
            finally
            {
                _ = releaseFirst.TrySetResult();
                _ = await send;
            }

            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.True(secondObservedFirstFinished, "the second function must observe completion of the first function");
        AssertEx.Equal(expected: 2, inner.CallCount);
        AssertEx.Equal(expected: 2, snapshot.ProviderCalls);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task KnownNonInvocableDeclaration_ReturnsCallWithoutExecutionOrFollowup(bool streaming)
    {
        var executions = 0;
        var executable = AIFunctionFactory.Create(() =>
            {
                _ = Interlocked.Increment(ref executions);
                return "must not run";
            },
            ToolName);
        var declaration = executable.AsDeclarationOnly();
        using var inner = new ScriptedChatClient((call, _, _) => FunctionCall(ToolName, call));
        using var provider = BuildProvider(inner);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;
        ChatResponse response;

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
        {
            response = await SendAsync(client, Options(declaration), streaming);
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        var returnedCall = AssertEx.NotNull(response.Messages
                                                    .SelectMany(static message => message.Contents)
                                                    .OfType<FunctionCallContent>()
                                                    .SingleOrDefault());
        AssertEx.Equal(ToolName, returnedCall.Name);
        AssertEx.Equal(expected: 0, executions, "a declaration-only tool must never execute its source delegate");
        AssertEx.Equal(expected: 1, inner.CallCount, "a known non-invocable declaration must surface without a recovery round");
        AssertEx.Equal(expected: 1, snapshot.ProviderCalls);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ListTools_RevealsThenInvokesHiddenAuthorizedTool(bool streaming)
    {
        const string HiddenToolName = "hidden_authorized_tool";
        var executions = 0;
        var tools = new List<AITool>();
        var listTools = new ListToolsFunction(tools);
        var hiddenTool = AIFunctionFactory.Create(() =>
            {
                _ = Interlocked.Increment(ref executions);
                return "hidden result";
            },
            HiddenToolName,
            "A hidden but authorized harmless tool.");
        tools.Add(listTools);
        tools.Add(hiddenTool);

        var selector = Substitute.For<IToolRelevanceSelector>();
        selector.SelectAsync(Arg.Any<string?>(),
                    Arg.Any<IReadOnlyList<ToolRelevanceCandidate>>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ToolRelevanceSelection([ListToolsFunction.ToolName], [HiddenToolName])));
        using var inner = new ScriptedChatClient((call, _, _) => call switch
        {
            1 => FunctionCall(ListToolsFunction.ToolName, call),
            2 => FunctionCall(HiddenToolName, call),
            _ => FinalAnswer()
        });
        using var provider = BuildProvider(inner, selector: selector, toolRelevanceThreshold: 1);
        var client = provider.GetRequiredService<IChatClient>();
        ProviderCallEfficiencySnapshot snapshot;
        ChatResponse response;

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
            using (ToolRelevanceScope.BeginScope(active: true, new HashSet<string>(StringComparer.Ordinal)))
            {
                response = await SendAsync(client, new ChatOptions
                {
                    Tools = tools
                }, streaming);
                snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
            }

        AssertEx.Equal("done", response.Text);
        AssertEx.Equal(expected: 1, executions, "the revealed authorized tool must execute exactly once");
        AssertEx.Equal(expected: 3, inner.CallCount, "list, hidden-tool, and final-answer rounds must all reach the provider");
        AssertEx.Equal(expected: 3, snapshot.ProviderCalls);
        AssertEx.True(inner.ReceivedToolNames[0].SequenceEqual([ListToolsFunction.ToolName], StringComparer.Ordinal),
            "the first offer must contain only list_tools");
        AssertEx.Contains(inner.ReceivedToolNames[1], HiddenToolName, "list_tools must reveal the hidden authorized tool on the next round");
        AssertEx.True(inner.ReceivedToolNames.SelectMany(static names => names).All(name => tools.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal))),
            "relevance recovery must never add a tool outside the authorized input array");
    }

    /// <summary>
    ///     Exactly ONE span in the assembled pipeline may claim <c>gen_ai.operation.name = execute_tool</c> for a given
    ///     tool call, and it is MEAI's: <c>FunctionInvokingChatClient</c> starts an <c>execute_tool {name}</c> activity
    ///     on the <see cref="ActivitySource" /> it takes from the client below it (the pipeline's
    ///     <c>OpenTelemetryChatClient</c>, source "Microsoft.Extensions.AI"). The repo's own
    ///     <c>AgentRun.ToolCall*</c> pair observes the same call on the Agent source and must stay out of that
    ///     convention — otherwise a backend filtering on the attribute counts every tool execution twice. The
    ///     per-client test cannot see this: it needs the whole decorated pipeline and both sources listening.
    /// </summary>
    [Test]
    public async Task ToolCall_ClaimsTheExecuteToolOperationNameExactlyOnceAcrossTheWholePipeline()
    {
        var callId = $"call-{Guid.NewGuid():N}";
        var tool = AIFunctionFactory.Create(static () => "tool result", ToolName);
        using var inner = new ScriptedChatClient((call, _, _) =>
            call == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, ToolName)]))
                : FinalAnswer());
        using var provider = BuildProvider(inner);
        var client = provider.GetRequiredService<IChatClient>();
        // Both sources are process-static and sibling tests emit on them concurrently, so every span is selected by
        // this test's own call id before anything is asserted about it.
        var spans = new ConcurrentQueue<(string OperationName, string? GenAiOperationName)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is "Microsoft.Extensions.AI" or "XE.LocalAiEngine.AI.Agent",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(activity.GetTagItem("gen_ai.tool.call.id")?.ToString(), callId, StringComparison.Ordinal))
                {
                    spans.Enqueue((activity.OperationName, activity.GetTagItem("gen_ai.operation.name")?.ToString()));
                }
            }
        };

        ActivitySource.AddActivityListener(listener);

        using (ProviderCallBudget.BeginScope(BudgetOptions()))
        {
            _ = await SendAsync(client, Options(tool), streaming: false);
        }

        AssertEx.ContainsSingle(spans, entry => string.Equals(entry.OperationName, "AgentRun.ToolCallRequested", StringComparison.Ordinal),
            "the repo's own request span must be emitted — without it this test would pass vacuously.");
        AssertEx.ContainsSingle(spans, entry => string.Equals(entry.GenAiOperationName, "execute_tool", StringComparison.Ordinal),
            "exactly one span per tool call may claim gen_ai.operation.name = execute_tool.");
        AssertEx.ContainsSingle(spans, entry => string.Equals(entry.OperationName, $"execute_tool {ToolName}", StringComparison.Ordinal)
                                                && string.Equals(entry.GenAiOperationName, "execute_tool", StringComparison.Ordinal),
            "the one execute_tool span must be MEAI's function-invocation span, not one of the repo's.");
    }

    private static ServiceProvider BuildProvider(ScriptedChatClient inner,
        int maximumIterations = 10,
        IToolRelevanceSelector? selector = null,
        int toolRelevanceThreshold = 12)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IChatClient>(inner);
        services.AddOptions<AgentToolPipelineOptions>()
                .Configure(options => options.MaximumToolIterationsPerRequest = maximumIterations);
        services.AddOptions<AgentTelemetryOptions>();
        services.AddOptions<ToolRelevanceOptions>()
                .Configure(options => options.Threshold = toolRelevanceThreshold);
        if (selector is not null)
        {
            services.AddSingleton(selector);
        }

        services.DecorateChatClientPipeline();
        return services.BuildServiceProvider();
    }

    private static ProviderCallBudgetOptions BudgetOptions(int maxProviderCalls = 20)
    {
        return new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = maxProviderCalls,
            MaxCumulativeInputTokens = int.MaxValue,
            DefaultContextTokens = 32_768,
            ReservedOutputTokenFloor = 0
        };
    }

    private static ChatOptions Options(params AITool[] tools)
    {
        return new ChatOptions
        {
            Tools = tools
        };
    }

    private static Task<ChatResponse> SendAsync(IChatClient client, ChatOptions options, bool streaming)
    {
        ChatMessage[] messages = [new ChatMessage(ChatRole.User, "run the scripted trajectory")];
        return streaming
            ? client.GetStreamingResponseAsync(messages, options).ToChatResponseAsync()
            : client.GetResponseAsync(messages, options);
    }

    private static ChatResponse FunctionCall(string name, int call)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"call-{call}", name)]));
    }

    private static ChatResponse FinalAnswer()
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
    }

    private sealed class ScriptedChatClient(Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> response) : IChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

        public List<IReadOnlyList<string>> ReceivedToolNames { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var received = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
            ReceivedMessages.Add([.. received]);
            ReceivedToolNames.Add([.. (options?.Tools ?? []).Select(static tool => tool.Name)]);
            var call = Interlocked.Increment(ref _callCount);
            return Task.FromResult(response(call, received, options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return ToUpdates(GetResponseAsync(messages, options, cancellationToken), cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ToUpdates(Task<ChatResponse> responseTask,
            [EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            var chatResponse = await responseTask;
            foreach (var message in chatResponse.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }
    }
}
