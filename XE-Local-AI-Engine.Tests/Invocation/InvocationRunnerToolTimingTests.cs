namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>A completed tool call carries how long the TOOL ran, measured in the function invoker and read back through the timing scope.</summary>
/// <remarks>
///     Requested→Completed is not that time: it spans the model's streaming tail and every sibling call of the round.
///     Driven through the real runner, a real <c>ChatClientAgent</c> and the production decorated pipeline over a scripted
///     model, so it is also the proof that the scope flows from the runner into the framework's function invocation.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class InvocationRunnerToolTimingTests
{
    private const string ToolA = "tool_a";
    private const string ToolB = "tool_b";

    [Test]
    public async Task TwoCallsInOneRound_EachDurationCoversOnlyItsOwnExecution()
    {
        var time = new ManualTimeProvider();
        var start = time.GetTimestamp();
        AITool[] tools =
        [
            AIFunctionFactory.Create(() =>
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                return "a done";
            }, ToolA),
            AIFunctionFactory.Create(() =>
            {
                time.Advance(TimeSpan.FromMilliseconds(300));
                return "b done";
            }, ToolB)
        ];
        using var model = new TwoCallRoundChatClient(time);
        await using var pipeline = BuildPipeline(model, time);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var lifecycle = new List<ToolCallLifecyclePayload>();
        dispatcher.ReportToolCallLifecycleAsync(Arg.Do<ToolCallLifecyclePayload>(lifecycle.Add)).Returns(Task.CompletedTask);
        var runner = InvocationRunnerTestSupport.CreateRunner(RealPipelineFactory(pipeline, tools), eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool(ToolA).WithAllowedTool(ToolB).Build();

        await InvocationRunnerTestSupport.RunAsync(runner, package);

        var completed = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Completed).ToDictionary(static payload => payload.ToolCallId);
        AssertEx.Equal(expected: 2, completed.Count, "both calls of the round must complete");
        AssertEx.Equal("a done", completed["call-a"].Result);
        AssertEx.Equal(expected: 100L, completed["call-a"].DurationMs, "tool A's own execution, not the model's second between the calls");
        AssertEx.Equal(expected: 300L, completed["call-b"].DurationMs, "tool B's own execution, not tool A's time before it");
        AssertEx.True(lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Requested).All(static payload => payload.DurationMs is null),
            "a requested call has run nothing yet");

        // The model's streaming time is real on the same clock and lands in neither duration.
        AssertEx.Equal(expected: 2, model.CallCount, "a tool round and an answer round");
        AssertEx.Equal(expected: 2400d, time.GetElapsedTime(start).TotalMilliseconds, "1000 ms model + 100 + 300 tools + 1000 ms answer");
    }

    [Test]
    public async Task CompletedLifecycle_WhenNoDurationRecorded_CarriesNull()
    {
        // A result the framework did not execute here (provider-side, or replayed): nothing passes the invoker, so no timing exists.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var lifecycle = new List<ToolCallLifecyclePayload>();
        dispatcher.ReportToolCallLifecycleAsync(Arg.Do<ToolCallLifecyclePayload>(lifecycle.Add)).Returns(Task.CompletedTask);
        var runner = InvocationRunnerTestSupport.CreateRunner(eventDispatcher: dispatcher, agentUpdates: UntimedToolRound());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool(ToolA).Build();

        await InvocationRunnerTestSupport.RunAsync(runner, package);

        var completed = lifecycle.Single(static payload => payload.Phase == ToolCallLifecyclePhase.Completed);
        AssertEx.Equal("call-a", completed.ToolCallId);
        AssertEx.Equal("a done", completed.Result);
        AssertEx.Null(completed.DurationMs, "an unmeasured call reads as null, never 0");
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> UntimedToolRound()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-a", ToolA)]);
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Tool, [new FunctionResultContent("call-a", "a done")]);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "done")
        {
            FinishReason = ChatFinishReason.Stop
        };
    }

    private static ServiceProvider BuildPipeline(IChatClient model, TimeProvider time)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _ = services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _ = services.AddSingleton(time);
        _ = services.AddSingleton(model);
        _ = services.DecorateChatClientPipeline();
        return services.BuildServiceProvider();
    }

    // The production agent over the production decorated pipeline, offering exactly the given tools.
    private static IInvocationAgentFactory RealPipelineFactory(ServiceProvider pipeline, IReadOnlyList<AITool> tools)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = InvocationAgentFactory.BuildApprovalValidatedAgent(pipeline.GetRequiredService<IChatClient>(),
                           "tool-timing",
                           "Tool execution timing end to end.",
                           [.. tools],
                           NullLoggerFactory.Instance,
                           pipeline,
                           instructions: definition.Instructions),
                       Session = null,
                       SeedMessages = definition.ConversationContext.Prepend(new ChatMessage(ChatRole.System, definition.Instructions)).ToList()
                   });
               });

        return factory;
    }

    // Round one streams call A, spends 1000 ms "generating", then streams call B in the same round. Round two spends 1000 ms and answers.
    private sealed class TwoCallRoundChatClient : IChatClient
    {
        private readonly ManualTimeProvider _time;

        public TwoCallRoundChatClient(ManualTimeProvider time)
        {
            _time = time;
        }

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The runner streams.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            if (messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Any())
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
                {
                    FinishReason = ChatFinishReason.Stop
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-a", ToolA)]);
            _time.Advance(TimeSpan.FromSeconds(1));
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-b", ToolB)])
            {
                FinishReason = ChatFinishReason.ToolCalls
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}
