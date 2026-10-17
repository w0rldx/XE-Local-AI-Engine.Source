namespace XE_Local_AI_Engine.Cli.Tests.Commands;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Auth;
using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Cli.Rendering;
using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Cli.Tests.Testing;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One turn through <see cref="TurnRunner" /> against a scripted event stream and a scripted node: the
///     <c>ask --json</c> line contract, the no-terminal policy for approvals and questions, and Ctrl-C.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TurnRunnerTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:5123/");
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();
    private static readonly Guid RequestId = Guid.NewGuid();

    [Test]
    public async Task AskJson_WritesOneLinePerEventWithTheFullKeySet_ThenTheSummary()
    {
        using var node = new ScriptedNode();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);

        var outcome = await new TurnRunner(node.Api, renderer, prompter: null, TimeProvider.System).RunAsync(_ => PlayAsync(Event(CliStreamEventKind.Status, ChatStreamEventTypes.AssistantStreaming),
                Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Hel", reasoning: "hmm"),
                Event(CliStreamEventKind.ToolActivity, ChatStreamEventTypes.ToolCallRequested, toolName: "read_file"),
                Event(CliStreamEventKind.ToolActivity, ChatStreamEventTypes.ToolCallCompleted, toolName: "read_file"),
                Event(CliStreamEventKind.Notice, ChatStreamEventTypes.AssistantNotice, text: "Model substituted"),
                Event(CliStreamEventKind.Completed, ChatStreamEventTypes.AssistantCompleted, text: "Hello")),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);

        var lines = Lines(output);
        AssertEx.Equal(ExitCodes.Success, outcome.ExitCode);
        AssertEx.Equal(7, lines.Count);
        var expectedKeys = string.Join(',', typeof(AskJsonLine).GetProperties().Select(static property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Order(StringComparer.Ordinal));
        foreach (var line in lines.Take(6))
        {
            AssertEx.Equal("event", line.GetProperty("type").GetString());
            AssertEx.Equal(expectedKeys, string.Join(',', line.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)));
            AssertEx.Equal(RequestId.ToString("D"), line.GetProperty("requestId").GetString());
        }

        AssertEx.Equal("status,delta,toolActivity,toolActivity,notice,completed", string.Join(',', lines.Take(6).Select(static line => line.GetProperty("kind").GetString())));
        AssertEx.Equal("tool-call-completed", lines[3].GetProperty("frameType").GetString());
        AssertEx.Equal("read_file", lines[3].GetProperty("toolName").GetString());
        AssertEx.Equal("hmm", lines[1].GetProperty("reasoning").GetString());
        var summary = lines[6];
        AssertEx.Equal("summary", summary.GetProperty("type").GetString());
        AssertEx.Equal("completed", summary.GetProperty("outcome").GetString());
        AssertEx.Equal(0, summary.GetProperty("exitCode").GetInt32());
        AssertEx.Equal("Hello", summary.GetProperty("text").GetString());
        AssertEx.Equal(MessageId.ToString("D"), summary.GetProperty("messageId").GetString());
    }

    [Test]
    public async Task Run_WithoutAPrompter_DeniesAnApprovalWithANoticeAndReportsInteractionRequired()
    {
        using var node = new ScriptedNode();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);

        // The deny is sent beside the loop; the node, parked on the approval, completes only once it has it.
        var outcome = await new TurnRunner(node.Api, renderer, prompter: null, TimeProvider.System).RunAsync(token => PlayAsync(node.ResolvePosted.Task,
                afterFirst: null,
                [
                    Event(CliStreamEventKind.ApprovalRequested, ChatStreamEventTypes.ApprovalRequested, toolName: "write_file", approvalRequestId: "approval-1", sessionScopeEligible: true),
                    Event(CliStreamEventKind.Completed, ChatStreamEventTypes.AssistantCompleted, text: "I could not write it.")
                ],
                token),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);

        var resolve = node.Handler.Requests.Single(request => request.Path == "/api/local/v1/chat/approvals/resolve");
        AssertEx.Contains(resolve.Body, "\"requestId\":\"approval-1\"");
        AssertEx.Contains(resolve.Body, "\"approved\":false");
        AssertEx.Equal(ExitCodes.InteractionRequired, outcome.ExitCode);
        var lines = Lines(output);
        AssertEx.ContainsSingle(lines, line => line.GetProperty("type").GetString() == "notice" && line.GetProperty("kind").GetString() == "approvalDenied");
        AssertEx.True(lines[^1].GetProperty("interactionRequired").GetBoolean());
        AssertEx.Equal(ExitCodes.InteractionRequired, lines[^1].GetProperty("exitCode").GetInt32());
    }

    [Test]
    public async Task Run_WithoutAPrompter_OnCtrlCWhileTheDenyIsStalled_AbandonsItAndEndsOnTheCancelledTerminal()
    {
        using var node = new ScriptedNode(hangingPath: "/api/local/v1/chat/approvals/resolve");
        using var interrupts = new CliInterrupts();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);

        var run = new TurnRunner(node.Api, renderer, prompter: null, TimeProvider.System).RunAsync(token => PlayAsync(node.CancelPosted.Task,
                afterFirst: null,
                [
                    Event(CliStreamEventKind.ApprovalRequested, ChatStreamEventTypes.ApprovalRequested, toolName: "write_file", approvalRequestId: "approval-1"),
                    Event(CliStreamEventKind.Cancelled, ChatStreamEventTypes.AssistantCancelled)
                ],
                token),
            Turn(),
            interrupts.Token,
            CancellationToken.None);
        await node.ResolvePosted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        interrupts.Raise();
        await AssertEx.CompletesAsync(run, TimeSpan.FromSeconds(10), "Ctrl-C abandons the stalled deny and the cancelled terminal ends the turn");
        var outcome = await run;

        AssertEx.True(node.HangAbandoned.Task.IsCompleted, "the deny request observed the cancellation");
        AssertEx.True(outcome.UserCancelled);
        AssertEx.Equal(CliStreamEventKind.Cancelled, outcome.Terminal?.Kind);
        // The denied approval outranks the Ctrl-C: a script learns that the turn needed a terminal.
        AssertEx.Equal(ExitCodes.InteractionRequired, outcome.ExitCode);
        AssertEx.True(Lines(output)[^1].GetProperty("interactionRequired").GetBoolean());
    }

    [Test]
    public async Task Run_WithoutAPrompter_CancelsTheTurnOnAQuestion()
    {
        using var node = new ScriptedNode();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);

        var outcome = await new TurnRunner(node.Api, renderer, prompter: null, TimeProvider.System).RunAsync(token => PlayAsync(node.CancelPosted.Task,
                afterFirst: null,
                [
                    Event(CliStreamEventKind.QuestionRequested, ChatStreamEventTypes.QuestionRequested, questionRequestId: "question-1"),
                    Event(CliStreamEventKind.Cancelled, ChatStreamEventTypes.AssistantCancelled)
                ],
                token),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);

        var cancel = node.Handler.Requests.Single(request => request.Path == "/api/local/v1/chat/cancel");
        AssertEx.Contains(cancel.Body, $"\"requestId\":\"{RequestId:D}\"");
        AssertEx.Contains(cancel.Body, $"\"messageId\":\"{MessageId:D}\"");
        AssertEx.Equal(ExitCodes.InteractionRequired, outcome.ExitCode);
        AssertEx.False(outcome.UserCancelled);
        AssertEx.Empty(node.Handler.Requests.Where(request => request.Path == "/api/local/v1/chat/questions/resolve"));
        AssertEx.ContainsSingle(Lines(output), line => line.TryGetProperty("kind", out var kind) && kind.GetString() == "questionRefused");
    }

    [Test]
    public async Task Run_OnCtrlC_PostsChatCancelAndEndsOnTheCancelledTerminalWithExitFive()
    {
        using var node = new ScriptedNode();
        using var interrupts = new CliInterrupts();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);

        var outcome = await new TurnRunner(node.Api, renderer, prompter: null, TimeProvider.System).RunAsync(token => PlayAsync(node.CancelPosted.Task,
                interrupts.Raise,
                [
                    Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Hel"),
                    Event(CliStreamEventKind.Cancelled, ChatStreamEventTypes.AssistantCancelled, text: "Hel")
                ],
                token),
            Turn(),
            interrupts.Token,
            CancellationToken.None);

        AssertEx.Equal(ExitCodes.CancelledByUser, outcome.ExitCode);
        AssertEx.True(outcome.UserCancelled);
        AssertEx.Equal(CliStreamEventKind.Cancelled, outcome.Terminal?.Kind);
        AssertEx.ContainsSingle(node.Handler.Requests, request => request.Path == "/api/local/v1/chat/cancel");
    }

    [Test]
    public async Task Run_OnCtrlC_WhenNoCancelledTerminalArrives_StopsListeningAfterTheGrace()
    {
        var time = new ManualTimeProvider();
        using var node = new ScriptedNode();
        using var interrupts = new CliInterrupts();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);

        var run = new TurnRunner(node.Api, renderer, prompter: null, time).RunAsync(token => PlayAsync(new TaskCompletionSource().Task,
                interrupts.Raise,
                [Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Hel")],
                token),
            Turn(),
            interrupts.Token,
            CancellationToken.None);
        await AssertEx.EventuallyAsync(() => node.CancelPosted.Task.IsCompleted && time.ArmedTimerCount == 1, TimeSpan.FromSeconds(10), "the cancel is posted and the grace armed");
        await AssertEx.StaysIncompleteAsync(run, "the client keeps listening inside the grace");
        time.Advance(TurnRunner.CancelGrace + TimeSpan.FromSeconds(1));
        var outcome = await run;

        AssertEx.Equal(ExitCodes.CancelledByUser, outcome.ExitCode);
        AssertEx.Null(outcome.Terminal);
        AssertEx.Equal("none", Lines(output)[^1].GetProperty("outcome").GetString());
    }

    [Test]
    public async Task Run_OnCtrlC_WhenTheCancelRequestNeverAnswers_RendersCancelFailedAndStopsListeningAfterTheGrace()
    {
        var time = new ManualTimeProvider();
        using var node = new ScriptedNode(hangingPath: "/api/local/v1/chat/cancel");
        using var interrupts = new CliInterrupts();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);

        var run = new TurnRunner(node.Api, renderer, prompter: null, time).RunAsync(token => PlayAsync(new TaskCompletionSource().Task,
                interrupts.Raise,
                [Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Hel")],
                token),
            Turn(),
            interrupts.Token,
            CancellationToken.None);
        await AssertEx.EventuallyAsync(() => node.CancelPosted.Task.IsCompleted && time.ArmedTimerCount == 1, TimeSpan.FromSeconds(10), "the cancel is in flight under the grace");
        await AssertEx.StaysIncompleteAsync(run, "the client waits for the cancel inside the grace");
        time.Advance(TurnRunner.CancelGrace);
        var outcome = await run;

        AssertEx.True(outcome.UserCancelled);
        AssertEx.Equal(ExitCodes.CancelledByUser, outcome.ExitCode);
        AssertEx.Null(outcome.Terminal);
        AssertEx.ContainsSingle(Lines(output), line => line.TryGetProperty("kind", out var kind) && kind.GetString() == "cancelFailed");
    }

    [Test]
    public async Task AskText_StreamsOnlyTheAnswerToStandardOutputAndTheRestToStandardError()
    {
        using var node = new ScriptedNode();
        await using var content = new StringWriter();
        await using var side = new StringWriter();
        using var renderer = new ConsoleTurnRenderer(content, TestCli.Capture(side), sameScreen: false, showReasoning: false, TimeProvider.System);

        await new TurnRunner(node.Api, renderer, prompter: null, TimeProvider.System).RunAsync(_ =>
                PlayAsync(Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, reasoning: "let me think"),
                    Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, reasoning: " more"),
                    Event(CliStreamEventKind.Delta, ChatStreamEventTypes.AssistantDelta, text: "Hel"),
                    Event(CliStreamEventKind.ToolActivity, ChatStreamEventTypes.ToolCallRequested, toolName: "read_file"),
                    Event(CliStreamEventKind.Snapshot, ChatStreamEventTypes.AssistantSnapshot, text: "Hello [b]"),
                    Event(CliStreamEventKind.Completed, ChatStreamEventTypes.AssistantCompleted, text: "Hello [b] world")),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);

        AssertEx.Equal("Hello [b] world" + Environment.NewLine, content.ToString());
        AssertEx.Contains(side.ToString(), "thinking...");
        AssertEx.Equal(1, side.ToString().Split("thinking", StringSplitOptions.None).Length - 1, "the thinking marker is shown once");
        AssertEx.Contains(side.ToString(), "> read_file");
        AssertEx.False(side.ToString().Contains("let me think", StringComparison.Ordinal), "reasoning stays hidden without --show-reasoning");
    }

    [Test]
    public async Task Run_ADeniedToolCall_RendersItsCompletionAsDenied()
    {
        using var node = new ScriptedNode();
        await using var content = new StringWriter();
        await using var side = new StringWriter();
        using var renderer = new ConsoleTurnRenderer(content, TestCli.Capture(side), sameScreen: false, showReasoning: false, TimeProvider.System);

        // The node reports a rejected call as a plain result with IsError false; only this client knows it denied it.
        // The node, parked on the approval, runs the call only once the deny has reached it.
        await new TurnRunner(node.Api, renderer, prompter: null, TimeProvider.System).RunAsync(token =>
                PlayAfterAsync(Event(CliStreamEventKind.ApprovalRequested, ChatStreamEventTypes.ApprovalRequested, toolName: "run_python", approvalRequestId: "approval-1"),
                    node.ResolvePosted.Task,
                    [
                        Event(CliStreamEventKind.ToolActivity, ChatStreamEventTypes.ToolCallRequested, toolName: "run_python"),
                        Event(CliStreamEventKind.ToolActivity, ChatStreamEventTypes.ToolCallCompleted, toolName: "run_python"),
                        Event(CliStreamEventKind.Completed, ChatStreamEventTypes.AssistantCompleted, text: "I could not run it.")
                    ],
                    token),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);

        AssertEx.Contains(side.ToString(), "< run_python denied");
        AssertEx.False(side.ToString().Contains("run_python ok", StringComparison.Ordinal), "a denied call is never shown as ok");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Run_WhenATerminalArrivesWhileAPromptIsOpen_DismissesThePromptAndSendsNoAnswer(bool approval)
    {
        using var node = new ScriptedNode();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);
        var console = new GatedConsole();

        // The node timed the prompt out (or the SPA cancelled the turn) while the person had not answered yet.
        var run = new TurnRunner(node.Api, renderer, new TurnPrompter(console), TimeProvider.System).RunAsync(
            token => PlayAsync(console.Reading.Task, afterFirst: null, [Prompt(approval), Event(CliStreamEventKind.Cancelled, ChatStreamEventTypes.AssistantCancelled)], token),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);
        await AssertEx.CompletesAsync(run, TimeSpan.FromSeconds(10), "the terminal is read while the prompt is open");
        var outcome = await run;

        AssertEx.Equal(CliStreamEventKind.Cancelled, outcome.Terminal?.Kind);
        AssertEx.True(console.ReadCancelled.Task.IsCompleted, "the open prompt was cancelled");
        AssertEx.Empty(node.Handler.Requests.Where(static request => request.Path.Contains("/resolve", StringComparison.Ordinal)));
    }

    [Test]
    [Arguments(true, "/api/local/v1/chat/approvals/resolve", "\"approved\":true")]
    [Arguments(false, "/api/local/v1/chat/questions/resolve", "\"selected\":[\"A\"]")]
    public async Task Run_WhenThePersonAnswersBeforeTheTerminal_SendsTheAnswer(bool approval, string resolvePath, string expectedBody)
    {
        using var node = new ScriptedNode();
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);
        var console = new GatedConsole();
        console.Key.SetResult(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var outcome = await new TurnRunner(node.Api, renderer, new TurnPrompter(console), TimeProvider.System).RunAsync(
            token => PlayAsync(node.ResolvePosted.Task, afterFirst: null, [Prompt(approval), Event(CliStreamEventKind.Completed, ChatStreamEventTypes.AssistantCompleted, text: "Done")], token),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);

        AssertEx.Equal(CliStreamEventKind.Completed, outcome.Terminal?.Kind);
        AssertEx.Contains(node.Handler.Requests.Single(request => request.Path == resolvePath).Body, expectedBody);
    }

    [Test]
    [Arguments(true, "/api/local/v1/chat/approvals/resolve")]
    [Arguments(false, "/api/local/v1/chat/questions/resolve")]
    public async Task Run_WhenATerminalArrivesWhileTheAnswerIsInFlight_AbandonsTheResolveRequest(bool approval, string resolvePath)
    {
        using var node = new ScriptedNode(hangingPath: resolvePath);
        await using var output = new StringWriter();
        using var renderer = new JsonTurnRenderer(output);
        var console = new GatedConsole();
        console.Key.SetResult(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        // The answer reached the node, which stalls on it; the turn then ends on its own.
        var run = new TurnRunner(node.Api, renderer, new TurnPrompter(console), TimeProvider.System).RunAsync(
            token => PlayAsync(node.ResolvePosted.Task, afterFirst: null, [Prompt(approval), Event(CliStreamEventKind.Cancelled, ChatStreamEventTypes.AssistantCancelled)], token),
            Turn(),
            CancellationToken.None,
            CancellationToken.None);
        await AssertEx.CompletesAsync(run, TimeSpan.FromSeconds(10), "the terminal abandons the stalled resolve");
        var outcome = await run;

        AssertEx.Equal(CliStreamEventKind.Cancelled, outcome.Terminal?.Kind);
        AssertEx.True(node.HangAbandoned.Task.IsCompleted, "the resolve request observed the cancellation");
    }

    private static CliStreamEvent Prompt(bool approval) =>
        approval
            ? Event(CliStreamEventKind.ApprovalRequested, ChatStreamEventTypes.ApprovalRequested, toolName: "write_file", approvalRequestId: "approval-1")
            : Event(CliStreamEventKind.QuestionRequested, ChatStreamEventTypes.QuestionRequested, questionRequestId: "question-1");

    private static CancelMessageRequest Turn() =>
        new()
        {
            ConversationId = ConversationId,
            MessageId = MessageId,
            RequestId = RequestId
        };

    private static List<JsonElement> Lines(StringWriter output) =>
        [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => JsonDocument.Parse(line).RootElement.Clone())];

    private static CliStreamEvent Event(CliStreamEventKind kind,
        string frameType,
        string text = "",
        string reasoning = "",
        string? toolName = null,
        string? approvalRequestId = null,
        string? questionRequestId = null,
        bool? sessionScopeEligible = null) =>
        new()
        {
            Kind = kind,
            ConversationId = ConversationId,
            MessageId = MessageId,
            RequestId = RequestId,
            Text = text,
            Reasoning = reasoning,
            Source = new ChatStreamEvent
            {
                Type = frameType,
                ConversationId = ConversationId,
                MessageId = MessageId,
                RequestId = RequestId,
                ToolName = toolName,
                ToolCallId = toolName is null ? null : "call-1",
                IsError = frameType == ChatStreamEventTypes.ToolCallCompleted ? false : null,
                ApprovalRequestId = approvalRequestId,
                QuestionRequestId = questionRequestId,
                Questions = questionRequestId is null ? null : """[{"question":"Which?","options":[{"label":"A"}]}]""",
                SessionScopeEligible = sessionScopeEligible
            }
        };

    private static async IAsyncEnumerable<CliStreamEvent> PlayAsync(params CliStreamEvent[] events)
    {
        foreach (var item in events)
        {
            await Task.Yield();
            yield return item;
        }
    }

    /// <summary>Yields <paramref name="first" />, waits for <paramref name="release" />, then yields the rest.</summary>
    private static async IAsyncEnumerable<CliStreamEvent> PlayAfterAsync(CliStreamEvent first,
        Task release,
        CliStreamEvent[] rest,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        yield return first;
        await release.WaitAsync(cancellationToken);
        foreach (var item in rest)
        {
            yield return item;
        }
    }

    /// <summary>Yields every event but the last, runs <paramref name="afterFirst" />, waits for <paramref name="release" />, then yields the last.</summary>
    private static async IAsyncEnumerable<CliStreamEvent> PlayAsync(Task release,
        Action? afterFirst,
        CliStreamEvent[] events,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < events.Length; index++)
        {
            if (index == events.Length - 1 && index > 0)
            {
                await release.WaitAsync(cancellationToken);
            }

            yield return events[index];
            if (index == 0)
            {
                afterFirst?.Invoke();
            }
        }

        if (events.Length == 1)
        {
            await release.WaitAsync(cancellationToken);
        }
    }

    /// <summary>A scripted node behind a real <see cref="NodeApiClient" />: it signs in, records resolves and acknowledges cancels.</summary>
    private sealed class ScriptedNode : IDisposable
    {
        private readonly HttpClient _http;
        private readonly NodeAuthClient _auth;

        /// <param name="hangingPath">A request path that never answers; only its own token ends it.</param>
        public ScriptedNode(string? hangingPath = null)
        {
            Handler = new ScriptedHttpHandler(request => request.Path switch
            {
                "/api/local/v1/auth/login" => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", DateTimeOffset.UtcNow.AddHours(1), "refresh-1")),
                "/api/local/v1/chat/cancel" => Cancelled(),
                "/api/local/v1/chat/approvals/resolve" or "/api/local/v1/chat/questions/resolve" => Resolved(),
                _ => throw new InvalidOperationException($"unexpected {request}")
            });
            _http = NodeHttp.CreateClient(new NodeConnectionOptions
            {
                Origin = Origin,
                HandlerFactory = hangingPath is null ? () => Handler : () => new HangingHandler(Handler, hangingPath, HangAbandoned)
            });
            _auth = new NodeAuthClient(_http, Origin, TimeProvider.System, tokenFilePath: null, _ => Task.FromResult<string?>("pw"));
            Api = new NodeApiClient(_http, _auth);
        }

        public ScriptedHttpHandler Handler { get; }

        public NodeApiClient Api { get; }

        public TaskCompletionSource CancelPosted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResolvePosted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once the hanging request's token fired.</summary>
        public TaskCompletionSource HangAbandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            _auth.Dispose();
            _http.Dispose();
        }

        private HttpResponseMessage Resolved()
        {
            ResolvePosted.TrySetResult();
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, "{}");
        }

        private HttpResponseMessage Cancelled()
        {
            CancelPosted.TrySetResult();
            return ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"status":"cancelling","cancelled":true}""");
        }
    }

    /// <summary>An interactive console whose every key read waits for <see cref="Key" />, so the test decides when a prompt is answered.</summary>
    private sealed class GatedConsole : IAnsiConsole, IAnsiConsoleInput
    {
        private readonly IAnsiConsole _inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(TextWriter.Null),
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            // Spectre's built-in CI enrichers (GITHUB_ACTIONS and friends) force Interactive=false after these settings,
            // which makes every prompt refuse to read and leaves the gated turn waiting forever on a CI runner.
            Enrichment = new ProfileEnrichment
            {
                UseDefaultEnrichers = false
            }
        });

        public TaskCompletionSource<ConsoleKeyInfo> Key { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once a prompt waits for a key.</summary>
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Profile Profile => _inner.Profile;

        public IAnsiConsoleCursor Cursor => _inner.Cursor;

        public IAnsiConsoleInput Input => this;

        public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;

        public RenderPipeline Pipeline => _inner.Pipeline;

        public void Clear(bool home) =>
            _inner.Clear(home);

        public void Write(IRenderable renderable) =>
            _inner.Write(renderable);

        public void WriteAnsi(Action<AnsiWriter> action) =>
            _inner.WriteAnsi(action);

        public bool IsKeyAvailable() =>
            Key.Task.IsCompleted;

        public ConsoleKeyInfo? ReadKey(bool intercept) =>
            throw new NotSupportedException("prompts read keys asynchronously");

        public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
        {
            Reading.TrySetResult();
            try
            {
                return await Key.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ReadCancelled.TrySetResult();
                throw;
            }
        }
    }

    /// <summary>Records every request through the script, then holds one path open until its token fires.</summary>
    private sealed class HangingHandler : DelegatingHandler
    {
        private readonly string _path;
        private readonly TaskCompletionSource _abandoned;

        public HangingHandler(ScriptedHttpHandler inner, string path, TaskCompletionSource abandoned) : base(inner)
        {
            _path = path;
            _abandoned = abandoned;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath == _path)
            {
                response.Dispose();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _abandoned.TrySetResult();
                    throw;
                }
            }

            return response;
        }
    }
}
