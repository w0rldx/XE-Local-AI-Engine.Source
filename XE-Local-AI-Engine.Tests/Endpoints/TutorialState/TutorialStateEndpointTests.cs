namespace XE_Local_AI_Engine.Tests.Endpoints.TutorialState;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Contract tests for the per-user onboarding tour-state endpoints:
///     <list type="bullet">
///         <item>PUT then GET round-trips an upserted entry, and a second key is preserved on merge (upsert one key
///         does not drop others);</item>
///         <item>completed is monotonic, and concurrent tutorial writes preserve every distinct key;</item>
///         <item>PUT is Operator-gated — an unauthenticated request is rejected.</item>
///     </list>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TutorialStateEndpointTests
{
    private const string Route = "/api/local/v1/tutorial-state";

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
