namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Deterministic CI guard for framework approval behavior (no Ollama, no network). Locks in the live finding from
///     the approval-gate probe: the framework-native approval gate
///     (<see cref="ApprovalRequiredAIFunction" /> + <c>UseFunctionInvocation</c>) PAUSES — run#1 surfaces a
///     <see cref="ToolApprovalRequestContent" /> and does NOT execute the tool — and a THREADLESS resume
///     (replay history + the approval response, <c>AgentSession = null</c>) executes the tool only when
///     approved. Execution is ground-truth via an in-process counter captured by the tool, not inferred
///     from model text. A scripted <see cref="IChatClient" /> stands in for the model so the test is fully
///     deterministic.
/// </summary>
public sealed class FrameworkApprovalGateTests
{
    private const string ToolName = "destructive_cleanup";

    [Test]
    public async Task ApprovalRequiredTool_ThreadlessApprove_ExecutesExactlyOnceAfterApproval()
    {
        var executed = 0;
        string? reasonSeen = null;
        var tool = BuildApprovalTool(reason =>
        {
            executed++;
            reasonSeen = reason;
        });

        using var scripted = new ScriptedApprovalChatClient(ToolName);
        var chatClient = scripted.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var sp = new ServiceCollection().BuildServiceProvider();
        var agent = BuildAgent(chatClient, tool, sp);
        var seed = BuildSeed();

        // run#1 — must PAUSE: surface a ToolApprovalRequestContent, tool NOT executed.
        var first = await agent.RunAsync(seed, null, null, CancellationToken.None);
        var requests = first.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
        AssertEx.Equal(1, requests.Count, "run#1 must surface exactly one ToolApprovalRequestContent");
        AssertEx.Equal(0, executed, "tool must NOT execute before approval");

        // run#2 — THREADLESS resume: replay full history + the approval response, no AgentSession.
        var resume = new List<ChatMessage>(seed);
        resume.AddRange(first.Messages);
        resume.Add(new ChatMessage(ChatRole.User, requests.Select(r => (AIContent)r.CreateResponse(true)).ToList()));
        _ = await agent.RunAsync(resume, null, null, CancellationToken.None);

        AssertEx.Equal(1, executed, "approved tool must execute exactly once after the threadless resume");
        AssertEx.Equal("ci-regression", reasonSeen, "approved tool must run with the scripted arguments");
    }

    [Test]
    public async Task ApprovalRequiredTool_ThreadlessReject_DoesNotExecute()
    {
        var executed = 0;
        var tool = BuildApprovalTool(_ => executed++);

        using var scripted = new ScriptedApprovalChatClient(ToolName);
        var chatClient = scripted.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var sp = new ServiceCollection().BuildServiceProvider();
        var agent = BuildAgent(chatClient, tool, sp);
        var seed = BuildSeed();

        var first = await agent.RunAsync(seed, null, null, CancellationToken.None);
        var requests = first.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
        AssertEx.Equal(1, requests.Count, "run#1 must surface exactly one ToolApprovalRequestContent");

        var resume = new List<ChatMessage>(seed);
        resume.AddRange(first.Messages);
        resume.Add(new ChatMessage(ChatRole.User, requests.Select(r => (AIContent)r.CreateResponse(false)).ToList()));
        _ = await agent.RunAsync(resume, null, null, CancellationToken.None);

        AssertEx.Equal(0, executed, "rejected tool must never execute");
    }

    private static ApprovalRequiredAIFunction BuildApprovalTool(Action<string> onExecute)
    {
        var inner = AIFunctionFactory.Create((string reason) =>
            {
                onExecute(reason);
                return "cleanup performed";
            },
            ToolName,
            "Performs the destructive cleanup. Side-effecting and irreversible.");
        return new ApprovalRequiredAIFunction(inner);
    }

    private static ChatClientAgent BuildAgent(IChatClient chatClient, AITool tool, IServiceProvider sp)
    {
        return new ChatClientAgent(chatClient,
            "ci-approval-gate",
            "Call the destructive_cleanup tool when asked to perform a cleanup.",
            "Deterministic approval-gate CI guard.",
            new List<AITool>
            {
                tool
            },
            NullLoggerFactory.Instance,
            sp);
    }

    private static List<ChatMessage> BuildSeed()
    {
        return new List<ChatMessage>
        {
            new(ChatRole.System, "Call the destructive_cleanup tool when asked to perform a cleanup."),
            new(ChatRole.User, "Perform the destructive cleanup now.")
        };
    }

    /// <summary>
    ///     Scripted stand-in for the model. Before the tool has run it emits a single
    ///     <see cref="FunctionCallContent" /> for the approval-required tool (which FICC converts to a
    ///     <see cref="ToolApprovalRequestContent" />); once a <see cref="FunctionResultContent" /> is present
    ///     in the history (tool executed or rejection synthesised) it returns a plain final message.
    /// </summary>
    private sealed class ScriptedApprovalChatClient : IChatClient
    {
        private readonly string _toolName;

        public ScriptedApprovalChatClient(string toolName)
        {
            _toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
        }

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var toolHasRun = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            if (toolHasRun)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "cleanup complete")));
            }

            var call = new FunctionCallContent($"call-{_toolName}", _toolName, new Dictionary<string, object?>
            {
                ["reason"] = "ci-regression"
            });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                call
            })));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Approval-gate CI guard exercises the non-streaming RunAsync path only.");
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
}
