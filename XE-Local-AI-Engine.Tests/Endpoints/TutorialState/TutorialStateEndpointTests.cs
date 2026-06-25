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
///         <item>PUT is Operator-gated — an unauthenticated request is rejected.</item>
///     </list>
/// </summary>
public sealed class TutorialStateEndpointTests
{
    private const string Route = "/api/local/v1/tutorial-state";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task PutThenGet_RoundTripsEntry_AndMergePreservesOtherKeys()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory).ConfigureAwait(false);

        // Upsert the first tour key.
        await SaveAsync(factory, client, key: "main-app-v1", status: "completed").ConfigureAwait(false);
        // Upsert a SECOND, distinct key — merge must keep the first one rather than replacing the whole array.
        await SaveAsync(factory, client, key: "settings-flow-v1", status: "skipped").ConfigureAwait(false);

        var state = await GetAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, state.Entries.Count);

        var mainApp = AssertEx.NotNull(state.Entries.SingleOrDefault(entry => entry.Key == "main-app-v1"));
        AssertEx.Equal("completed", mainApp.Status);

        var settingsFlow = AssertEx.NotNull(state.Entries.SingleOrDefault(entry => entry.Key == "settings-flow-v1"));
        AssertEx.Equal("skipped", settingsFlow.Status);
    }

    [Test]
    public async Task Put_ReUpsertingSameKey_ReplacesThatEntryInPlace()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        await SeedAdminUserAsync(factory).ConfigureAwait(false);

        await SaveAsync(factory, client, key: "main-app-v1", status: "skipped").ConfigureAwait(false);
        await SaveAsync(factory, client, key: "main-app-v1", status: "completed").ConfigureAwait(false);

        var state = await GetAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, state.Entries.Count);
        AssertEx.Equal("completed", state.Entries[0].Status);
    }

    [Test]
    public async Task Put_WhenUnauthenticated_IsRejected()
    {
        await using var factory = new TestingWebAppFactory();
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

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The node bearer token is minted for the single-admin user id "node-admin-test" (see
    // TestingWebAppFactory.CreateNodeAccessToken). The tutorial-state service resolves that user via UserManager, so the
    // Identity row must exist for an authenticated write to persist — seed it to match the token exactly.
    private static async Task SeedAdminUserAsync(TestingWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();

        var existing = await userManager.FindByIdAsync("node-admin-test").ConfigureAwait(false);
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

        var result = await userManager.CreateAsync(user).ConfigureAwait(false);
        AssertEx.True(result.Succeeded);
    }

    private static async Task SaveAsync(TestingWebAppFactory factory, HttpClient client, string key, string status)
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

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<TutorialStateResponseDto> GetAsync(TestingWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<TutorialStateResponseDto>(stream, JsonOptions).ConfigureAwait(false));
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
