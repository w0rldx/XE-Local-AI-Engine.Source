namespace XE_Local_AI_Engine.Cli.Tests.Commands;

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Auth;
using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Cli.Discovery;
using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Cli.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class CliCommandTests
{
    private const string Url = "--url http://127.0.0.1:5123 --no-token-file";

    [Test]
    [Arguments("--help", ExitCodes.Success)]
    [Arguments("no-such-command", ExitCodes.Usage)]
    [Arguments("ask", ExitCodes.Usage)]
    [Arguments("status --url ftp://127.0.0.1:5123", ExitCodes.Usage)]
    [Arguments("status --url http://192.0.2.10:5123", ExitCodes.Usage)]
    [Arguments("status --url http://127.0.0.1:5123 --no-token-file --token-file other.json", ExitCodes.Usage)]
    [Arguments("status " + Url, ExitCodes.NodeNotRunning)]
    [Arguments("models " + Url, ExitCodes.NodeNotRunning)]
    [Arguments("agents --json " + Url, ExitCodes.NodeNotRunning)]
    [Arguments("ask hello " + Url, ExitCodes.NodeNotRunning)]
    [Arguments("chat " + Url, ExitCodes.InteractionRequired)]
    public async Task Run_ExitsWithTheDocumentedCode(string commandLine, int expected)
    {
        using var cli = new TestCli(Unreachable);

        var exitCode = await cli.RunAsync(commandLine.Split(' '));

        AssertEx.Equal(expected, exitCode, $"'{commandLine}' -> out: {cli.Out} err: {cli.Error}");
    }

    [Test]
    public async Task ChatJson_IsRejectedAsAUsageError()
    {
        using var cli = new TestCli(Unreachable);

        var exitCode = await cli.RunAsync("chat", "--json", "--url", "http://127.0.0.1:5123", "--no-token-file");

        AssertEx.Equal(ExitCodes.Usage, exitCode, $"out: {cli.Out} err: {cli.Error}");
        AssertEx.Contains(cli.JsonLines.Single().GetProperty("message").GetString(), "xe chat has no --json mode; use xe ask --json in scripts.");
    }

    [Test]
    public async Task Help_ListsEveryCommandAndTheExitCodes()
    {
        using var cli = new TestCli();

        var exitCode = await cli.RunAsync("--help");

        AssertEx.Equal(ExitCodes.Success, exitCode);
        foreach (var expected in new[]
                 {
                     "status",
                     "models",
                     "agents",
                     "ask",
                     "chat",
                     "--json",
                     "--password-stdin",
                     "--insecure-dev-cert",
                     "--no-token-file",
                     "--token-file",
                     "6 interaction required"
                 })
        {
            AssertEx.Contains(cli.Out, expected);
        }
    }

    [Test]
    public async Task Status_WhenTheNodeDoesNotAnswer_PrintsOneClearLineOnStandardError()
    {
        using var cli = new TestCli(Unreachable);

        var exitCode = await cli.RunAsync("status", "--url", "http://127.0.0.1:5123", "--no-token-file");

        AssertEx.Equal(ExitCodes.NodeNotRunning, exitCode);
        AssertEx.Equal(string.Empty, cli.Out);
        AssertEx.Contains(cli.Error, "xe: The node at http://127.0.0.1:5123/ does not answer");
    }

    [Test]
    public async Task AskJson_WhenTheNodeDoesNotAnswer_WritesOneErrorLineOnStandardOutput()
    {
        using var cli = new TestCli(Unreachable);

        var exitCode = await cli.RunAsync("ask", "hello", "--json", "--url", "http://127.0.0.1:5123", "--no-token-file");

        var line = cli.JsonLines.Single();
        AssertEx.Equal(ExitCodes.NodeNotRunning, exitCode);
        AssertEx.Equal("error", line.GetProperty("type").GetString());
        AssertEx.Equal(ExitCodes.NodeNotRunning, line.GetProperty("exitCode").GetInt32());
        AssertEx.Contains(line.GetProperty("message").GetString(), "does not answer");
        AssertEx.Equal(string.Empty, cli.Error);
    }

    [Test]
    public async Task Status_WhenNoPasswordCanBeHad_ExitsWithTheSignInCode()
    {
        using var handler = new ScriptedHttpHandler(request => request.Path switch
        {
            "/api/local/v1/auth/status" => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"setupRequired":false,"authenticated":false,"vault":"unlocked"}"""),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        using var cli = new TestCli(() => handler);

        var exitCode = await cli.RunAsync("status", "--url", "http://127.0.0.1:5123", "--no-token-file");

        AssertEx.Equal(ExitCodes.AuthOrVault, exitCode);
        AssertEx.Contains(cli.Error, "Sign-in required");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    public void ExitCodeFor_MapsEachExpectedFailure(int row)
    {
        Exception failure = row switch
        {
            0 => new CliCommandException(ExitCodes.InteractionRequired, "x"),
            1 => new NodeDiscoveryException(NodeDiscoveryFailure.InvalidUrl, "x"),
            2 => new NodeDiscoveryException(NodeDiscoveryFailure.InvalidDataDirectory, "x"),
            3 => new NodeDiscoveryException(NodeDiscoveryFailure.NotRunning, "x"),
            4 => new NodeDiscoveryException(NodeDiscoveryFailure.StaleReadyFile, "x"),
            5 => new HttpRequestException("refused"),
            6 => new NodeApiException(HttpStatusCode.Unauthorized, "x"),
            7 => new NodeApiException(HttpStatusCode.ServiceUnavailable, "vault locked"),
            8 => new NodeApiException(HttpStatusCode.NotFound, "x"),
            9 => new HubException("refused"),
            10 => new ChatStreamWatchdogException(noFirstFrame: true),
            _ => new InvalidOperationException("a bug")
        };
        int? expected = row switch
        {
            0 => ExitCodes.InteractionRequired,
            1 or 2 => ExitCodes.Usage,
            3 or 4 or 5 => ExitCodes.NodeNotRunning,
            6 or 7 => ExitCodes.AuthOrVault,
            8 or 9 or 10 => ExitCodes.Failure,
            _ => null
        };

        AssertEx.Equal(expected, CommandRunner.ExitCodeFor(failure), failure.GetType().Name);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ChatAgentSlash_WithNoArgument_LeavesTheConversationsStoredAgentBehind(bool conversationHasAgent)
    {
        var storedAgentId = Guid.NewGuid();
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        var posts = 0;
        var origin = new Uri("http://127.0.0.1:5123/");
        using var handler = new ScriptedHttpHandler(request => request.Path switch
        {
            "/api/local/v1/auth/login" => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", DateTimeOffset.UtcNow.AddHours(1), "refresh-1")),
            "/api/local/v1/chat/conversations" when request.Method == "POST" => Created(posts++),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        var connection = new NodeConnectionOptions
        {
            Origin = origin,
            HandlerFactory = () => handler
        };
        using var cli = new TestCli();
#pragma warning disable CA2000 // The session owns the client and auth, the hub session its never-started connection.
        var http = NodeHttp.CreateClient(connection);
        var auth = new NodeAuthClient(http, origin, TimeProvider.System, tokenFilePath: null, _ => Task.FromResult<string?>("pw"));
        using var session = new NodeSession(new DiscoveredNode
        {
            Origin = origin
        }, connection, http, auth, new NodeAuthStatus());
        await using var hub = new ChatSession(ChatSession.CreateConnection(connection, auth), session.Api, TimeProvider.System);
#pragma warning restore CA2000
        var chat = new ChatCommand(cli.Environment, session, hub, showReasoning: false);

        _ = await chat.HandleSlashAsync(ChatCommand.ParseSlash("/new"), CancellationToken.None);
        _ = await chat.HandleSlashAsync(ChatCommand.ParseSlash("/agent"), CancellationToken.None);

        var creates = handler.Requests.Where(static request => request is { Method: "POST", Path: "/api/local/v1/chat/conversations" }).ToList();
        AssertEx.True(creates.TrueForAll(request => request.Body?.Contains(storedAgentId.ToString("D"), StringComparison.Ordinal) != true), "no create carries an agent");
        // The captured console wraps at 80 columns.
        var output = cli.Out.ReplaceLineEndings(string.Empty);
        if (conversationHasAgent)
        {
            AssertEx.Equal(2, creates.Count, "a conversation bound to an agent is left for a new one");
            AssertEx.Contains(output, $"Agent cleared: new conversation {ids[1]:D} without an agent.");
        }
        else
        {
            AssertEx.Equal(1, creates.Count, "a conversation without an agent is kept");
            AssertEx.Contains(output, "Agent cleared.");
            AssertEx.False(output.Contains("without an agent", StringComparison.Ordinal), "nothing new was started");
        }

        // Only the first conversation is bound to an agent, and only when the row says so.
        HttpResponseMessage Created(int index) =>
            ScriptedHttpHandler.Json(HttpStatusCode.OK,
                $$"""{"conversationId":"{{ids[index]:D}}","agentDefinitionId":{{(conversationHasAgent && index == 0 ? $"\"{storedAgentId:D}\"" : "null")}},"messages":[]}""");
    }

    [Test]
    public async Task ChatCancelSlash_AfterCtrlCWhoseCancelFailed_TargetsTheTurnThatWasInterrupted()
    {
        var origin = new Uri("http://127.0.0.1:5123/");
        var turn = new CancelMessageRequest
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        var cancels = 0;
        using var handler = new ScriptedHttpHandler(request => request.Path switch
        {
            "/api/local/v1/auth/login" => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", DateTimeOffset.UtcNow.AddHours(1), "refresh-1")),
            // Ctrl-C's cancel is refused, so the turn may still run on the node; the later /cancel is accepted.
            "/api/local/v1/chat/cancel" => cancels++ == 0
                ? ScriptedHttpHandler.Json(HttpStatusCode.Conflict, """{"message":"busy"}""")
                : ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"status":"cancelling","cancelled":true}"""),
            _ => throw new InvalidOperationException($"unexpected {request}")
        });
        var connection = new NodeConnectionOptions
        {
            Origin = origin,
            HandlerFactory = () => handler
        };
        using var cli = new TestCli();
