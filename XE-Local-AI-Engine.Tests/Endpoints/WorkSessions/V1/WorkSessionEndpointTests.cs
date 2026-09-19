namespace XE_Local_AI_Engine.Tests.Endpoints.WorkSessions.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class WorkSessionEndpointTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ArtifactId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ConversationId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string Root = "/api/local/v1/work-sessions";
    private const string Session = $"{Root}/11111111-1111-1111-1111-111111111111";

    [Test]
    [Arguments("GET", Root)]
    [Arguments("POST", Root)]
    [Arguments("GET", Session)]
    [Arguments("PATCH", Session)]
    [Arguments("DELETE", Session)]
    [Arguments("POST", $"{Session}/start")]
    [Arguments("POST", $"{Session}/pause")]
    [Arguments("POST", $"{Session}/resume")]
    [Arguments("POST", $"{Session}/cancel")]
    [Arguments("GET", $"{Session}/tasks")]
    [Arguments("GET", $"{Session}/findings")]
    [Arguments("GET", $"{Session}/artifacts")]
    [Arguments("GET", $"{Session}/checkpoints")]
    [Arguments("GET", $"{Session}/events")]
    [Arguments("GET", $"{Session}/artifacts/22222222-2222-2222-2222-222222222222/content")]
    [Arguments("POST", $"{Session}/messages")]
    public async Task WorkSessionRoute_WhenOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = EnabledFactory(Substitute.For<IWorkSessionService>());
        using var client = factory.CreateClient();
        using var request = Request(method, route);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
    }

    [Test]
    public async Task GetWorkSession_WithRouteSessionId_BindsTheIdAndReturnsOk()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.GetAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Detail());
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", Session);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await service.Received(1).GetAsync(SessionId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListWorkSessions_ProjectsTheSummaryRows()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.ListAsync(Arg.Any<CancellationToken>())
               .Returns([new WorkSessionSummary { Id = SessionId, Title = "title", Kind = AgentWorkSessionKind.Research, Status = AgentWorkSessionStatus.Paused, AgentDefinitionId = AgentId, StepCount = 4, UpdatedUtc = 99 }]);
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", Root);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var item = document.RootElement.GetProperty("items")[0];
        AssertEx.Equal("Research", item.GetProperty("kind").GetString());
        AssertEx.Equal("Paused", item.GetProperty("status").GetString());
        AssertEx.Equal(4, item.GetProperty("stepCount").GetInt32());
    }

    [Test]
    [Arguments("tasks")]
    [Arguments("findings")]
    [Arguments("artifacts")]
    [Arguments("checkpoints")]
    public async Task Feed_ForwardsSinceSeq(string feed)
    {
        var service = SubstituteWithEmptyFeeds();
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/{feed}?sinceSeq=17");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        switch (feed)
        {
            case "tasks":
                await service.Received(1).ListTasksAsync(SessionId, 17, Arg.Any<CancellationToken>());
                break;
            case "findings":
                await service.Received(1).ListFindingsAsync(SessionId, 17, Arg.Any<CancellationToken>());
                break;
            case "artifacts":
                await service.Received(1).ListArtifactsAsync(SessionId, 17, Arg.Any<CancellationToken>());
                break;
            default:
                await service.Received(1).ListCheckpointsAsync(SessionId, 17, Arg.Any<CancellationToken>());
                break;
        }
    }

    [Test]
    public async Task EventFeed_ForwardsSinceSeqAndLimit()
    {
        var service = SubstituteWithEmptyFeeds();
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/events?sinceSeq=3&limit=25");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await service.Received(1).ListEventsAsync(SessionId, 3, 25, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TaskFeed_ReportsThePagesHighestSequence_NotItsLastRows()
    {
        var service = SubstituteWithEmptyFeeds();
        // The feeds are ordered by creation step, so a re-stamped task keeps its place and the newest sequence sits in
        // the middle of the page. Paging from the last row would replay these two rows on every poll.
        service.ListTasksAsync(SessionId, 0, Arg.Any<CancellationToken>())
               .Returns([TaskRow(sequence: 9), TaskRow(sequence: 2)]);
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/tasks");
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(9L, document.RootElement.GetProperty("lastSequence").GetInt64());
    }

    [Test]
    public async Task EventFeed_WhenThePageIsFull_ReportsHasMore()
    {
        var service = SubstituteWithEmptyFeeds();
        service.ListEventsAsync(SessionId, 0, 2, Arg.Any<CancellationToken>())
               .Returns([Event(sequence: 1), Event(sequence: 2)]);
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/events?limit=2");
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        AssertEx.True(document.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Test]
    public async Task EventFeed_CarriesTheOperationIdOfTheToolCallThatProducedTheRow()
    {
        // The journal row already records which tool call it belongs to; the feed has to hand it over, or a client
        // cannot group a step's rows by operation.
        var operationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var service = SubstituteWithEmptyFeeds();
        service.ListEventsAsync(SessionId, 0, 50, Arg.Any<CancellationToken>()).Returns([Event(sequence: 1, operationId)]);
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/events?limit=50");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(operationId, document.RootElement.GetProperty("items")[0].GetProperty("operationId").GetGuid());
    }

    [Test]
    public async Task CreateWorkSession_WhenValid_ReturnsCreatedWithLocation()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.CreateAsync(Arg.Any<CreateWorkSessionRequestModel>(), Arg.Any<CancellationToken>()).Returns(Detail());
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory,
                "POST",
                Root,
                """{"title":"Study","objective":"Find out","kind":"Research","agentDefinitionId":"33333333-3333-3333-3333-333333333333"}""");

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.NotNull(response.Headers.Location);
        await service.Received(1)
                     .CreateAsync(Arg.Is<CreateWorkSessionRequestModel>(model => model.Kind == AgentWorkSessionKind.Research
                                                                                 && model.Title == "Study"
                                                                                 && model.AgentDefinitionId == AgentId),
                         Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("""{"title":"","objective":"o","kind":"Research","agentDefinitionId":"33333333-3333-3333-3333-333333333333"}""")]
    [Arguments("""{"title":"t","objective":"","kind":"Research","agentDefinitionId":"33333333-3333-3333-3333-333333333333"}""")]
    [Arguments("""{"title":"t","objective":"o","kind":"Nonsense","agentDefinitionId":"33333333-3333-3333-3333-333333333333"}""")]
    [Arguments("""{"title":"t","objective":"o","kind":"Development","agentDefinitionId":"33333333-3333-3333-3333-333333333333"}""")]
    [Arguments("""{"title":"t","objective":"o","kind":"Research","agentDefinitionId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task CreateWorkSession_WhenTheShapeIsWrong_ReturnsBadRequestAndNeverReachesTheService(string body)
    {
        var service = Substitute.For<IWorkSessionService>();
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "POST", Root, body);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceive().CreateAsync(Arg.Any<CreateWorkSessionRequestModel>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateWorkSession_WithAnOverLongTitle_ReturnsBadRequest()
    {
        var service = Substitute.For<IWorkSessionService>();
        await using var factory = EnabledFactory(service);
        var title = new string('t', 201);

        using var response = await SendAsync(factory,
                "POST",
                Root,
                $$"""{"title":"{{title}}","objective":"o","kind":"Research","agentDefinitionId":"33333333-3333-3333-3333-333333333333"}""");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    [Arguments("sinceSeq=-1")]
    [Arguments("limit=0")]
    [Arguments("limit=501")]
    public async Task EventFeed_WithAnOutOfRangeQuery_ReturnsBadRequest(string query)
    {
        var service = SubstituteWithEmptyFeeds();
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/events?{query}");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceive().ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TaskFeed_WithANegativeSinceSeq_ReturnsBadRequest()
    {
        var service = SubstituteWithEmptyFeeds();
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/tasks?sinceSeq=-1");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    [Arguments("GET", Session, null)]
    [Arguments("PATCH", Session, """{"title":"renamed"}""")]
    [Arguments("DELETE", Session, null)]
    [Arguments("POST", $"{Session}/start", null)]
    [Arguments("POST", $"{Session}/pause", null)]
    [Arguments("POST", $"{Session}/resume", null)]
    [Arguments("POST", $"{Session}/cancel", null)]
    [Arguments("GET", $"{Session}/tasks", null)]
    [Arguments("GET", $"{Session}/findings", null)]
    [Arguments("GET", $"{Session}/artifacts", null)]
    [Arguments("GET", $"{Session}/checkpoints", null)]
    [Arguments("GET", $"{Session}/events", null)]
    [Arguments("GET", $"{Session}/artifacts/22222222-2222-2222-2222-222222222222/content", null)]
    [Arguments("POST", $"{Session}/messages", """{"text":"hello"}""")]
    public async Task WorkSessionRoute_WhenTheSessionIsUnknown_ReturnsBodylessNotFound(string method, string route, string? body)
    {
        var service = Substitute.For<IWorkSessionService>();
        var missing = CreateWorkSessionNotFoundException("gone");
        service.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<UpdateWorkSessionRequestModel>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.StartAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.PauseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.ResumeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.ListTasksAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.ListFindingsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.ListArtifactsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.ListCheckpointsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.GetArtifactAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        service.PostFollowUpAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, method, route, body);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 for an unknown session.");
        AssertEx.Null(response.Content.Headers.ContentType, $"{method} {route} must not attach a content type to a bodyless 404.");
        AssertEx.Equal(string.Empty,
            await response.Content.ReadAsStringAsync(),
            $"{method} {route} must preserve the existing bodyless 404 contract.");
    }

    [Test]
    public async Task UpdateWorkSession_WithOnlyATitle_ForwardsTheOtherMembersAsUnchanged()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.UpdateAsync(SessionId, Arg.Any<UpdateWorkSessionRequestModel>(), Arg.Any<CancellationToken>()).Returns(Detail());
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "PATCH", Session, """{"title":"renamed"}""");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await service.Received(1)
                     .UpdateAsync(SessionId,
                         Arg.Is<UpdateWorkSessionRequestModel>(model => model.Title == "renamed"
                                                                        && model.Objective == null
                                                                        && model.AgentDefinitionId == null),
                         Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWorkSession_WhenTheStatusForbidsIt_ReturnsConflictProblemDetails()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.StartAsync(SessionId, Arg.Any<CancellationToken>())
               .ThrowsAsyncForAnyArgs(new WorkSessionInvalidTransitionException("A work session in Running cannot be started from here."));
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "POST", $"{Session}/start");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("WorkSessionInvalidTransition", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    public async Task UpdateWorkSession_WhenTheWriteLosesARace_ReturnsVersionConflict()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.UpdateAsync(SessionId, Arg.Any<UpdateWorkSessionRequestModel>(), Arg.Any<CancellationToken>())
               .ThrowsAsyncForAnyArgs(new WorkSessionConcurrencyException("The work session moved on."));
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "PATCH", Session, """{"title":"renamed"}""");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("WorkSessionVersionConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    public async Task CreateWorkSession_WhenTheServiceRefusesTheAgent_ReturnsBadRequestInTheGeneralErrorsShape()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.CreateAsync(Arg.Any<CreateWorkSessionRequestModel>(), Arg.Any<CancellationToken>())
               .ThrowsAsyncForAnyArgs(new WorkSessionValidationException("That agent's model cannot call tools."));
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory,
                "POST",
                Root,
                """{"title":"t","objective":"o","kind":"Research","agentDefinitionId":"33333333-3333-3333-3333-333333333333"}""");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "generalErrors", StringComparison.Ordinal);
        AssertEx.Contains(body, "cannot call tools", StringComparison.Ordinal);
    }

    [Test]
    public async Task ArtifactResponses_NeverCarryTheBlobPath()
    {
        const string ManagedReference = "work-sessions/11111111/22222222.bin";
        var service = SubstituteWithEmptyFeeds();
        service.ListArtifactsAsync(SessionId, 0, Arg.Any<CancellationToken>()).Returns([Artifact()]);
        service.GetArtifactAsync(SessionId, ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact());
        service.ReadArtifactContentAsync(SessionId, ArtifactId, Arg.Any<CancellationToken>())
               .Returns(new WorkSessionArtifactContent { Artifact = Artifact(), Content = "# report", IsBase64 = false });
        await using var factory = EnabledFactory(service);

        using var listResponse = await SendAsync(factory, "GET", $"{Session}/artifacts");
        var listJson = await listResponse.Content.ReadAsStringAsync();
        using var contentResponse = await SendAsync(factory, "GET", $"{Session}/artifacts/{ArtifactId}/content");
        var contentJson = await contentResponse.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, contentResponse.StatusCode);
        foreach (var json in new[]
                 {
                     listJson,
                     contentJson
                 })
        {
            AssertEx.False(json.Contains(ManagedReference, StringComparison.Ordinal));
            AssertEx.False(json.Contains("managedReference", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Test]
    public async Task ArtifactContent_WhenTheBytesNoLongerVerify_ReturnsNotFound()
    {
        var service = SubstituteWithEmptyFeeds();
        service.GetArtifactAsync(SessionId, ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact());
        service.ReadArtifactContentAsync(SessionId, ArtifactId, Arg.Any<CancellationToken>())
               .ThrowsAsyncForAnyArgs(CreateWorkSessionNotFoundException("could not be read"));
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/artifacts/{ArtifactId}/content");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The ceiling check reads the one artifact, never the whole feed: a session with thousands of artifacts must
        // not pay a full scan for one content read.
        await service.DidNotReceive().ListArtifactsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ArtifactContent_OverTheNodeCeiling_Returns413AndNeverReadsTheBlob()
    {
        var service = SubstituteWithEmptyFeeds();
        service.GetArtifactAsync(SessionId, ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact(sizeBytes: 4096));
        await using var factory = EnabledFactory(service, ("WorkSessions:MaxArtifactBytes", "1024"));

        using var response = await SendAsync(factory, "GET", $"{Session}/artifacts/{ArtifactId}/content");

        AssertEx.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await service.DidNotReceive().ReadArtifactContentAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ListArtifactsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("text/markdown", false)]
    [Arguments("application/octet-stream", true)]
    public async Task ArtifactContent_ReportsWhetherTheServiceEncodedTheBytes(string mediaType, bool isBase64)
    {
        var service = SubstituteWithEmptyFeeds();
        service.GetArtifactAsync(SessionId, ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact(mediaType: mediaType));
        service.ReadArtifactContentAsync(SessionId, ArtifactId, Arg.Any<CancellationToken>())
               .Returns(new WorkSessionArtifactContent { Artifact = Artifact(mediaType: mediaType), Content = "payload", IsBase64 = isBase64 });
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "GET", $"{Session}/artifacts/{ArtifactId}/content");
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(isBase64, document.RootElement.GetProperty("isBase64").GetBoolean());
    }

    /// <summary>
    ///     The generated SDK declares `body: never` for the four lifecycle verbs, so it posts nothing while the axios
    ///     instance still stamps a JSON content type. A route-only POST must therefore be accepted with no body at all —
    ///     this repo has a standing 415 trap for exactly that shape.
    /// </summary>
    [Test]
    [Arguments("start")]
    [Arguments("pause")]
    [Arguments("resume")]
    [Arguments("cancel")]
    public async Task LifecycleVerb_WithNoRequestBody_IsAcceptedRatherThanUnsupportedMediaType(string verb)
    {
        var service = Substitute.For<IWorkSessionService>();
        service.StartAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Detail());
        service.PauseAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Detail());
        service.ResumeAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Detail());
        service.CancelAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Detail());
        await using var factory = EnabledFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Session}/{verb}");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            $"POST {verb} with no body answered {(int)response.StatusCode}.");
    }

    [Test]
    public async Task PostMessage_ForwardsTheTextVerbatimAndAnswersAccepted()
    {
        const string Text = "  keep going, and mind the whitespace  ";
        var messageId = Guid.NewGuid();
        var service = Substitute.For<IWorkSessionService>();
        service.PostFollowUpAsync(SessionId, Text, Arg.Any<CancellationToken>()).Returns(messageId);
        service.GetAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Detail());
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "POST", $"{Session}/messages", JsonSerializer.Serialize(new
        {
            text = Text
        }));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await service.Received(1).PostFollowUpAsync(SessionId, Text, Arg.Any<CancellationToken>());
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(messageId, document.RootElement.GetProperty("messageId").GetGuid());
        AssertEx.Equal(ConversationId, document.RootElement.GetProperty("conversationId").GetGuid());
    }

    [Test]
    public async Task PostMessage_WhenTheTextIsOverTheNodeCap_ReturnsBadRequest()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.PostFollowUpAsync(SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
               .ThrowsAsyncForAnyArgs(new WorkSessionValidationException("That follow-up is too large (300 KB, limit 256 KB)."));
        await using var factory = EnabledFactory(service);

        using var response = await SendAsync(factory, "POST", $"{Session}/messages", """{"text":"oversized"}""");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    [Arguments("GET", Root)]
    [Arguments("POST", Root)]
    [Arguments("GET", Session)]
    [Arguments("DELETE", Session)]
    [Arguments("POST", $"{Session}/start")]
    [Arguments("GET", $"{Session}/events")]
    [Arguments("GET", $"{Session}/artifacts/22222222-2222-2222-2222-222222222222/content")]
    [Arguments("POST", $"{Session}/messages")]
    public async Task WorkSessionRoute_WhenTheFeatureIsDisabled_ReturnsNotFoundWithoutReachingTheService(string method, string route)
    {
        var service = Substitute.For<IWorkSessionService>();
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["WorkSessions:Enabled"] = "false"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkSessionService>();
                services.AddSingleton(service);
            }
        };

        using var response = await SendAsync(factory, method, route, method == "POST" ? "{}" : null);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 on a disabled node, never 500.");
        AssertEx.Empty(service.ReceivedCalls());
    }

    /// <summary>
    ///     The one route the disabled-node 404 sweep must NOT swallow. Without it the SPA reads the bodyless 404 the
    ///     test above pins as "could not load work sessions", which is indistinguishable from a broken node.
    /// </summary>
    [Test]
    [Arguments("false", false)]
    [Arguments("true", true)]
    public async Task Capability_ReportsTheSwitch_AndStaysReachableWhileTheRestOfTheFamilyIs404(string configured, bool expected)
    {
        var service = Substitute.For<IWorkSessionService>();
        service.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["WorkSessions:Enabled"] = configured
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkSessionService>();
                services.AddSingleton(service);
            }
        };

        using var response = await SendAsync(factory, "GET", $"{Root}/capability");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "The capability GET answers on every node, switch on or off.");
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected, document.RootElement.GetProperty("enabled").GetBoolean());

        // The carve-out is exactly one path: its siblings must still be gone on a disabled node. The literal
        // `capability` segment also has to outrank the `{sessionId}` parameter, which this proves by answering at all.
        using var sibling = await SendAsync(factory, "GET", Root);
        AssertEx.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.NotFound,
            sibling.StatusCode,
            "Only the capability path is exempt from the disabled-node 404 sweep.");
    }

    [Test]
    public async Task Capability_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized()
    {
        await using var factory = EnabledFactory(Substitute.For<IWorkSessionService>());
        using var client = factory.CreateClient();
        using var request = Request("GET", $"{Root}/capability");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "The capability GET is operator-gated like the rest of the family.");
    }

    private static IWorkSessionService SubstituteWithEmptyFeeds()
    {
        var service = Substitute.For<IWorkSessionService>();
        service.ListTasksAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns([]);
        service.ListFindingsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns([]);
        service.ListArtifactsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns([]);
        service.ListCheckpointsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns([]);
        service.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        return service;
    }

    private static WorkSessionDetail Detail() =>
        new()
        {
            Id = SessionId,
            Title = "title",
            Objective = "objective",
            Kind = AgentWorkSessionKind.Research,
            Status = AgentWorkSessionStatus.Draft,
            AgentDefinitionId = AgentId,
            ConversationId = ConversationId,
            CurrentTaskId = null,
            StepCount = 0,
            MaxStepsPerRun = 25,
            LastCheckpointId = null,
            LastSequence = 0,
            Version = 1,
            CreatedUtc = 1,
            UpdatedUtc = 2
        };

    private static WorkSessionTaskDto TaskRow(long sequence) =>
        new()
        {
            Id = Guid.NewGuid(),
            ParentTaskId = null,
            Sequence = sequence,
            Title = "task",
            Detail = null,
            Status = AgentWorkSessionTaskStatus.Planned,
            BlockedReason = null,
            Origin = AgentWorkSessionTaskOrigin.Agent,
            CreatedStep = 1,
            UpdatedStep = 1
        };

    private static WorkSessionEventDto Event(long sequence, Guid? operationId = null) =>
        new() { Id = Guid.NewGuid(), Sequence = sequence, Step = 1, EventType = "step.started", DetailJson = null, Outcome = null, OccurredUtc = 100, OperationId = operationId };

    private static WorkSessionArtifactDto Artifact(bool isValid = true, long sizeBytes = 8, string mediaType = "text/markdown") =>
        new()
        {
            Id = ArtifactId,
            Sequence = 5,
            Kind = AgentWorkSessionArtifactKind.Report,
            Name = "report.md",
            MediaType = mediaType,
            ContentSha256 = "sha",
            SizeBytes = sizeBytes,
            IsValid = isValid,
            CreatedStep = 2
        };

    private static Exception CreateWorkSessionNotFoundException(string message)
    {
        var exceptionType = AssertEx.NotNull(typeof(IAgentWorkSessionStore).Assembly.GetType("XE_Local_AI_Engine.Client.Persistence.Stores.WorkSessionNotFoundException",
                throwOnError: false,
                ignoreCase: false),
            "The persistence layer must expose the typed WorkSessionNotFoundException family before endpoint catches are removed.");
        return AssertEx.NotNull(Activator.CreateInstance(exceptionType, message) as Exception,
            "WorkSessionNotFoundException must expose a public constructor accepting its sanitized message.");
    }

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string method, string route, string? body = null)
    {
        using var client = factory.CreateClient();
        using var request = Request(method, route, body);
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Request(string method, string route, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PATCH")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static TestServerWebAppFactory EnabledFactory(IWorkSessionService service, params (string Key, string? Value)[] configuration)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["WorkSessions:Enabled"] = "true"
        };
        foreach (var (key, value) in configuration)
        {
            settings[key] = value;
        }

        return new TestServerWebAppFactory
        {
            AdditionalConfiguration = settings,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkSessionService>();
                services.AddSingleton(service);
            }
        };
    }
}
