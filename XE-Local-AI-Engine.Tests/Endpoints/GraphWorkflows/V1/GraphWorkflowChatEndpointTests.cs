namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;
using static Tests.GraphWorkflows.GraphWorkflowChatTestSupport;

/// <summary>
///     The chat send's dispatch matrix, through the real routes, service, store and chat persistence. Ticks are driven by
///     the harness, so a run is Pending, parked or finished only because the test advanced it that far.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowChatEndpointTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    public async Task Send_OnAConversationWithNoRun_StartsABoundRunAndPersistsTheUserMessage()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "  hello  "));

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        AssertEx.Equal("started", body.RootElement.GetProperty("action").GetString());
        var runId = body.RootElement.GetProperty("runId").GetGuid();
        var messageId = body.RootElement.GetProperty("messageId").GetGuid();
        var run = await harness.ReadRunAsync(runId);
        AssertEx.Equal<Guid?>(conversationId, run.ConversationId);
        AssertEx.Equal<Guid?>(messageId, run.TriggerMessageId);
        using var input = JsonDocument.Parse(AssertEx.NotNull(run.InputJson));
        AssertEx.Equal("hello", input.RootElement.GetProperty("message").GetString());
        AssertEx.Equal(messageId, input.RootElement.GetProperty("messageId").GetGuid());
        AssertEx.Equal(conversationId, input.RootElement.GetProperty("conversationId").GetGuid());
        AssertEx.Equal(0, input.RootElement.GetProperty("attachments").GetArrayLength());
        var message = (await MessagesAsync(harness.Services, conversationId)).Single();
        AssertEx.Equal(messageId, message.MessageId);
        AssertEx.Equal("user", message.Role);
        AssertEx.Equal("hello", message.Content);
    }

    [Test]
    public async Task Send_ReplayedWithTheSameRequestId_AnswersTheSameBodyAndWritesNothingTwice()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        var body = Body(definitionId, "hello", requestId: Guid.NewGuid());

        using var first = await PostMessageAsync(Host.Factory, conversationId, body);
        using var replay = await PostMessageAsync(Host.Factory, conversationId, body);

        AssertEx.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        AssertEx.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync(), "a replay answers the same 202.");
        AssertEx.Equal(1, (await MessagesAsync(harness.Services, conversationId)).Count);
        AssertEx.Equal(1, (await harness.ReadConversationRunsAsync(conversationId)).Count);
    }

    [Test]
    public async Task Send_WhileTheRunIsParkedOnAChatInput_AnswersItAndTheEndPublishesTheResult()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        var runId = await StartAndParkAsync(harness, definitionId, conversationId);
        var answerRequestId = Guid.NewGuid();

        using var answer = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "postgres", requestId: answerRequestId));
        using var replay = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "postgres", requestId: answerRequestId));
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        using var body = await ReadJsonAsync(answer);
        AssertEx.Equal("answered", body.RootElement.GetProperty("action").GetString());
        AssertEx.Equal(runId, body.RootElement.GetProperty("runId").GetGuid());
        AssertEx.Equal(await answer.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync(), "an answer replays too, even after the run moved on.");
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        var messages = await MessagesAsync(harness.Services, conversationId);
        AssertEx.Equal("user:hello|user:postgres|assistant:postgres", string.Join('|', messages.Select(static message => $"{message.Role}:{message.Content}")));
        AssertEx.Equal("Answer", messages[^1].AgentName, "a published message is attributed to its node's label.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId), "node.published");
    }

    [Test]
    public async Task Send_AfterACompletedRun_AsksForConfirmationFirst_AndStartsAnotherOnceConfirmed()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph(requireRerunConfirmation: true));
        var conversationId = await CreateConversationAsync(harness.Services);
        var runId = await StartAndParkAsync(harness, definitionId, conversationId);
        using (var answer = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "postgres")))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        }

        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        var before = (await MessagesAsync(harness.Services, conversationId)).Count;

        using var refused = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "again"));
        AssertEx.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        AssertEx.Equal("GraphWorkflowRerunConfirmationRequired", await ConflictTypeAsync(refused));
        AssertEx.Equal(before, (await MessagesAsync(harness.Services, conversationId)).Count, "a refused rerun persists nothing.");

        using var confirmed = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "again", confirmRerun: true));
        AssertEx.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        AssertEx.Equal(2, (await harness.ReadConversationRunsAsync(conversationId)).Count);
    }

    [Test]
    public async Task Send_AfterACompletedRunThatNeedsNoConfirmation_StartsAnother()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph(requireRerunConfirmation: false));
        var conversationId = await CreateConversationAsync(harness.Services);
        var runId = await StartAndParkAsync(harness, definitionId, conversationId);
        using (var answer = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "postgres")))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        }

        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        using var again = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "again"));

        AssertEx.Equal(HttpStatusCode.Accepted, again.StatusCode);
    }

    /// <summary>Two first sends race: one run starts, the loser answers busy and its user message is removed again.</summary>
    [Test]
    public async Task TwoConcurrentFirstSends_StartOneRun_AndLeaveOneUserMessage()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);

        var responses = await Task.WhenAll(PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "first")),
            PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "second")));
        try
        {
            AssertEx.Equal("Accepted,Conflict", string.Join(',', responses.Select(static response => response.StatusCode).Order()));
            AssertEx.Equal("GraphWorkflowRunBusy", await ConflictTypeAsync(responses.Single(static response => response.StatusCode == HttpStatusCode.Conflict)));
            AssertEx.Equal(1, (await MessagesAsync(harness.Services, conversationId)).Count, "the losing send leaves no orphan turn.");
            AssertEx.Equal(1, (await harness.ReadConversationRunsAsync(conversationId)).Count);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    ///     The run write refusing the request after the user message is written: a Tool node whose tool is gone by start time.
    ///     Seeded through the STORE, the shape of a definition saved while the tool still existed.
    /// </summary>
    [Test]
    public async Task Send_WhoseStartIsRefusedAsInvalid_LeavesNoUserMessage()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        Guid definitionId;
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            definitionId = (await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand
            {
                DefinitionId = Guid.NewGuid(),
                Name = $"Saved before its tool went away {Guid.NewGuid():N}",
                GraphJson = ChatToolGraph("no_such_tool"),
                NodeCount = 3,
                Kind = GraphWorkflowDefinitionKind.Chat
            })).Id;
        }

        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello"));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Empty(await MessagesAsync(harness.Services, conversationId), "a refused start leaves no orphan turn.");
        AssertEx.Empty(await harness.ReadConversationRunsAsync(conversationId));
    }

    [Test]
    public async Task Send_ReusingARequestIdThatStartedARunElsewhere_IsAConflict()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var first = await CreateConversationAsync(harness.Services);
        var second = await CreateConversationAsync(harness.Services);
        var requestId = Guid.NewGuid();
        using (var start = await PostMessageAsync(Host.Factory, first, Body(definitionId, "hello", requestId: requestId)))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, start.StatusCode);
        }

        using var reused = await PostMessageAsync(Host.Factory, second, Body(definitionId, "hello", requestId: requestId));

        AssertEx.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(reused));
        AssertEx.Empty(await MessagesAsync(harness.Services, second));
    }

    [Test]
    public async Task Send_ReusingARequestIdThatAnsweredElsewhere_IsAConflict()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var first = await CreateConversationAsync(harness.Services);
        var second = await CreateConversationAsync(harness.Services);
        _ = await StartAndParkAsync(harness, definitionId, first);
        var answerId = Guid.NewGuid();
        using (var answer = await PostMessageAsync(Host.Factory, first, Body(definitionId, "postgres", requestId: answerId)))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        }

        using var reused = await PostMessageAsync(Host.Factory, second, Body(definitionId, "postgres", requestId: answerId));

        AssertEx.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(reused));
        AssertEx.Empty(await MessagesAsync(harness.Services, second));
    }

    /// <summary>An answer naming another workflow than the one parked in the conversation is refused, and persists nothing.</summary>
    [Test]
    public async Task Send_AnsweringWithAnotherDefinition_IsAConflict()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var otherDefinitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        _ = await StartAndParkAsync(harness, definitionId, conversationId);
        var before = (await MessagesAsync(harness.Services, conversationId)).Count;

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(otherDefinitionId, "postgres"));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(response));
        AssertEx.Equal(before, (await MessagesAsync(harness.Services, conversationId)).Count);
    }

    [Test]
    public async Task Send_WhileTheRunIsStillPending_IsBusy()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        using (var start = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello")))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, start.StatusCode);
        }

        await AssertBusyAsync(harness, definitionId, conversationId);
    }

    [Test]
    public async Task Send_WhileTheRunIsCancelling_IsBusy()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        var runId = await StartAndParkAsync(harness, definitionId, conversationId);
        await harness.CancelAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, (await harness.ReadRunAsync(runId)).Status);

        await AssertBusyAsync(harness, definitionId, conversationId);
    }

    [Test]
    public async Task Send_WhileTheRunIsParkedOnAPause_IsBusy()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatPauseGraph);
        var conversationId = await CreateConversationAsync(harness.Services);
        using var start = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello"));
        using var body = await ReadJsonAsync(start);
        var runId = body.RootElement.GetProperty("runId").GetGuid();
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId)).Status);

        await AssertBusyAsync(harness, definitionId, conversationId);
    }

    [Test]
    public async Task Send_WithAttachmentsToAWorkflowThatDoesNotAcceptThem_IsRefusedAndPersistsNothing()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph(acceptsAttachments: false));
        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "see file", attachmentFileIds: [Guid.NewGuid()]));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowAttachmentsNotAccepted", await ConflictTypeAsync(response));
        AssertEx.Empty(await MessagesAsync(harness.Services, conversationId));
    }

    [Test]
    public async Task Send_WithAttachmentsToAWorkflowThatAcceptsThem_CarriesTheirReferencesOnly()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph(acceptsAttachments: true));
        var conversationId = await CreateConversationAsync(harness.Services);
        var fileId = Guid.NewGuid();
        _ = await harness.Services.GetRequiredService<IConversationUploadedFileStore>()
                         .AddAsync(new ConversationUploadedFileInput
                             {
                                 ConversationId = conversationId,
                                 FileId = fileId,
                                 OriginalFileName = "notes.md",
                                 MimeType = "text/markdown",
                                 Extension = ".md",
                                 SizeBytes = 11,
                                 Content = "SECRET-BODY"u8.ToArray(),
                                 ExtractionStatus = DocumentExtractionStatus.Extracted,
                                 ExtractedMarkdown = "SECRET-BODY",
                                 ExtractedChars = 11
                             },
                             CancellationToken.None);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "see file", attachmentFileIds: [fileId]));

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var run = await harness.ReadRunAsync(body.RootElement.GetProperty("runId").GetGuid());
        using var input = JsonDocument.Parse(AssertEx.NotNull(run.InputJson));
        var attachment = input.RootElement.GetProperty("attachments")[0];
        AssertEx.Equal(fileId, attachment.GetProperty("fileId").GetGuid());
        AssertEx.Equal("notes.md", attachment.GetProperty("name").GetString());
        AssertEx.Equal("text", attachment.GetProperty("kind").GetString());
        AssertEx.Equal(11L, attachment.GetProperty("bytes").GetInt64());
        AssertEx.False(run.InputJson!.Contains("SECRET-BODY", StringComparison.Ordinal), "only references travel; the content is S4's consumption.");

        using var unknown = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "other", attachmentFileIds: [Guid.NewGuid()]));
        AssertEx.Equal(HttpStatusCode.BadRequest, unknown.StatusCode, "an id that is not a file of this conversation is a bad request.");
    }

    [Test]
    public async Task Send_OverTheChatMessageCap_Answers400()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, new string('x', (256 * 1024) + 1)));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "a chat message may carry");
        AssertEx.Empty(await MessagesAsync(harness.Services, conversationId));
    }

    [Test]
    public async Task Send_WhoseRunInputIsOverTheInputCap_Answers400()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, new string('x', 70 * 1024)));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "one run input may carry");
        AssertEx.Empty(await MessagesAsync(harness.Services, conversationId));
    }

    [Test]
    public async Task Send_ToAnUnknownDefinition_Answers404()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(Guid.NewGuid(), "hello"));

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Send_ToAStandardDefinition_Answers400()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.PauseTwoDecisions);
        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello"));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "only a Chat workflow runs from a conversation");
    }

    [Test]
    public async Task Send_ToAnUnknownConversation_Answers404()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());

        using var response = await PostMessageAsync(Host.Factory, Guid.NewGuid(), Body(definitionId, "hello"));

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Send_ToAWorkSessionConversation_Answers400()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services, kind: NodeConversationKind.WorkSession);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello"));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Send_ToARemoteConversation_IsRefusedAsReadOnly()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services, origin: NodeChatOriginValues.Remote);

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello"));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ReadOnlyConversation", await ConflictTypeAsync(response));
    }

    [Test]
    public async Task ListRuns_ShowsTheParkedInputAndTheDefinitionName_NewestFirst()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        var runId = await StartAndParkAsync(harness, definitionId, conversationId);

        using var response = await GetRunsAsync(Host.Factory, conversationId);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var run = body.RootElement.GetProperty("runs")[0];
        AssertEx.Equal(runId, run.GetProperty("run").GetProperty("id").GetGuid());
        AssertEx.Equal(definitionId, run.GetProperty("definitionId").GetGuid());
        AssertEx.True(run.GetProperty("definitionName").GetString()!.StartsWith("Seeded", StringComparison.Ordinal));
        AssertEx.Equal("ask", run.GetProperty("pendingInput").GetProperty("nodeKey").GetString());
        AssertEx.Equal("Which database?", run.GetProperty("pendingInput").GetProperty("prompt").GetString());
        AssertEx.Equal(JsonValueKind.Null, run.GetProperty("steerable").ValueKind);
        AssertEx.Equal(JsonValueKind.String, run.GetProperty("triggerMessageId").ValueKind);
    }

    [Test]
    public async Task ListRuns_OfAnUnknownConversation_Answers404()
    {
        using var response = await GetRunsAsync(Host.Factory, Guid.NewGuid());

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>404 ahead of auth on a disabled node, like every other route of the family.</summary>
    [Test]
    public async Task Send_WhenTheFeatureIsDisabled_ReturnsNotFound()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            }
        };

        using var response = await PostMessageAsync(factory, Guid.NewGuid(), Body(Guid.NewGuid(), "hello"));

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Guid> StartAndParkAsync(GraphWorkflowHarness harness, Guid definitionId, Guid conversationId)
    {
        using var start = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello"));
        AssertEx.Equal(HttpStatusCode.Accepted, start.StatusCode);
        using var body = await ReadJsonAsync(start);
        var runId = body.RootElement.GetProperty("runId").GetGuid();
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "ask")).Status, "the run parks on its chat input.");
        return runId;
    }

    private async Task AssertBusyAsync(GraphWorkflowHarness harness, Guid definitionId, Guid conversationId)
    {
        var before = (await MessagesAsync(harness.Services, conversationId)).Count;

        using var response = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "not now"));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunBusy", await ConflictTypeAsync(response));
        AssertEx.Equal(before, (await MessagesAsync(harness.Services, conversationId)).Count, "a busy send persists nothing.");
    }

    /// <summary>A Chat <c>Start → Tool → End</c> graph over one named tool.</summary>
    private static string ChatToolGraph(string toolName) =>
        $$"""
          {
            "schemaVersion": 1,
            "kind": "Chat",
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "call", "kind": "Tool", "config": { "toolName": "{{toolName}}" } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "call" },
              { "key": "e2", "from": "call", "to": "done" }
            ]
          }
          """;
}
