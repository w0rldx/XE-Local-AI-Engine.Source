namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three hand-mapped external routes as an integrator actually reaches them: inside <c>/api/local/v1</c>, on
///     the integration key scheme ONLY, with no 403 reachable anywhere, and with the round-5 authorisation rule
///     applied at the route — a key scoped to one trigger cannot read OR cancel its own principal's executions under
///     another.
/// </summary>
public sealed class IntegrationApiRouteTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Invoke_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, IntegrationApiRoutes.Invoke("anything"), key: null);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "The routes live under the loopback-gated prefix and accept only the integration key scheme.");
    }

    [Test]
    public async Task Invoke_WhenPresentedTheOperatorJwt_Returns401()
    {
        using var client = Factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, IntegrationApiRoutes.Invoke("anything"));
        Factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "The IntegrationApi policy accepts the key scheme alone — never the operator's JWT.");
    }

    [Test]
    public async Task Invoke_WhenTheKeyIsRevoked_Returns401IndistinguishableFromGarbage()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "revoked-probe");

        using var revoke = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"{IntegrationEndpointPayloads.KeysRoute}/{seeded.BroadKeyId}");
        AssertEx.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var revoked = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Execution(seeded.ExecutionUnderB), seeded.BroadKey);
        using var garbage = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Execution(seeded.ExecutionUnderB), "xeint_notarealkey");

        AssertEx.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode, "A revoked credential is the same 401 as an unknown one: telling them apart confirms the key was real.");
        AssertEx.Equal(garbage.StatusCode, revoked.StatusCode);
        AssertEx.Equal(await garbage.Content.ReadAsStringAsync(), await revoked.Content.ReadAsStringAsync(), "The two 401 bodies must be byte-identical.");
    }

    [Test]
    public async Task GetExecution_WhenTheKeyIsBroad_ReturnsTheRowWithItsOutputCount()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "broad-read");

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Execution(seeded.ExecutionUnderB), seeded.BroadKey);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<StatusBody>(IntegrationEndpointPayloads.Json));
        AssertEx.Equal(seeded.ExecutionUnderB, body.ExecutionId);
        AssertEx.Equal("accepted", body.Status);
        // The transactional counter on the row, never a buffer read: the buffer is evictable and a restarted node would
        // report zero for a run that did emit.
        AssertEx.Equal(expected: 0, body.OutputCount, "outputCount reads 0 until the output tool ships, which is the true answer rather than a placeholder.");
    }

    [Test]
    public async Task GetExecution_WhenTheKeyIsNarrow_Returns404ByteIdenticalToAnUnknownId()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "narrow-read");

        using var masked = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Execution(seeded.ExecutionUnderB), seeded.NarrowKey);
        using var unknown = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Execution(Guid.NewGuid()), seeded.NarrowKey);

        AssertEx.Equal(HttpStatusCode.NotFound, masked.StatusCode,
            "The allowlist bounds which triggers a key may FIRE and must keep binding afterwards, or scoping a key buys nothing.");
        AssertEx.Equal(await unknown.Content.ReadAsStringAsync(), await masked.Content.ReadAsStringAsync(), "An out-of-scope row must not be distinguishable from one that does not exist.");
    }

    [Test]
    public async Task GetExecution_WhenAnotherPrincipalAsks_Returns404()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "foreign-read");

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Execution(seeded.ExecutionUnderB), seeded.ForeignKey);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "The id is the capability, so another integrator's execution must not be confirmable.");
    }

    [Test]
    public async Task CancelExecution_WhenTheKeyIsNarrow_Returns404AndLeavesTheRowUntouched()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "narrow-cancel");

        using var response = await SendAsync(client, HttpMethod.Post, IntegrationApiRoutes.Cancel(seeded.ExecutionUnderB), seeded.NarrowKey);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var row = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>().GetByIdAsync(seeded.ExecutionUnderB));
        AssertEx.Null(row.StopRequestedAtUtc, "A masked cancel must have no side effect at all — the check runs before the marker is stamped.");
    }

    [Test]
    public async Task CancelExecution_WhenTheKeyIsBroad_Returns202AndStampsTheDurableMarker()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "broad-cancel");

        using var response = await SendAsync(client, HttpMethod.Post, IntegrationApiRoutes.Cancel(seeded.ExecutionUnderB), seeded.BroadKey);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var row = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>().GetByIdAsync(seeded.ExecutionUnderB));
        AssertEx.True(row.StopRequestedAtUtc is not null, "The stop marker is durable so a restart cannot resurrect the run.");
    }

    [Test]
    public async Task GetSession_WhenTheKeyIsBroad_ReportsTheSessionByItsTriggerName()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "session-read");

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Session(seeded.SessionUnderB), seeded.BroadKey);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<SessionStatusBody>(IntegrationEndpointPayloads.Json));
        AssertEx.Equal(seeded.SessionUnderB, body.SessionId);
        AssertEx.Equal(seeded.TriggerBName, body.TriggerName, "An integrator addresses the trigger by NAME, so the body carries it rather than an id.");
        AssertEx.Equal("active", body.Status);
        AssertEx.Equal(expected: 1, body.ExecutionCount);
    }

    [Test]
    public async Task GetSession_ForANarrowKeyAForeignPrincipalAndAnUnknownId_AreOneByteIdentical404()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "session-mask");

        using var unknown = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Session(Guid.NewGuid()), seeded.BroadKey);
        using var narrow = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Session(seeded.SessionUnderB), seeded.NarrowKey);
        using var foreign = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Session(seeded.SessionUnderB), seeded.ForeignKey);

        AssertEx.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertEx.Equal(HttpStatusCode.NotFound, narrow.StatusCode, "A key scoped to another trigger must not read this session, or scoping the key buys nothing.");
        AssertEx.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var body = await unknown.Content.ReadAsStringAsync();
        AssertEx.Equal(body, await narrow.Content.ReadAsStringAsync(), "Three masked cases, one body: a distinguishable answer re-opens the enumeration oracle.");
        AssertEx.Equal(body, await foreign.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task GetSession_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Session(Guid.NewGuid()), key: null);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ExternalFamily_NeverProducesA403()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "no-403");
        var routes = new[]
        {
            (HttpMethod.Post, IntegrationApiRoutes.Invoke(seeded.TriggerBName)),
            (HttpMethod.Get, IntegrationApiRoutes.Execution(seeded.ExecutionUnderB)),
            (HttpMethod.Post, IntegrationApiRoutes.Cancel(seeded.ExecutionUnderB)),
            (HttpMethod.Get, IntegrationApiRoutes.Session(seeded.SessionUnderB))
        };
        var credentials = new[]
        {
            null,
            "xeint_notarealkey",
            seeded.NarrowKey,
            seeded.ForeignKey
        };

        foreach (var (method, route) in routes)
        {
            foreach (var credential in credentials)
            {
                using var response = await SendAsync(client, method, route, credential);
                AssertEx.NotEqual(HttpStatusCode.Forbidden,
                    response.StatusCode,
                    $"{method} {route} with credential '{credential ?? "none"}' produced a 403; this family has none by contract.");
            }
        }
    }

    private async Task<Seeded> SeedAsync(HttpClient client, string prefix)
    {
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, $"{prefix}-agent");
        var triggerA = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, $"{prefix}-a", agentId);
        var triggerB = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, $"{prefix}-b", agentId);

        var broad = await GenerateKeyAsync(client, $"{prefix}-broad", allowedTriggerIds: null, principalId: null);
        // The same integrator, a narrower credential: the exact pairing principal-only masking let through.
        var narrow = await GenerateKeyAsync(client, $"{prefix}-narrow", [triggerA.Id], broad.View.PrincipalId);
        var foreign = await GenerateKeyAsync(client, $"{prefix}-foreign", allowedTriggerIds: null, principalId: null);

        var (executionId, sessionId) = await SeedExecutionAsync(triggerB.Id, broad.View.PrincipalId, broad.View.KeyPrefix);
        return new Seeded(triggerB.Name, executionId, sessionId, broad.Key, narrow.Key, foreign.Key, broad.View.Id);
    }

    private async Task<GeneratedIntegrationApiKeyBody> GenerateKeyAsync(HttpClient client, string label, Guid[]? allowedTriggerIds, Guid? principalId)
    {
        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody(label, allowedTriggerIds, principalId));
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"Seeding the credential '{label}' must succeed.");
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<GeneratedIntegrationApiKeyBody>(IntegrationEndpointPayloads.Json));
    }

    /// <summary>
    ///     Writes an admitted row through the real store rather than through the invoke route: nothing reaches the
    ///     coordinator's channel this way, so the suite asserts on a stable <c>Accepted</c> row instead of racing a
    ///     background run that has no model to reach.
    /// </summary>
    private async Task<(Guid ExecutionId, Guid SessionId)> SeedExecutionAsync(Guid triggerId, Guid principalId, string keyPrefix)
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
        var executionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var admitted = await store.AcceptAsync(new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, triggerId, Guid.NewGuid(), Guid.NewGuid()),
                executionId,
                triggerId,
                sessionId,
                principalId,
                Guid.NewGuid(),
                new byte[]
                {
                    1,
                    2,
                    3
                },
                keyPrefix,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new IntegrationEventAppend(Guid.NewGuid(),
                    executionId,
                    Sequence: 1,
                    IntegrationStreamEventTypes.ExecutionAccepted,
                    DetailJson: null,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
            maxActive: 4096,
            maxActivePerPrincipal: 4096);
        AssertEx.True(admitted, "Seeding the execution row must be admitted.");
        return (executionId, sessionId);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, string? key)
    {
        using var request = new HttpRequestMessage(method, route);
        if (key is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {key}");
        }

        using var content = method == HttpMethod.Post
            ? JsonContent.Create(new
            {
                requestId = Guid.NewGuid()
            })
            : null;
        request.Content = content;
        return await client.SendAsync(request);
    }

    private sealed record Seeded(
        string TriggerBName,
        Guid ExecutionUnderB,
        Guid SessionUnderB,
        string BroadKey,
        string NarrowKey,
        string ForeignKey,
        Guid BroadKeyId);

    private sealed record StatusBody(
        Guid ExecutionId,
        Guid SessionId,
        string Status,
        string? FailureCategory,
        string? FailureSummary,
        long ReceivedAtUnixMs,
        long? StartedAtUnixMs,
        long? EndedAtUnixMs,
        int OutputCount,
        JsonElement Links);

    private sealed record SessionStatusBody(Guid SessionId, string TriggerName, string Status, int ExecutionCount, long LastActivityUtc);
}

/// <summary>The external route strings, written out so a suite asserts the literal paths a caller has to use.</summary>
internal static class IntegrationApiRoutes
{
    public static string Invoke(string triggerName) =>
        $"/api/local/v1/integration-api/triggers/{triggerName}/invoke";

    public static string Execution(Guid executionId) =>
        $"/api/local/v1/integration-api/executions/{executionId:D}";

    public static string Events(Guid executionId) =>
        $"/api/local/v1/integration-api/executions/{executionId:D}/events";

    public static string Cancel(Guid executionId) =>
        $"/api/local/v1/integration-api/executions/{executionId:D}/cancel";

    public static string Session(Guid sessionId) =>
        $"/api/local/v1/integration-api/sessions/{sessionId:D}";
}
