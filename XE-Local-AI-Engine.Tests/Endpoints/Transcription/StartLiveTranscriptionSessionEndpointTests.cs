namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The live-start route: who may call it, and how each outcome of
///     <see cref="ITranscriptionService.StartLiveAsync" /> reaches the wire. The service is stubbed — what is under
///     test is the transport, the status codes and the body a client branches on.
/// </summary>
public sealed class StartLiveTranscriptionSessionEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Post_WithAnAuthenticatedNonOperator_IsForbidden()
    {
        // THIS is the Operator proof. An anonymous 401 is not: swapping Policies(Operator) for a plain [Authorize]
        // keeps the anonymous case green while opening the route to any signed-in principal.
        using var service = new StubTranscriptionService();
        var sessionId = service.SeedSession();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var forbidden = Request(sessionId);
        factory.AddNonOperatorBearerToken(forbidden);
        using var forbiddenResponse = await client.SendAsync(forbidden).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        // The control: without it a route broken for everyone would pass the assertion above.
        using var allowed = Request(sessionId);
        factory.AddNodeBearerToken(allowed);
        using var allowedResponse = await client.SendAsync(allowed).ConfigureAwait(false);
        AssertEx.True(allowedResponse.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
            $"an operator must not be refused (got {(int)allowedResponse.StatusCode}).");
    }

    [Test]
    public async Task Post_WithoutAToken_IsUnauthorized()
    {
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Request(Guid.NewGuid());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Post_WithNoBody_StartsTheSessionAndReturnsItsWatermark()
    {
        // The 415 regression the Description(x => x.Accepts<…>()) override exists to prevent: FastEndpoints otherwise
        // declares application/json only, and this is a route-only POST with no body at all.
        using var service = new StubTranscriptionService
        {
            StartLiveResult = new StartLiveResult
            {
                Outcome = StartLiveOutcome.Started,
                Status = TranscriptionSessionStatus.Transcribing,
                LastSeq = 4
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        using var request = Authorized(factory, sessionId);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response).ConfigureAwait(false);
        AssertEx.Equal(sessionId, body.GetProperty("sessionId").GetGuid());
        AssertEx.Equal("Transcribing", body.GetProperty("status").GetString());
        AssertEx.Equal(expected: 4L, body.GetProperty("lastSeq").GetInt64(), "the client subscribes from the sequence already persisted.");
        AssertEx.Equal(expected: 1, service.StartLiveCallCount);
        AssertEx.Equal(sessionId, service.LastStartLiveSessionId, "the id comes from the route, not from a body.");
    }

    [Test]
    public async Task Post_Twice_IsIdempotent()
    {
        // A double-click, a retried fetch or a reconnect that re-issues the start is not an error; it answers with the
        // state the session is already in.
        using var service = new StubTranscriptionService
        {
            StartLiveResult = new StartLiveResult
            {
                Outcome = StartLiveOutcome.AlreadyLive,
                Status = TranscriptionSessionStatus.Transcribing,
                LastSeq = 9
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();

        using var first = Authorized(factory, sessionId);
        using var firstResponse = await client.SendAsync(first).ConfigureAwait(false);
        using var second = Authorized(factory, sessionId);
        using var secondResponse = await client.SendAsync(second).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        AssertEx.Equal(await firstResponse.Content.ReadAsStringAsync().ConfigureAwait(false),
            await secondResponse.Content.ReadAsStringAsync().ConfigureAwait(false),
            "the second start reports the same state as the first.");
    }

    [Test]
    public async Task Post_WhenTheSessionIsUnknown_ReturnsNotFound()
    {
        using var service = new StubTranscriptionService
        {
            StartLiveResult = new StartLiveResult
            {
                Outcome = StartLiveOutcome.SessionNotFound
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Guid.NewGuid());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Post_WhenTheSessionHasAlreadyFinished_ReturnsConflictCarryingTheTerminalStatus()
    {
        // A bare 409 would leave the client unable to say whether the session completed or was cancelled underneath it.
        using var service = new StubTranscriptionService
        {
            StartLiveResult = new StartLiveResult
            {
                Outcome = StartLiveOutcome.SessionAlreadyFinished,
                Status = TranscriptionSessionStatus.Cancelled,
                LastSeq = 12
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Guid.NewGuid());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response).ConfigureAwait(false);
        AssertEx.Equal("Cancelled", body.GetProperty("status").GetString());
        AssertEx.Equal(expected: 12L, body.GetProperty("lastSeq").GetInt64());
    }

    [Test]
    public async Task Post_ForASourceKindWithNoLivePath_ReturnsBadRequest()
    {
        // A File session can never be captured live, at any status. A 500 from an unhandled exception would invite a
        // retry that could never work, and a 404 would send the client looking for a session that exists.
        using var service = new StubTranscriptionService
        {
            StartLiveThrows = new LiveTranscriptionSourceKindException(TranscriptionSourceKind.File)
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, Guid.NewGuid());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpRequestMessage Request(Guid sessionId) =>
        new(HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/live/start");

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, Guid sessionId)
    {
        var request = Request(sessionId);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
    }

    private static TestServerWebAppFactory FactoryWith(ITranscriptionService service) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITranscriptionService>();
                services.AddSingleton(service);
            }
        };
}
