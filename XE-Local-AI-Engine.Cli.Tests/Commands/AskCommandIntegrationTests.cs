namespace XE_Local_AI_Engine.Cli.Tests.Commands;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Cli.Tests.Integration;
using XE_Local_AI_Engine.Tests.Testing;
using Server = XE_Local_AI_Engine.Client.Services.Chat;

/// <summary><c>xe ask --json</c> end to end against a real in-process node; only the chat stream service is scripted.</summary>
/// <remarks>Covers <c>--url</c>, sign-in with the environment password, conversation creation, the hub and the approval endpoint.</remarks>
[Category(TestCategories.Integration)]
public sealed class AskCommandIntegrationTests
{
    [Test]
    public async Task AskJson_StreamsTheTurnAsJsonLinesAndExitsZero()
    {
        Server.NodeChatStreamRequest? sent = null;
        await using var node = await StartAsync(request =>
        {
            sent = request;
            return [Frame(Server.ChatStreamEventTypes.AssistantDelta, delta: "Hel", offset: 0), Frame(Server.ChatStreamEventTypes.AssistantDelta, delta: "lo", offset: 3), Frame(Server.ChatStreamEventTypes.AssistantCompleted, content: "Hello")];
        });
        using var cli = Cli(node);

        var exitCode = await cli.RunAsync("ask", "Say hello", "--json", "--url", "http://localhost", "--no-token-file", "--new-conversation-title", "From a script");

        AssertEx.Equal(ExitCodes.Success, exitCode, cli.Out + cli.Error);
        var lines = cli.JsonLines;
        AssertEx.Equal("delta,delta,completed", string.Join(',', lines.SkipLast(1).Select(static line => line.GetProperty("kind").GetString())));
        AssertEx.Equal("summary", lines[^1].GetProperty("type").GetString());
        AssertEx.Equal("Hello", lines[^1].GetProperty("text").GetString());
        var request = AssertEx.NotNull(sent);
        AssertEx.Equal("Say hello", request.Content);
        AssertEx.Equal(request.RequestId.GetValueOrDefault().ToString("D"), lines[^1].GetProperty("requestId").GetString());
        AssertEx.Equal(string.Empty, cli.Error);
    }

    [Test]
    public async Task AskJson_WhenTheTurnNeedsAnApproval_DeniesItThroughTheNodeAndExitsSix()
    {
        await using var node = await StartAsync(_ =>
            [Frame(Server.ChatStreamEventTypes.ApprovalRequested, approvalRequestId: "approval-1"), Frame(Server.ChatStreamEventTypes.AssistantCompleted, content: "Denied.")]);
        using var cli = Cli(node);

        var exitCode = await cli.RunAsync("ask", "Write a file", "--json", "--url", "http://localhost", "--no-token-file");

        AssertEx.Equal(ExitCodes.InteractionRequired, exitCode, cli.Out + cli.Error);
        var lines = cli.JsonLines;
        AssertEx.Equal("approvalRequested", lines[0].GetProperty("kind").GetString());
        AssertEx.ContainsSingle(lines, line => line.GetProperty("type").GetString() == "notice" && line.GetProperty("kind").GetString() == "approvalDenied");
        AssertEx.False(lines.Any(line => line.TryGetProperty("kind", out var kind) && kind.GetString() == "resolveFailed"), "the node accepted the denial");
        AssertEx.Equal(ExitCodes.InteractionRequired, lines[^1].GetProperty("exitCode").GetInt32());
    }

    private static TestCli Cli(TestNode node) =>
        new(node.Server.CreateHandler, new Dictionary<string, string>(StringComparer.Ordinal) { [PasswordSource.EnvironmentVariable] = TestNode.AdminPassword });

    private static async Task<TestNode> StartAsync(Func<Server.NodeChatStreamRequest, IReadOnlyList<Server.ChatStreamEvent>> script)
    {
        var streams = Substitute.For<Server.INodeChatStreamService>();
        streams.SendMessageAsync(Arg.Any<Server.NodeChatStreamRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   var request = call.Arg<Server.NodeChatStreamRequest>();
                   return PlayAsync(script(request).Select((frame, index) => frame with
                   {
                       ConversationId = request.ConversationId,
                       MessageId = request.MessageId!.Value,
                       RequestId = request.RequestId!.Value,
                       Sequence = index
                   }), call.Arg<CancellationToken>());
               });
        var node = await TestNode.StartAsync(services =>
        {
            services.RemoveAll<Server.INodeChatStreamService>();
            services.AddSingleton(streams);
        });
        await node.SetUpAdminAsync();
        return node;
    }

    private static async IAsyncEnumerable<Server.ChatStreamEvent> PlayAsync(IEnumerable<Server.ChatStreamEvent> frames, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var frame in frames)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
        }
    }

    private static Server.ChatStreamEvent Frame(string type, string? delta = null, long? offset = null, string? content = null, string? approvalRequestId = null) =>
        new()
        {
            Type = type,
            ConversationId = Guid.Empty,
            MessageId = Guid.Empty,
            RequestId = Guid.Empty,
            Status = type == Server.ChatStreamEventTypes.AssistantCompleted
                ? Server.NodeChatMessageStatusValues.Completed
                : Server.NodeChatMessageStatusValues.Streaming,
            Sequence = 0,
            OccurredAtUtc = 1,
            Delta = delta,
            ContentOffset = offset,
            ReasoningOffset = offset is null ? null : 0,
            Content = content,
            ToolName = approvalRequestId is null ? null : "write_file",
            ApprovalRequestId = approvalRequestId
        };
}
