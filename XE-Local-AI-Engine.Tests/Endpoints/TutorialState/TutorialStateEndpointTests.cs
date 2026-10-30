namespace XE_Local_AI_Engine.Tests.Endpoints.TutorialState;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Tutorial;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Contract tests for the per-user onboarding tour-state endpoints:
///     <list type="bullet">
///         <item>PUT then GET round-trips an upserted entry, and a second key is preserved on merge (upsert one key
///         does not drop others);</item>
///         <item>completed is monotonic, and concurrent tutorial writes preserve every distinct key;</item>
///         <item>PUT is Operator-gated — an unauthenticated request is rejected;</item>
///         <item>the AI acknowledgement reads false until POSTed, and a repeated POST keeps the first timestamp.</item>
///     </list>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TutorialStateEndpointTests
{
    private const string Route = "/api/local/v1/tutorial-state";
    private const string AiAcknowledgementRoute = "/api/local/v1/tutorial-state/ai-acknowledgement";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task PutThenGet_RoundTripsEntry_AndMergePreservesOtherKeys()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory);

        // Upsert the first tour key.
        await SaveAsync(factory, client, key: "main-app-v1", status: "completed");
        // Upsert a SECOND, distinct key — merge must keep the first one rather than replacing the whole array.
        await SaveAsync(factory, client, key: "settings-flow-v1", status: "skipped");

        var state = await GetAsync(factory, client);

        AssertEx.Equal(expected: 2, state.Entries.Count);

        var mainApp = AssertEx.NotNull(state.Entries.SingleOrDefault(entry => entry.Key == "main-app-v1"));
        AssertEx.Equal("completed", mainApp.Status);

        var settingsFlow = AssertEx.NotNull(state.Entries.SingleOrDefault(entry => entry.Key == "settings-flow-v1"));
        AssertEx.Equal("skipped", settingsFlow.Status);
    }

    [Test]
    public async Task Put_ReUpsertingSameKey_ReplacesThatEntryInPlace()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory);

        await SaveAsync(factory, client, key: "main-app-v1", status: "skipped");
        await SaveAsync(factory, client, key: "main-app-v1", status: "completed");

        var state = await GetAsync(factory, client);

        AssertEx.Equal(expected: 1, state.Entries.Count);
        AssertEx.Equal("completed", state.Entries[0].Status);
    }

    [Test]
    public async Task Put_SkippedAfterCompleted_PreservesCompleted()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory);

        await SaveAsync(factory, client, key: "main-app-v1", status: "completed");
        await SaveAsync(factory, client, key: "main-app-v1", status: "skipped");

        var state = await GetAsync(factory, client);

        AssertEx.Equal(expected: 1, state.Entries.Count);
        AssertEx.Equal("completed", state.Entries[0].Status);
    }

    [Test]
    public async Task Put_ConcurrentDistinctKeys_PreservesEveryEntry()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory);

        const int entryCount = 8;
        await Parallel.ForEachAsync(Enumerable.Range(start: 0, entryCount), async (index, _) =>
        {
            await SaveAsync(factory, client, key: $"tutorial-{index}", status: "completed");
        });

        var state = await GetAsync(factory, client);

        AssertEx.Equal(entryCount, state.Entries.Count);
        for (var index = 0; index < entryCount; index++)
        {
            AssertEx.True(state.Entries.Any(entry => entry.Key == $"tutorial-{index}" && entry.Status == "completed"));
        }
    }

    /// <summary>
    ///     Pins the request-shape refusals: both keys, both messages, and the order they arrive in.
    /// </summary>
    /// <remarks>
    ///     The key and status checks report TOGETHER rather than stopping at the first, and the key's own two rules
    ///     (required, then length) stop at the first. That is the exact shape the move into a
    ///     <c>Validator&lt;SaveTutorialStateRequest&gt;</c> had to preserve, so these ran green on the hand-written
    ///     handler checks before the move.
    /// </remarks>
    [Test]
    public async Task Put_WhenKeyIsBlankAndStatusIsUnknown_ReportsBothErrorsKeyedAndInOrder()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory);

        var errors = await PostInvalidAsync(factory, client, key: "   ", status: "paused");

        AssertEx.Equal(expected: 2, errors.GetArrayLength());
        AssertEx.Equal("key", errors[0].GetProperty("name").GetString());
        AssertEx.Equal("Key is required.", errors[0].GetProperty("reason").GetString());
        AssertEx.Equal("status", errors[1].GetProperty("name").GetString());
        AssertEx.Equal("Status must be 'completed' or 'skipped'.", errors[1].GetProperty("reason").GetString());
    }

    [Test]
    public async Task Put_WhenKeyIsTooLong_ReportsTheLengthErrorAndNotTheRequiredError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory);

        var errors = await PostInvalidAsync(factory, client, key: new string('k', count: 129), status: "completed");

        AssertEx.Equal(expected: 1, errors.GetArrayLength());
        AssertEx.Equal("key", errors[0].GetProperty("name").GetString());
        AssertEx.Equal("Key must be 128 characters or fewer.", errors[0].GetProperty("reason").GetString());
    }

    [Test]
    public async Task Put_WhenUnauthenticated_IsRejected()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        // No bearer token added — the Operator policy must reject the request.
        using var request = new HttpRequestMessage(HttpMethod.Put, Route)
        {
            Content = JsonContent.Create(new
            {
                key = "main-app-v1",
                status = "completed"
            })
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task AiAcknowledgement_WhenUnauthenticated_IsRejected()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        foreach (var method in new[]
                 {
                     HttpMethod.Get,
                     HttpMethod.Post
                 })
        {
            using var request = new HttpRequestMessage(method, AiAcknowledgementRoute);
            request.Headers.Add("Origin", "http://localhost");

            using var response = await client.SendAsync(request);

            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} must require a signed-in user.");
        }
    }

    [Test]
    public async Task AiAcknowledgement_ReadsFalseUntilPosted_AndARepeatKeepsTheFirstTimestamp()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory);

        var before = await SendAiAcknowledgementAsync(factory, client, HttpMethod.Get);
        AssertEx.False(before.GetProperty("acknowledged").GetBoolean(), "A user who never acknowledged must see the notice.");
        AssertEx.Null(AcknowledgedAt(before));

        var first = await SendAiAcknowledgementAsync(factory, client, HttpMethod.Post);
        AssertEx.True(first.GetProperty("acknowledged").GetBoolean());
        var firstAt = AcknowledgedAt(first) ?? string.Empty;
        AssertEx.NotNullOrEmpty(firstAt, "An acknowledgement must carry its timestamp.");

        var repeat = await SendAiAcknowledgementAsync(factory, client, HttpMethod.Post);
        AssertEx.Equal(firstAt, AcknowledgedAt(repeat), "A repeated acknowledgement must keep the first record.");

        var after = await SendAiAcknowledgementAsync(factory, client, HttpMethod.Get);
        AssertEx.True(after.GetProperty("acknowledged").GetBoolean());
        AssertEx.Equal(firstAt, AcknowledgedAt(after));
    }

    [Test]
    public async Task Writes_ThatIdentityRefuses_Answer409_NotAnAuthenticationFailure()
    {
        // The real service and the real Identity pipeline; only the user validator is told to refuse once seeding is done.
        var refusing = new RefusingNodeUserValidator();
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services => services.AddSingleton<IUserValidator<NodeUser>>(refusing)
        };
        using var client = factory.CreateClient();
        await SeedAdminUserAsync(factory);
        refusing.Refuse = true;

        using (var save = await SendWriteAsync(factory, client, HttpMethod.Put, Route, JsonContent.Create(new
               {
                   key = "main-app-v1",
                   status = "completed"
               })))
        {
            AssertEx.Equal(HttpStatusCode.Conflict, save.StatusCode, "a tour write Identity refused is not the caller's authentication failing.");
            AssertEx.Contains(await save.Content.ReadAsStringAsync(), "could not be saved");
        }

        using var acknowledge = await SendWriteAsync(factory, client, HttpMethod.Post, AiAcknowledgementRoute, content: null);
        AssertEx.Equal(HttpStatusCode.Conflict, acknowledge.StatusCode, "an acknowledgement Identity refused is not the caller's authentication failing.");
        AssertEx.Contains(await acknowledge.Content.ReadAsStringAsync(), "could not be saved");
    }

    [Test]
    public async Task Writes_ForAPrincipalWithNoNodeUser_Answer401()
    {
        // Substituted: a bearer token for a user Identity cannot find fails authentication before the endpoint runs, so
        // the real pipeline never reaches the endpoint's own unresolved-user branch.
        var service = Substitute.For<INodeTutorialStateService>();
        service.SaveEntryAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<TutorialStatus>(), Arg.Any<CancellationToken>())
               .Returns(TutorialEntrySaveOutcome.UserNotResolved);
        service.AcknowledgeAiAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new AiAcknowledgementResult.UserNotResolved());
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeTutorialStateService>();
                services.AddSingleton(service);
            }
        };
        using var client = factory.CreateClient();

        using (var save = await SendWriteAsync(factory, client, HttpMethod.Put, Route, JsonContent.Create(new
               {
                   key = "main-app-v1",
                   status = "completed"
               })))
        {
            AssertEx.Equal(HttpStatusCode.Unauthorized, save.StatusCode);
        }

        using var acknowledge = await SendWriteAsync(factory, client, HttpMethod.Post, AiAcknowledgementRoute, content: null);
        AssertEx.Equal(HttpStatusCode.Unauthorized, acknowledge.StatusCode);
    }

    // The node bearer token is minted for the single-admin user id "node-admin-test" (see
    // TestServerWebAppFactory.CreateNodeAccessToken). The tutorial-state service resolves that user via UserManager, so the
    // Identity row must exist for an authenticated write to persist — seed it to match the token exactly.
    private static async Task SeedAdminUserAsync(TestServerWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();

        var existing = await userManager.FindByIdAsync("node-admin-test");
        if (existing is not null)
        {
            return;
        }

        var user = new NodeUser
        {
            Id = "node-admin-test",
            UserName = "admin@example.test",
            Email = "admin@example.test",
            SetupCompleted = true
        };

        var result = await userManager.CreateAsync(user);
        AssertEx.True(result.Succeeded);

        // CreateAsync rotates the security stamp to a random value; pin it to the fixed stamp the synthetic bearer token
        // carries (TestServerWebAppFactory.CreateNodeAccessToken) so the JWT validator's fail-closed stamp check matches.
        user.SecurityStamp = TestServerWebAppFactory.NodeAdminTestSecurityStamp;
        var stampResult = await userManager.UpdateAsync(user);
        AssertEx.True(stampResult.Succeeded);
    }

    private static async Task SaveAsync(TestServerWebAppFactory factory, HttpClient client, string key, string status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Route)
        {
            Content = JsonContent.Create(new
            {
                key,
                status
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonElement> PostInvalidAsync(TestServerWebAppFactory factory,
        HttpClient client,
        string key,
        string status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Route)
        {
            Content = JsonContent.Create(new
            {
                key,
                status
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("errors").Clone();
    }

    private static async Task<TutorialStateResponseDto> GetAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<TutorialStateResponseDto>(stream, JsonOptions));
    }

    private static async Task<JsonElement> SendAiAcknowledgementAsync(TestServerWebAppFactory factory,
        HttpClient client,
        HttpMethod method)
    {
        using var request = new HttpRequestMessage(method, AiAcknowledgementRoute);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendWriteAsync(TestServerWebAppFactory factory,
        HttpClient client,
        HttpMethod method,
        string route,
        HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, route)
        {
            Content = content
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return await client.SendAsync(request);
    }

    private static string? AcknowledgedAt(JsonElement acknowledgement)
    {
        return acknowledgement.TryGetProperty("acknowledgedAtUtc", out var value) ? value.GetString() : null;
    }

    // Local wire shapes for deserialization — the endpoint DTOs are internal to the Client project's V1 namespace, so
    // the test mirrors the JSON contract (key/status/atUtc) rather than referencing those types.
    private sealed record TutorialStateResponseDto
    {
        public IReadOnlyList<TutorialStateEntryDto> Entries { get; init; } = [];
    }

    private sealed record TutorialStateEntryDto
    {
        public string Key { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }
}
