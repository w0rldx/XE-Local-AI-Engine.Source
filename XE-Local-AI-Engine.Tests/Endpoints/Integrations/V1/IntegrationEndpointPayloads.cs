namespace XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Wire payloads and helpers shared by the integration admin endpoint suites. Request bodies are anonymous objects
///     rather than the DTO records on purpose: these suites assert the HTTP contract the generated SDK actually sends,
///     so a rename on a DTO surfaces as a failing request here.
/// </summary>
internal static class IntegrationEndpointPayloads
{
    public const string TriggersRoute = "/api/local/v1/integrations/triggers";

    public const string KeysRoute = "/api/local/v1/integrations/keys";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    /// <summary>A minimal, valid trigger body. The agent id is caller-supplied because the service probes it.</summary>
    public static object TriggerBody(string name,
        Guid targetAgentDefinitionId,
        string displayName = "Sensor feed",
        string? description = null,
        bool enabled = true,
        string sessionPolicy = "PerInvocation",
        string[]? acceptedInputKinds = null) =>
        new
        {
            name,
            displayName,
            description,
            enabled,
            targetKind = "Agent",
            targetAgentDefinitionId,
            sessionPolicy,
            acceptedInputKinds = acceptedInputKinds ?? ["text", "json"]
        };

    public static object UpdateBody(Guid targetAgentDefinitionId,
        long expectedVersion,
        string displayName = "Sensor feed",
        string? description = null,
        bool enabled = true,
        string sessionPolicy = "PerInvocation",
        string[]? acceptedInputKinds = null) =>
        new
        {
            displayName,
            description,
            enabled,
            targetAgentDefinitionId,
            sessionPolicy,
            acceptedInputKinds = acceptedInputKinds ?? ["text", "json"],
            expectedVersion
        };

    public static object KeyBody(string label, Guid[]? allowedTriggerIds = null, Guid? principalId = null) =>
        new
        {
            label,
            allowedTriggerIds,
            principalId
        };

    /// <summary>Sends <paramref name="method" /> to <paramref name="route" /> as the operator (Admin role).</summary>
    public static Task<HttpResponseMessage> SendAsOperatorAsync(TestServerWebAppFactory factory,
        HttpClient client,
        HttpMethod method,
        string route,
        object? body = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return SendAsync(client, method, route, body, factory.AddNodeBearerToken);
    }

    /// <summary>Sends the same request as an authenticated principal that is NOT the operator.</summary>
    public static Task<HttpResponseMessage> SendAsNonOperatorAsync(TestServerWebAppFactory factory,
        HttpClient client,
        HttpMethod method,
        string route,
        object? body = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return SendAsync(client, method, route, body, factory.AddNonOperatorBearerToken);
    }

    /// <summary>Sends the same request with no credentials at all.</summary>
    public static Task<HttpResponseMessage> SendAnonymousAsync(HttpClient client, HttpMethod method, string route, object? body = null) =>
        SendAsync(client, method, route, body, authenticate: null);

    /// <summary>Creates a trigger through the real POST endpoint, failing loudly if it did not 201.</summary>
    public static async Task<IntegrationTriggerBody> CreateTriggerAsync(TestServerWebAppFactory factory,
        HttpClient client,
        string name,
        Guid targetAgentDefinitionId,
        string sessionPolicy = "PerInvocation")
    {
        using var response = await SendAsOperatorAsync(factory,
            client,
            HttpMethod.Post,
            TriggersRoute,
            TriggerBody(name, targetAgentDefinitionId, sessionPolicy: sessionPolicy)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode, $"Seeding the trigger '{name}' must succeed.");
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<IntegrationTriggerBody>(Json).ConfigureAwait(false));
    }

    /// <summary>
    ///     Mints a credential through the real POST endpoint. The plaintext exists only in this response, and the
    ///     resolved principal is what every ownership rule downstream keys on.
    /// </summary>
    public static async Task<GeneratedIntegrationApiKeyBody> GenerateKeyAsync(TestServerWebAppFactory factory,
        HttpClient client,
        string label,
        Guid[]? allowedTriggerIds = null,
        Guid? principalId = null)
    {
        using var response = await SendAsOperatorAsync(factory, client, HttpMethod.Post, KeysRoute, KeyBody(label, allowedTriggerIds, principalId)).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"Seeding the credential '{label}' must succeed.");
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<GeneratedIntegrationApiKeyBody>(Json).ConfigureAwait(false));
    }

    /// <summary>
    ///     Seeds a real agent definition, because the trigger service PROBES the target agent rather than trusting the
    ///     id — a stubbed store would test a different code path than the one that ships.
    /// </summary>
    public static async Task<Guid> SeedAgentAsync(TestServerWebAppFactory factory, string name)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var agent = await store.AddAsync(new AgentDefinitionInput(name,
            Description: null,
            "You are a careful integration agent.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson: null)).ConfigureAwait(false);
        return agent.Id;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        HttpMethod method,
        string route,
        object? body,
        Action<HttpRequestMessage>? authenticate)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        authenticate?.Invoke(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}

/// <summary>The response shape the suites read back, declared here rather than reusing the server DTO.</summary>
internal sealed record IntegrationTriggerBody(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description,
    bool Enabled,
    string TargetKind,
    Guid TargetAgentDefinitionId,
    string SessionPolicy,
    IReadOnlyList<string> AcceptedInputKinds,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version);

internal sealed record IntegrationTriggerListBody(IReadOnlyList<IntegrationTriggerBody> Items);

internal sealed record IntegrationApiKeyBody(
    Guid Id,
    Guid PrincipalId,
    string KeyPrefix,
    string Label,
    IReadOnlyList<Guid>? AllowedTriggerIds,
    long CreatedAtUtc,
    long? LastUsedAtUtc,
    long? RevokedAtUtc);

internal sealed record IntegrationApiKeyListBody(IReadOnlyList<IntegrationApiKeyBody> Items);

internal sealed record GeneratedIntegrationApiKeyBody(string Key, IntegrationApiKeyBody View);