#pragma warning disable CA2000 // The session owns the client and auth, the hub session its never-started connection.
        var http = NodeHttp.CreateClient(connection);
        var auth = new NodeAuthClient(http, origin, TimeProvider.System, tokenFilePath: null, _ => Task.FromResult<string?>("pw"));
        using var session = new NodeSession(new DiscoveredNode
        {
            Origin = origin
        }, connection, http, auth, new NodeAuthStatus());
        await using var hub = new ChatSession(ChatSession.CreateConnection(connection, auth), session.Api, TimeProvider.System);
#pragma warning restore CA2000
        var chat = new ChatCommand(cli.Environment, session, hub, showReasoning: false);

        await chat.RunTurnAsync(token => NeverEndingTurnAsync(turn, cli.Interrupts.Raise, token), turn, CancellationToken.None);
        // The chat loop arms a fresh interrupt before it reads the next line.
        _ = cli.Interrupts.Reset();
        _ = await chat.HandleSlashAsync(ChatCommand.ParseSlash("/cancel"), CancellationToken.None);

        var posted = handler.Requests.Where(static request => request.Path == "/api/local/v1/chat/cancel").ToList();
        AssertEx.Equal(2, posted.Count, $"Ctrl-C's cancel and /cancel are both posted; out: {cli.Out}");
        AssertEx.Contains(posted[1].Body, $"\"messageId\":\"{turn.MessageId:D}\"");
        AssertEx.Contains(posted[1].Body, $"\"requestId\":\"{turn.RequestId:D}\"");
    }

    /// <summary>Yields one delta of <paramref name="turn" />, presses Ctrl-C, then never reaches a terminal.</summary>
    private static async IAsyncEnumerable<CliStreamEvent> NeverEndingTurnAsync(CancelMessageRequest turn,
        Action interrupt,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        yield return new CliStreamEvent
        {
            Kind = CliStreamEventKind.Delta,
            ConversationId = turn.ConversationId,
            MessageId = turn.MessageId,
            RequestId = turn.RequestId,
            Text = "Hel",
            Reasoning = string.Empty,
            Source = new ChatStreamEvent
            {
                Type = ChatStreamEventTypes.AssistantDelta,
                ConversationId = turn.ConversationId,
                MessageId = turn.MessageId,
                RequestId = turn.RequestId
            }
        };
        interrupt();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static HttpMessageHandler Unreachable() =>
        new ScriptedHttpHandler(_ => throw new HttpRequestException("Connection refused"));
}
