namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class McpAgenticToolAdapterTests
{
    private static readonly McpInboundExecutionContext Agentic = new()
    {
        Scope = McpServerApiKeyScope.Agentic,
        KeyPrefix = "xemcp_abc123"
    };

    [Test]
    public async Task InvokeAsync_AuditsBeforeInvokingInner_ExactlyOnce()
    {
        var events = new List<string>();
        var audit = Substitute.For<IMcpAgenticApprovalAuditRecorder>();
        audit.RecordAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ToolCategory>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             {
                 events.Add("audit");
                 return Task.CompletedTask;
             });
        var inner = AIFunctionFactory.Create(() => events.Add("inner"), "write_file");
        var adapted = new McpAgenticToolAdapter(audit, NullLogger<McpAgenticToolAdapter>.Instance)
            .Adapt(new ApprovalRequiredAIFunction(inner), ToolCategory.WriteExecute, Agentic, Guid.NewGuid());

        await adapted.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        AssertEx.True(events.SequenceEqual(["audit", "inner"], StringComparer.Ordinal));
        AssertEx.False(adapted is ApprovalRequiredAIFunction);
        await audit.Received(1).RecordAsync(Arg.Any<Guid>(),
            "write_file",
            ToolCategory.WriteExecute,
            "xemcp_abc123",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvokeAsync_WhenStrictAuditFails_DoesNotInvokeInner()
    {
        var invoked = 0;
        var audit = Substitute.For<IMcpAgenticApprovalAuditRecorder>();
        audit.RecordAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ToolCategory>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns<Task>(_ => throw new IOException("audit unavailable"));
        var inner = AIFunctionFactory.Create(() => invoked++, "write_file");
        var adapted = new McpAgenticToolAdapter(audit, NullLogger<McpAgenticToolAdapter>.Instance)
            .Adapt(new ApprovalRequiredAIFunction(inner), ToolCategory.WriteExecute, Agentic, Guid.NewGuid());

        _ = await Assert.ThrowsAsync<IOException>(() => adapted.InvokeAsync(new AIFunctionArguments(), CancellationToken.None).AsTask());

        AssertEx.Equal(0, invoked);
    }

    // I-D12: MEAI's FunctionInvokingChatClient detects approval with GetService<ApprovalRequiredAIFunction>(), which a plain
    // DelegatingAIFunction forwards to its inner function — so the wrapper alone still turned the call into an unanswered approval request.
    [Test]
    public async Task FunctionInvocation_RunsAdaptedTool_InsteadOfRequestingApproval()
    {
        var invoked = 0;
        var audit = Substitute.For<IMcpAgenticApprovalAuditRecorder>();
        var inner = AIFunctionFactory.Create(() =>
        {
            invoked++;
            return "4";
        }, "run_python");
        var adapted = new McpAgenticToolAdapter(audit, NullLogger<McpAgenticToolAdapter>.Instance)
            .Adapt(new ApprovalRequiredAIFunction(inner), ToolCategory.WriteExecute, Agentic, Guid.NewGuid());
        using var scripted = new CallThenAnswerChatClient("run_python");
        using var client = new ChatClientBuilder(scripted)
                           .UseFunctionInvocation(NullLoggerFactory.Instance)
                           .Build();

        var response = await client.GetResponseAsync("compute", new ChatOptions
        {
            Tools = [adapted]
        });

        AssertEx.Equal(1, invoked);
        AssertEx.Equal("answer", response.Text);
        AssertEx.False(response.Messages.SelectMany(static message => message.Contents).OfType<ToolApprovalRequestContent>().Any());
        await audit.Received(1).RecordAsync(Arg.Any<Guid>(),
            "run_python",
            ToolCategory.WriteExecute,
            "xemcp_abc123",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetService_HidesApprovalRequiredMarker()
    {
        var adapted = new McpAgenticToolAdapter(Substitute.For<IMcpAgenticApprovalAuditRecorder>(), NullLogger<McpAgenticToolAdapter>.Instance)
            .Adapt(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => 0, "run_python")),
                ToolCategory.WriteExecute,
                Agentic,
                Guid.NewGuid());

        AssertEx.Null(adapted.GetService<ApprovalRequiredAIFunction>());
    }

    /// <summary>First round calls the named tool; the second round answers with text.</summary>
    private sealed class CallThenAnswerChatClient : IChatClient
    {
        private readonly string _toolName;
        private int _calls;

        public CallThenAnswerChatClient(string toolName)
        {
            _toolName = toolName;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var message = Interlocked.Increment(ref _calls) == 1
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", _toolName)])
                : new ChatMessage(ChatRole.Assistant, "answer");
            return Task.FromResult(new ChatResponse(message));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
