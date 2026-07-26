namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
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
        var first = await agent.RunAsync(seed, session: null, options: null, CancellationToken.None);
        var requests = first.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
        AssertEx.Equal(expected: 1, requests.Count, "run#1 must surface exactly one ToolApprovalRequestContent");
        AssertEx.Equal(expected: 0, executed, "tool must NOT execute before approval");

        // run#2 — THREADLESS resume: replay full history + the approval response, no AgentSession.
        var resume = new List<ChatMessage>(seed);
        resume.AddRange(first.Messages);
        resume.Add(new ChatMessage(ChatRole.User, requests.Select(r => (AIContent)r.CreateResponse(true)).ToList()));
        _ = await agent.RunAsync(resume, session: null, options: null, CancellationToken.None);

        AssertEx.Equal(expected: 1, executed, "approved tool must execute exactly once after the threadless resume");
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

        var first = await agent.RunAsync(seed, session: null, options: null, CancellationToken.None);
        var requests = first.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
        AssertEx.Equal(expected: 1, requests.Count, "run#1 must surface exactly one ToolApprovalRequestContent");

        var resume = new List<ChatMessage>(seed);
        resume.AddRange(first.Messages);
        resume.Add(new ChatMessage(ChatRole.User, requests.Select(r => (AIContent)r.CreateResponse(false)).ToList()));
        _ = await agent.RunAsync(resume, session: null, options: null, CancellationToken.None);

        AssertEx.Equal(expected: 0, executed, "rejected tool must never execute");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ApprovalRequiredTool_StreamingThreadlessDecision_PreservesPauseResumeAndExactlyOnce(bool approved)
    {
        var executed = 0;
        string? reasonSeen = null;
        var tool = BuildApprovalTool(ToolName, reason =>
        {
            executed++;
            reasonSeen = reason;
        });

        using var scripted = new ScriptedApprovalChatClient(ToolName);
        var chatClient = scripted.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var sp = new ServiceCollection().BuildServiceProvider();
        var agent = BuildAgent(chatClient, tool, sp);
        var seed = BuildSeed();

        var first = await agent
                          .RunStreamingAsync(seed, session: null, options: null, CancellationToken.None)
                          .ToAgentResponseAsync(CancellationToken.None);
        var requests = first.Messages.SelectMany(static message => message.Contents).OfType<ToolApprovalRequestContent>().ToList();
        AssertEx.Equal(expected: 1, requests.Count, "streaming run#1 must surface exactly one approval request");
        AssertEx.Equal(expected: 0, executed, "streaming run#1 must pause before executing the tool");

        var resume = new List<ChatMessage>(seed);
        resume.AddRange(first.Messages);
        resume.Add(new ChatMessage(ChatRole.User,
            requests.Select(request => (AIContent)request.CreateResponse(approved)).ToList()));

        var final = await agent
                          .RunStreamingAsync(resume, session: null, options: null, CancellationToken.None)
                          .ToAgentResponseAsync(CancellationToken.None);

        AssertEx.Equal(expected: approved ? 1 : 0,
            executed,
            approved
                ? "the approved streaming resume must execute the tool exactly once"
                : "the rejected streaming resume must never execute the tool");
        AssertEx.Equal(expected: 2, scripted.CallCount, "pause and resume must each invoke the scripted model exactly once");
        AssertEx.Equal("cleanup complete", final.Text, "the resumed streaming run must preserve the final response");
        if (approved)
        {
            AssertEx.Equal("ci-regression", reasonSeen, "the approved streaming tool call must preserve its arguments");
        }
    }

    [Test]
    public async Task ApprovalRequiredTools_StreamingParallelRequests_CorrelatesReverseOrderedResponsesAndExecutesEachOnce()
    {
        const string secondToolName = "destructive_archive";
        var cleanupExecutions = 0;
        var archiveExecutions = 0;
        var cleanupTool = BuildApprovalTool(ToolName, _ => cleanupExecutions++);
        var archiveTool = BuildApprovalTool(secondToolName, _ => archiveExecutions++);

        using var scripted = new ScriptedApprovalChatClient(ToolName, secondToolName);
        var chatClient = scripted.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var sp = new ServiceCollection().BuildServiceProvider();
        var agent = BuildAgent(chatClient,
            [
                cleanupTool,
                archiveTool
            ],
            sp);
        var seed = BuildSeed();

        var first = await agent
                          .RunStreamingAsync(seed, session: null, options: null, CancellationToken.None)
                          .ToAgentResponseAsync(CancellationToken.None);
        var requests = first.Messages.SelectMany(static message => message.Contents).OfType<ToolApprovalRequestContent>().ToList();
        AssertEx.Equal(expected: 2, requests.Count, "parallel tool calls must surface two independent approval requests");
        AssertEx.Equal(expected: 0, cleanupExecutions, "the cleanup tool must pause before approval");
        AssertEx.Equal(expected: 0, archiveExecutions, "the archive tool must pause before approval");

        var resume = new List<ChatMessage>(seed);
        resume.AddRange(first.Messages);
        resume.Add(new ChatMessage(ChatRole.User,
            requests.AsEnumerable().Reverse().Select(request => (AIContent)request.CreateResponse(true)).ToList()));

        _ = await agent
                  .RunStreamingAsync(resume, session: null, options: null, CancellationToken.None)
                  .ToAgentResponseAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, cleanupExecutions, "the first approved parallel tool must execute exactly once");
        AssertEx.Equal(expected: 1, archiveExecutions, "the second approved parallel tool must execute exactly once");
        AssertEx.Equal(expected: 2, scripted.CallCount, "parallel approvals must resume in one model round after all decisions arrive");
    }

    [Test]
    public async Task ApprovalRequiredTool_StreamingCancelledBeforeFirstUpdate_DoesNotExecute()
    {
        var executed = 0;
        var tool = BuildApprovalTool(_ => executed++);

        using var scripted = new ScriptedApprovalChatClient(ToolName);
        var chatClient = scripted.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var sp = new ServiceCollection().BuildServiceProvider();
        var agent = BuildAgent(chatClient, tool, sp);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => agent
                                                                          .RunStreamingAsync(BuildSeed(),
                                                                              session: null,
                                                                              options: null,
                                                                              cancellationTokenSource.Token)
                                                                          .ToAgentResponseAsync(cancellationTokenSource.Token));

        AssertEx.Equal(expected: 0, executed, "a cancelled streaming run must not execute an approval-required tool");
    }

    private static ApprovalRequiredAIFunction BuildApprovalTool(Action<string> onExecute)
    {
        return BuildApprovalTool(ToolName, onExecute);
    }

    private static ApprovalRequiredAIFunction BuildApprovalTool(string toolName, Action<string> onExecute)
    {
        var inner = AIFunctionFactory.Create((string reason) =>
            {
                onExecute(reason);
                return "cleanup performed";
            },
            toolName,
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

    private static ChatClientAgent BuildAgent(IChatClient chatClient, IReadOnlyList<AITool> tools, IServiceProvider sp)
    {
        return new ChatClientAgent(chatClient,
            "ci-approval-gate",
            "Call the destructive tools when asked to perform cleanup.",
            "Deterministic approval-gate CI guard.",
            tools,
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
        private readonly IReadOnlyList<string> _toolNames;

        public ScriptedApprovalChatClient(params string[] toolNames)
        {
            ArgumentNullException.ThrowIfNull(toolNames);
            if (toolNames.Length == 0)
            {
                throw new ArgumentException("At least one tool name is required.", nameof(toolNames));
            }

            _toolNames = toolNames;
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

            var calls = _toolNames.Select(toolName => (AIContent)new FunctionCallContent($"call-{toolName}",
                    toolName,
                    new Dictionary<string, object?>
                    {
                        ["reason"] = "ci-regression"
                    }))
                                  .ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
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
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var response = await responseTask.ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }
    }
}
