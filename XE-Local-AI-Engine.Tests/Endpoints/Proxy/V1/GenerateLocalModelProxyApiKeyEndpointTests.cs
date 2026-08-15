namespace XE_Local_AI_Engine.Tests.Endpoints.Proxy.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>POST proxy/key</c>: operator-gated, and the ONLY place the plaintext credential ever appears. The suite pins
///     the show-once contract — the mint returns the key, every subsequent read returns only the digest's prefix, and a
///     regenerate rotates rather than re-reveals.
///     <para>
///         Serialized: the node stores exactly ONE proxy credential, so two of these tests running concurrently against
///         the shared host would rotate each other's key out from under them.
///     </para>
/// </summary>
[NotInParallel("LocalModelProxyApiKeyGenerate")]
public sealed class GenerateLocalModelProxyApiKeyEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Generate_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await LocalModelProxyApiKeyRequests.AnonymousAsync(client, HttpMethod.Post).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Generate_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await LocalModelProxyApiKeyRequests.AsNonOperatorAsync(Factory, client, HttpMethod.Post).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Generate_RevealsPlaintextExactlyOnceAndRotatesOnRegenerate()
    {
        using var client = Factory.CreateClient();

        using var minted = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Post).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, minted.StatusCode);

        var generated = AssertEx.NotNull(await minted.Content.ReadFromJsonAsync<GeneratedLocalModelProxyApiKeyResponse>().ConfigureAwait(false));
        AssertEx.True(generated.Configured, "A freshly minted credential must report configured=true.");
        AssertEx.NotNullOrEmpty(generated.Key);
        var prefix = AssertEx.NotNull(generated.ApiKey).Prefix;
        AssertEx.NotNullOrEmpty(prefix);

        // Read #1: the status endpoint. The node persists only a SHA-256 digest, so the plaintext must be absent from
        // the raw body — checked on the wire, not just on the typed shape, so a stray field would still be caught.
        using var status = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Get).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusBody = await status.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.False(statusBody.Contains(generated.Key, StringComparison.Ordinal),
            "The status read must never return the plaintext proxy key.");

        var statusView = AssertEx.NotNull(await status.Content.ReadFromJsonAsync<LocalModelProxyApiKeyStatusResponse>().ConfigureAwait(false));
        AssertEx.True(statusView.Configured, "The status read must see the minted credential.");
        AssertEx.Equal(prefix, AssertEx.NotNull(statusView.ApiKey).Prefix);

        // Read #2: a second status call, to prove the first read did not consume some one-shot reveal.
        using var secondStatus = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Get).ConfigureAwait(false);
        var secondBody = await secondStatus.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.False(secondBody.Contains(generated.Key, StringComparison.Ordinal),
            "Re-reading the status must still never return the plaintext proxy key.");

        // Regenerate is rotation, not recovery: the new body carries a DIFFERENT key and the old one is gone for good.
        using var rotated = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Post).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var regenerated = AssertEx.NotNull(await rotated.Content.ReadFromJsonAsync<GeneratedLocalModelProxyApiKeyResponse>().ConfigureAwait(false));
        AssertEx.NotEqual(generated.Key, regenerated.Key, "A regenerate must mint a new key rather than re-reveal the old one.");
    }
}
