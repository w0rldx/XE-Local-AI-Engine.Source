namespace XE_Local_AI_Engine.Tests.RateLimiting;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Owns the ONE host in this module that actually runs <c>UseRateLimiter()</c>. Every other fixture runs under the
///     <c>Testing</c> environment, where <c>Program.CreateAppAsync</c> deliberately omits the middleware and
///     <c>ConfigureServices</c> relaxes the permit limits to non-limits — so before this fixture existed no test could
///     observe a 429 at all, and a renamed or unregistered policy would have shipped silently.
///     <para>
///         The middleware's <c>PartitionedRateLimiter</c> replenishment timer is never disposed and GC-roots the host
///         for the process lifetime (docs/agent-knowledge.md §1). That cost is paid exactly once here: one host, shared
///         across every assertion in <see cref="RateLimitPolicyTests" />, never one per test.
///     </para>
/// </summary>
public sealed class RateLimitedHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = new()
    {
        EnvironmentName = "RateLimitEnforcement"
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

/// <summary>
///     The rate limiter's three guarantees: a limited endpoint stops serving past its window, an unlimited one never
///     does, and every policy an endpoint asks for is actually registered.
///     <para>
///         Serialized as a class: the partition key is the peer address, and TestServer presents none, so every request
///         in this host shares the single <c>Unknown</c> partition. Only <see cref="AuthPolicy_WhenWindowExhausted_Returns429WithRetryAfter" />
///         spends <c>AuthRateLimit</c> permits — the other tests deliberately use unlimited or far-higher-permit
///         surfaces so they cannot poison its count.
///     </para>
/// </summary>
[NotInParallel("RateLimitEnforcement")]
public sealed class RateLimitPolicyTests
{
    // The production AuthPolicy permit limit (ConfigureServices): 10 requests per fixed 1-minute window per peer.
    private const int ProductionAuthPermitLimit = 10;

    [ClassDataSource<RateLimitedHostFixture>(Shared = SharedType.PerClass)]
    public required RateLimitedHostFixture Host { get; init; }

    [Test]
    public async Task AuthPolicy_WhenWindowExhausted_Returns429WithRetryAfter()
    {
        using var client = Host.Factory.CreateClient();

        // The window is a fixed minute. Firing exactly Limit+1 requests can straddle a window boundary under a slow,
        // contended run and silently reset the counter mid-loop, so send 2*Limit+1: at most one boundary falls inside a
        // few seconds of sequential in-process calls, which guarantees one window still sees Limit+1 requests.
        HttpResponseMessage? rejected = null;
        var attempts = 0;
        try
        {
            for (var attempt = 1; attempt <= (ProductionAuthPermitLimit * 2) + 1; attempt++)
            {
                attempts = attempt;
                var response = await PostLoginAsync(client).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    rejected = response;
                    break;
                }

                response.Dispose();
            }

            var throttled = rejected
                ?? throw new AssertionException($"No 429 within {attempts} login attempts — the {ProductionAuthPermitLimit}/window auth permit limit is not enforced.");
            AssertEx.True(attempts > ProductionAuthPermitLimit,
                $"Login attempt {attempts} was rejected inside the {ProductionAuthPermitLimit}/window permit limit.");

            // OnRejected writes both, and the React client and any external caller rely on them: without Retry-After a
            // caller cannot tell a throttle from a hard failure.
            AssertEx.Equal("60",
                throttled.Headers.TryGetValues("Retry-After", out var retryAfter) ? string.Join(",", retryAfter) : null,
                "A 429 must carry the Retry-After hint OnRejected sets.");

            var body = await throttled.Content.ReadAsStringAsync().ConfigureAwait(false);
            AssertEx.Contains(body, "Too many auth attempts", StringComparison.Ordinal);
        }
        finally
        {
            rejected?.Dispose();
        }
    }

    [Test]
    public async Task UnlimitedEndpoint_IsNotThrottled()
    {
        using var client = Host.Factory.CreateClient();

        // auth/status carries no RequireRateLimiting metadata, so the middleware must pass it through untouched even
        // well past every policy's permit limit. This is the control that proves the 429 above came from the policy
        // rather than from a global limiter accidentally left on.
        for (var attempt = 1; attempt <= (ProductionAuthPermitLimit * 3); attempt++)
        {
            using var response = await client.GetAsync(new Uri("/api/local/v1/auth/status", UriKind.Relative)).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"auth/status request {attempt} must not be throttled.");
        }
    }

    [Test]
    public void EveryReferencedPolicyName_IsOneOfTheRegisteredPolicies()
    {
        var referenced = Host.Factory.Services.GetRequiredService<EndpointDataSource>()
                             .Endpoints
                             .SelectMany(static endpoint => endpoint.Metadata.OfType<EnableRateLimitingAttribute>())
                             .Select(static attribute => attribute.PolicyName)
                             .Where(static name => name is not null)
                             .Distinct(StringComparer.Ordinal)
                             .ToHashSet(StringComparer.Ordinal);

        AssertEx.NotEmpty(referenced, "The endpoint graph must reference at least one rate-limiting policy.");

        // A policy name is a magic string on both sides: renaming the constant without re-registering it, or typing a
        // literal at a call site, produces a 500 at request time. Pinning the referenced set to the registered set
        // turns that into a compile-adjacent failure here instead.
        AssertEx.True(referenced.SetEquals(new[]
        {
            NodeAuthRateLimits.AuthPolicy,
            NodeAuthRateLimits.McpPolicy,
            NodeAuthRateLimits.LocalModelProxyPolicy
        }), $"Endpoints reference rate-limiting policies [{string.Join(", ", referenced)}], which is not the registered set.");
    }

    [Test]
    public async Task McpAndProxyPolicies_ResolveAtRequestTime()
    {
        using var client = Host.Factory.CreateClient();

        // The rate limiter runs BEFORE authentication, so an unauthenticated request still forces the middleware to
        // resolve the endpoint's policy. An unregistered policy surfaces there as a 500; a 401 proves it resolved.
        using var mcpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list"
            })
        };
        using var mcpResponse = await client.SendAsync(mcpRequest).ConfigureAwait(false);
        AssertEx.NotEqual(HttpStatusCode.InternalServerError,
            mcpResponse.StatusCode,
            $"{NodeAuthRateLimits.McpPolicy} must resolve; a 500 means it is referenced but not registered.");

        using var proxyResponse = await client.GetAsync(new Uri("/api/local/v1/proxy/v1/models", UriKind.Relative)).ConfigureAwait(false);
        AssertEx.NotEqual(HttpStatusCode.InternalServerError,
            proxyResponse.StatusCode,
            $"{NodeAuthRateLimits.LocalModelProxyPolicy} must resolve; a 500 means it is referenced but not registered.");
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client)
    {
        return client.PostAsJsonAsync(new Uri("/api/local/v1/auth/login", UriKind.Relative), new
        {
            email = "nobody@example.test",
            password = "not-the-password"
        });
    }
}
