namespace XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three Operator-gated credential endpoints. The load-bearing assertions are that the plaintext appears
///     exactly once — never on a list — and that a revoke keeps the row, because execution and audit history reference
///     the credential's prefix.
/// </summary>
public sealed class IntegrationApiKeyEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task List_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client, HttpMethod.Get, IntegrationEndpointPayloads.KeysRoute)
                                                              .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Generate_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsNonOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody("viewer-probe")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Generate_WithABlankLabel_Returns400()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody(label: "")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Generate_WithAnEmptyAllowlist_Returns400()
    {
        // "Every trigger" is expressed by OMITTING the allowlist; an explicit [] is a key that can invoke nothing,
        // which is never what an operator means.
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody("empty-allowlist", allowedTriggerIds: [])).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Generate_ReturnsThePlaintextOnceAndNeverOnTheList()
    {
        using var client = Factory.CreateClient();

        using var generated = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody("show-once-probe")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, generated.StatusCode);
        var body = AssertEx.NotNull(await generated.Content.ReadFromJsonAsync<GeneratedIntegrationApiKeyBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.True(body.Key.StartsWith("xeint_", StringComparison.Ordinal));
        AssertEx.True(body.Key.StartsWith(body.View.KeyPrefix, StringComparison.Ordinal));
        AssertEx.NotEqual(Guid.Empty, body.View.PrincipalId);
        AssertEx.Null(body.View.AllowedTriggerIds, "An omitted allowlist means every trigger.");

        using var list = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, IntegrationEndpointPayloads.KeysRoute)
                                                          .ConfigureAwait(false);
        var listedJson = await list.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.False(listedJson.Contains(body.Key, StringComparison.Ordinal), "The plaintext must never appear on a read surface.");

        var listed = AssertEx.NotNull(await list.Content.ReadFromJsonAsync<IntegrationApiKeyListBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.Contains(listed.Items, item => item.Id == body.View.Id && item.RevokedAtUtc is null);
    }

    [Test]
    public async Task Generate_WithAnExistingPrincipal_AddsASecondCredentialToTheSameIntegrator()
    {
        using var client = Factory.CreateClient();
        using var first = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody("rotation-probe")).ConfigureAwait(false);
        var original = AssertEx.NotNull(await first.Content.ReadFromJsonAsync<GeneratedIntegrationApiKeyBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));

        using var second = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody("rotation-probe-v2", principalId: original.View.PrincipalId)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, second.StatusCode);
        var rotated = AssertEx.NotNull(await second.Content.ReadFromJsonAsync<GeneratedIntegrationApiKeyBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.Equal(original.View.PrincipalId, rotated.View.PrincipalId);
        AssertEx.NotEqual(original.View.Id, rotated.View.Id);
        AssertEx.NotEqual(original.View.KeyPrefix, rotated.View.KeyPrefix);
    }

    [Test]
    public async Task Generate_WithAnAllowlist_RoundTripsIt()
    {
        using var client = Factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, "allowlist-probe-agent").ConfigureAwait(false);
        var trigger = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, "allowlist-probe", agentId).ConfigureAwait(false);

        using var generated = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody("narrow-probe", [trigger.Id])).ConfigureAwait(false);

        var body = AssertEx.NotNull(await generated.Content.ReadFromJsonAsync<GeneratedIntegrationApiKeyBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.True(AssertEx.NotNull(body.View.AllowedTriggerIds).SequenceEqual(new[]
        {
            trigger.Id
        }));
    }

    [Test]
    public async Task Revoke_Returns204AndKeepsTheRowThenAnswers404ForAnUnknownId()
    {
        using var client = Factory.CreateClient();
        using var generated = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            IntegrationEndpointPayloads.KeysRoute,
            IntegrationEndpointPayloads.KeyBody("revoke-probe")).ConfigureAwait(false);
        var body = AssertEx.NotNull(await generated.Content.ReadFromJsonAsync<GeneratedIntegrationApiKeyBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));

        using var revoked = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"{IntegrationEndpointPayloads.KeysRoute}/{body.View.Id}").ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using var list = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, IntegrationEndpointPayloads.KeysRoute)
                                                          .ConfigureAwait(false);
        var listed = AssertEx.NotNull(await list.Content.ReadFromJsonAsync<IntegrationApiKeyListBody>(IntegrationEndpointPayloads.Json).ConfigureAwait(false));
        AssertEx.Contains(listed.Items,
            item => item.Id == body.View.Id && item.RevokedAtUtc is not null,
            "Revocation is soft: the row survives so execution and audit history keep a credential to name.");

        using var unknown = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"{IntegrationEndpointPayloads.KeysRoute}/{Guid.NewGuid()}").ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Test]
    public async Task Revoke_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await IntegrationEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Delete,
            $"{IntegrationEndpointPayloads.KeysRoute}/{Guid.NewGuid()}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
