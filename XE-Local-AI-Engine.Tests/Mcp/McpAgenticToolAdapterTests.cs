namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpAgenticToolAdapterTests
{
    private static readonly McpInboundExecutionContext Agentic = new(McpServerApiKeyScope.Agentic, "xemcp_abc123");

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
}
