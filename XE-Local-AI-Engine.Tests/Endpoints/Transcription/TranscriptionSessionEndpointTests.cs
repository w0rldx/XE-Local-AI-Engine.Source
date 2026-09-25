namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The session routes that carry no audio: list, create, get, delete, cancel and the transcript-row edit. The transport is what is
///     under test here — the service behind it is stubbed — so the assertions are about status codes, the paging
///     envelope, and who is allowed to call at all.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptionSessionEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task TranscriptionEndpoints_DenyAuthenticatedNonOperator()
    {
        // THIS is the Operator proof. An anonymous 401 is not: swapping Policies(Operator) for a plain [Authorize]
        // keeps every anonymous 401 green while opening all six routes to any signed-in principal.
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        foreach (var (description, create) in Routes())
        {
            using var forbidden = create();
            factory.AddNonOperatorBearerToken(forbidden);
            using var forbiddenResponse = await client.SendAsync(forbidden);
            AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode,
                $"'{description}' must answer 403 for an authenticated non-operator.");

            // The control: without it a route that is broken for everyone would pass the test above.
            using var allowed = create();
            factory.AddNodeBearerToken(allowed);
            using var allowedResponse = await client.SendAsync(allowed);
            AssertEx.True(allowedResponse.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                $"'{description}' must not answer 401/403 for an operator (got {(int)allowedResponse.StatusCode}).");
        }
    }

    [Test]
    public async Task TranscriptionEndpoints_RejectAnonymous()
    {
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        foreach (var (description, create) in Routes())
        {
            using var request = create();
            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"'{description}' must answer 401 without a token.");
        }
    }

    [Test]
    public async Task CreateSession_ThenGet_RoundTrips()
    {
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var createRequest = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions");
        createRequest.Content = JsonContent.Create(new
        {
            title = "interview.wav",
            sourceKind = "File",
            languageMode = "override",
            languageOverride = "de",
            translate = true,
            maxWindowSeconds = 7
        });
        using var createResponse = await client.SendAsync(createRequest);
        AssertEx.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var created = await ReadJsonAsync(createResponse);
        var sessionId = created.GetProperty("session").GetProperty("id").GetGuid();
        AssertEx.Equal("interview.wav", created.GetProperty("session").GetProperty("title").GetString());
        AssertEx.Equal("Created", created.GetProperty("session").GetProperty("status").GetString());

        // The options the caller sent must survive the round trip, or the transcript comes back in the wrong language
        // with nothing on the wire to show why.
        AssertEx.Equal("override", created.GetProperty("config").GetProperty("languageMode").GetString());
        AssertEx.Equal("de", created.GetProperty("config").GetProperty("languageOverride").GetString());
        AssertEx.True(created.GetProperty("config").GetProperty("translate").GetBoolean());
        AssertEx.Equal(expected: 7, created.GetProperty("config").GetProperty("maxWindowSeconds").GetInt32());

        using var getRequest = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/sessions/{sessionId}");
        using var getResponse = await client.SendAsync(getRequest);
        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var fetched = await ReadJsonAsync(getResponse);
        AssertEx.Equal(sessionId, fetched.GetProperty("session").GetProperty("id").GetGuid());
        AssertEx.Equal("interview.wav", fetched.GetProperty("session").GetProperty("title").GetString());

        // A session that has had no audio yet carries an empty transcript rather than a missing member: the client
        // renders the array, and a null here would be a second empty state to handle.
        AssertEx.Equal(expected: 0, fetched.GetProperty("segments").GetArrayLength());
        AssertEx.Equal(expected: 0, fetched.GetProperty("session").GetProperty("segmentCount").GetInt32());
    }

    [Test]
    [Arguments("Telepathy")]
    [Arguments("99")]
    [Arguments("-1")]
    public async Task CreateSession_WithUnknownSourceKind_IsRejected(string sourceKind)
    {
        // The service throws an ArgumentException for an unknown kind, and an unhandled exception is a 500, so the
        // validator has to refuse it first. The NUMERIC cases are the ones a name check exists for: Enum.TryParse
        // parses the underlying values too, so "99" would have been stored as an ordinal no member has and read back
        // on the wire as "99" — a value no client has a case for.
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions");
        request.Content = JsonContent.Create(new
        {
            sourceKind
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, service.CreateCallCount, "The service must never see an unknown source kind.");
    }

    [Test]
    [Arguments(null)]
    [Arguments("e")]
    public async Task CreateSession_WhenOverrideWithoutLanguage_IsRejected(string? languageOverride)
    {
        // An override with nothing usable to override by is degraded to auto downstream. Refusing it is what stops an
        // operator who asked for a specific language getting an auto-detected transcript with no sign of the swap.
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions");
        request.Content = JsonContent.Create(new
        {
            sourceKind = "File",
            languageMode = "override",
            languageOverride
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, service.CreateCallCount);
    }

    [Test]
    [Arguments(1)]
    [Arguments(11)]
    public async Task CreateSession_WhenMaxWindowSecondsOutOfRange_IsRejected(int maxWindowSeconds)
    {
        // The service clamps this silently, so without the refusal a caller asking for a 60-second window would be
        // told it was accepted and then get 10.
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions");
        request.Content = JsonContent.Create(new
        {
            sourceKind = "File",
            maxWindowSeconds
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, service.CreateCallCount);
    }

    [Test]
    public async Task Upload_WhenSessionUnknown_Returns404BeforeReadingTheBody()
    {
        // The refusal has to precede the transfer. Discovering the unknown id only after the whole file is on disk
        // makes the client wait out a pointless upload and makes the node write bytes it was never going to use.
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(new byte[128 * 1024]);
        form.Add(fileContent, "file", "clip.wav");

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}/file");
        request.Content = form;
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Equal(expected: 0, service.BeginUploadCallCount, "No upload slot may be minted for a session that does not exist.");
    }

    [Test]
    public async Task ListSessions_AppliesDefaultPageAndRefusesOutOfRange()
    {
        using var service = new StubTranscriptionService
        {
            TotalCount = 137
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var defaultRequest = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/sessions");
        using var defaultResponse = await client.SendAsync(defaultRequest);
        AssertEx.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);

        var body = await ReadJsonAsync(defaultResponse);
        AssertEx.Equal(expected: 137, body.GetProperty("totalCount").GetInt32());
        AssertEx.Equal(expected: 1, body.GetProperty("items").GetArrayLength());

        // A caller that names no page gets the default one, not "everything" and not "nothing".
        AssertEx.Equal(expected: 50, service.LastLimit);
        AssertEx.Equal(expected: 0, service.LastOffset);

        using var ceilingRequest = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/sessions?limit=200&offset=25");
        using var ceilingResponse = await client.SendAsync(ceilingRequest);
        AssertEx.Equal(HttpStatusCode.OK, ceilingResponse.StatusCode);
        AssertEx.Equal(expected: 200, service.LastLimit);
        AssertEx.Equal(expected: 25, service.LastOffset);

        // Past the ceiling the request is refused rather than silently trimmed: a page the caller did not ask for,
        // returned as though it had, is how a client comes to believe it has seen every row.
        using var oversizeRequest = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/sessions?limit=201");
        using var oversizeResponse = await client.SendAsync(oversizeRequest);
        AssertEx.Equal(HttpStatusCode.BadRequest, oversizeResponse.StatusCode);

        using var negativeRequest = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/sessions?offset=-1");
        using var negativeResponse = await client.SendAsync(negativeRequest);
        AssertEx.Equal(HttpStatusCode.BadRequest, negativeResponse.StatusCode);
    }

    [Test]
    public async Task CancelSession_WithNoBody_IsAccepted()
    {
        // The 415 regression the Description(x => x.Accepts<…>()) override exists to prevent: FastEndpoints otherwise
        // declares application/json only, and a route-only POST carries no body at all.
        using var service = new StubTranscriptionService
        {
            CancelResult = true
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}/cancel");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertEx.Equal(expected: 1, service.CancelCallCount);
    }

    [Test]
    public async Task CancelSession_WhenNothingIsRunning_ReturnsNotFound()
    {
        using var service = new StubTranscriptionService
        {
            CancelResult = false
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}/cancel");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task DeleteSession_WhenUnknown_ReturnsNotFound()
    {
        using var service = new StubTranscriptionService
        {
            DeleteResult = false
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task DeleteSession_WhenKnown_ReturnsNoContent()
    {
        using var service = new StubTranscriptionService
        {
            DeleteResult = true
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Test]
    public async Task GetSession_WhenUnknown_ReturnsNotFound()
    {
        using var service = new StubTranscriptionService
        {
            GetReturnsNull = true
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task UpdateSegment_WhenUpdated_ReturnsTheRowAndHandsTheServiceTrimmedText()
    {
        var sessionId = Guid.NewGuid();
        using var service = new StubTranscriptionService
        {
            UpdateSegmentResult = new UpdateTranscriptSegmentResult
            {
                Outcome = UpdateTranscriptSegmentOutcome.Updated,
                Segment = new TranscriptSegmentView
                {
                    Id = Guid.NewGuid(),
                    Seq = 3,
                    StartMs = 2_000,
                    EndMs = 3_000,
                    Text = "corrected",
                    Channel = TranscriptChannel.You
                }
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var response = await PutSegmentAsync(factory, client, sessionId, seq: 3, "  corrected  ");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await ReadJsonAsync(response);
        AssertEx.Equal(expected: 3L, row.GetProperty("seq").GetInt64());
        AssertEx.Equal("corrected", row.GetProperty("text").GetString());
        AssertEx.Equal("You", row.GetProperty("channel").GetString());
        AssertEx.Equal("corrected", service.LastUpdatedText, "The endpoint stores the trimmed text, the same text the validator measured.");
        AssertEx.Equal(expected: 3L, service.LastUpdatedSeq);
    }

    [Test]
    [Arguments(UpdateTranscriptSegmentOutcome.SessionNotFound)]
    [Arguments(UpdateTranscriptSegmentOutcome.SegmentNotFound)]
    public async Task UpdateSegment_WhenSessionOrRowUnknown_ReturnsNotFound(UpdateTranscriptSegmentOutcome outcome)
    {
        using var service = new StubTranscriptionService
        {
            UpdateSegmentResult = new UpdateTranscriptSegmentResult
            {
                Outcome = outcome
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var response = await PutSegmentAsync(factory, client, Guid.NewGuid(), seq: 1, "corrected");

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task UpdateSegment_WhileTranscribing_ReturnsConflictWithReasonCode()
    {
        using var service = new StubTranscriptionService
        {
            UpdateSegmentResult = new UpdateTranscriptSegmentResult
            {
                Outcome = UpdateTranscriptSegmentOutcome.SessionTranscribing
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var response = await PutSegmentAsync(factory, client, Guid.NewGuid(), seq: 1, "corrected");

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertEx.Equal("session-transcribing", body.GetProperty("reason").GetString(), "The SPA branches on the code, never on the prose.");
        AssertEx.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task UpdateSegment_WithBlankText_IsRejectedBeforeTheService(string? text)
    {
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var response = await PutSegmentAsync(factory, client, Guid.NewGuid(), seq: 1, text);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, service.UpdateSegmentCallCount);
    }

    [Test]
    public async Task UpdateSegment_WithTextOverTheLimit_IsRejected_ButTheLimitItselfPasses()
    {
        using var service = new StubTranscriptionService
        {
            UpdateSegmentResult = new UpdateTranscriptSegmentResult
            {
                Outcome = UpdateTranscriptSegmentOutcome.SegmentNotFound
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        // Padding around the limit proves the length is measured after trimming.
        using var atLimit = await PutSegmentAsync(factory, client, Guid.NewGuid(), seq: 1, $"  {new string('a', 8000)}  ");
        using var overLimit = await PutSegmentAsync(factory, client, Guid.NewGuid(), seq: 1, new string('a', 8001));

        AssertEx.Equal(HttpStatusCode.NotFound, atLimit.StatusCode, "8000 characters after trimming reaches the service.");
        AssertEx.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        AssertEx.Equal(expected: 1, service.UpdateSegmentCallCount);
    }

    [Test]
    public async Task UpdateSegment_WithNonPositiveSeq_IsRejected()
    {
        using var service = new StubTranscriptionService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var response = await PutSegmentAsync(factory, client, Guid.NewGuid(), seq: 0, "corrected");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Sequences ascend from one; zero is never a row.");
        AssertEx.Equal(expected: 0, service.UpdateSegmentCallCount);
    }

    private static async Task<HttpResponseMessage> PutSegmentAsync(TestServerWebAppFactory factory, HttpClient client, Guid sessionId, long seq, string? text)
    {
        using var request = Authorized(factory, HttpMethod.Put, $"{ApiPrefix}/transcription/sessions/{sessionId}/segments/{seq}");
        request.Content = JsonContent.Create(new
        {
            text
        });
        return await client.SendAsync(request);
    }

    /// <summary>
    ///     Every route the slice adds, as request factories — a <see cref="HttpRequestMessage" /> cannot be sent twice,
    ///     and the authorization tests send each route under two different principals.
    /// </summary>
    private static IReadOnlyList<(string Description, Func<HttpRequestMessage> Create)> Routes()
    {
        var sessionId = Guid.NewGuid();
        return
        [
            ("GET transcription/sessions", () => new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/transcription/sessions")),
            ("POST transcription/sessions", () => new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/transcription/sessions")
            {
                Content = JsonContent.Create(new
                {
                    sourceKind = "File"
                })
            }),
            ("GET transcription/sessions/{id}", () => new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/transcription/sessions/{sessionId}")),
            ("DELETE transcription/sessions/{id}", () => new HttpRequestMessage(HttpMethod.Delete, $"{ApiPrefix}/transcription/sessions/{sessionId}")),
            ("POST transcription/sessions/{id}/cancel", () => new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/cancel")),
            ("PUT transcription/sessions/{id}/segments/{seq}", () => new HttpRequestMessage(HttpMethod.Put, $"{ApiPrefix}/transcription/sessions/{sessionId}/segments/1")
            {
                Content = JsonContent.Create(new
                {
                    text = "corrected"
                })
            }),

            // An empty multipart body, so the operator control reaches the handler's own "a file is required" answer
            // rather than a content-type rejection that would prove nothing about authorization.
            ("POST transcription/sessions/{id}/file", () => new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/file")
            {
                Content = new MultipartFormDataContent()
            })
        ];
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
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
