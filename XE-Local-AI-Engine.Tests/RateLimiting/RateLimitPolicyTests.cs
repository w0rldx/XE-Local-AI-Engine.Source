namespace XE_Local_AI_Engine.Tests.RateLimiting;

using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        EnvironmentName = "RateLimitEnforcement",
        AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // The external integration family's COARSE PER-IP ceiling, lowered so its window is observable here. This is
            // the IP key, not RateLimitPerMinute: that 600 is the per-principal budget and it is spent inside the
            // hand-mapped handler, where a principal exists, not by this middleware.
            ["Integrations:IpRateLimitPerMinute"] = "2"
        }
    };

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
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

    // The integration family's per-IP ceiling, lowered by the fixture's configuration overlay above.
    private const int IntegrationIpPermitLimit = 2;

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
                NodeAuthRateLimits.LocalModelProxyPolicy,
                NodeAuthRateLimits.IntegrationApiPolicy
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

    [Test]
    public async Task IntegrationApiPolicy_WhenWindowExhausted_Returns429WithRetryAfter()
    {
        using var client = Host.Factory.CreateClient();

        // The limiter runs BEFORE authentication, so an unauthenticated 401 still spends a permit. That is asserted
        // here as a FACT about this layer, not as a desirable one: it is precisely why the per-IP policy is a coarse
        // abuse ceiling and per-principal fairness lives in the handler instead.
        HttpResponseMessage? rejected = null;
        var attempts = 0;
        try
        {
            for (var attempt = 1; attempt <= (IntegrationIpPermitLimit * 2) + 1; attempt++)
            {
                attempts = attempt;
                var response = await PostInvokeAsync(client).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    rejected = response;
                    break;
                }

                AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "An unauthenticated invoke is a 401, which still spends a permit.");
                response.Dispose();
            }

            var throttled = rejected
                            ?? throw new AssertionException($"No 429 within {attempts} invoke attempts — the {IntegrationIpPermitLimit}/window integration permit limit is not enforced.");
            AssertEx.True(attempts > IntegrationIpPermitLimit, $"Invoke attempt {attempts} was rejected inside the {IntegrationIpPermitLimit}/window permit limit.");
            AssertEx.Equal("60",
                throttled.Headers.TryGetValues("Retry-After", out var retryAfter) ? string.Join(",", retryAfter) : null,
                "A 429 must carry the Retry-After hint OnRejected sets.");
        }
        finally
        {
            rejected?.Dispose();
        }
    }

    /// <summary>
    ///     Test 42 / §9(a): an open SSE response consumes exactly ONE permit, and holding it consumes nothing further.
    ///     <para>
    ///         Two halves, because neither alone is the claim. First, the stream routes really do keep
    ///         <c>.RequireRateLimiting</c> — D5 stands and R2-11 forbids removing it to make anything pass. Second, the
    ///         REGISTERED policy is a fixed window over the shared peer-address partition, and a fixed-window lease
    ///         returns nothing on disposal: holding one for a response's whole lifetime is therefore indistinguishable
    ///         from releasing it at once, which is exactly the premise D5 rests on. A concurrency limiter is what would
    ///         have made an open stream cost a slot.
    ///     </para>
    /// </summary>
    [Test]
    public void IntegrationApiPolicy_IsFixedWindow_SoAnOpenStreamCostsOnePermitAndHoldsNothing()
    {
        var streamRoutes = Host.Factory.Services.GetRequiredService<EndpointDataSource>()
                               .Endpoints
                               .OfType<RouteEndpoint>()
                               .Where(static endpoint => endpoint.RoutePattern.RawText?.Contains("integration-api/executions/{executionId}/events", StringComparison.Ordinal) == true)
                               .ToArray();

        AssertEx.NotEmpty(streamRoutes, "The external events route must be mapped, or there is no stream to rate-limit.");
        foreach (var route in streamRoutes)
        {
            AssertEx.Contains(route.Metadata.OfType<EnableRateLimitingAttribute>().Select(static attribute => attribute.PolicyName),
                NodeAuthRateLimits.IntegrationApiPolicy,
                $"{route.RoutePattern.RawText} must keep its rate-limiting policy: R2-11 forbids removing it from a stream route.");
        }

        // The REGISTERED policy, not a limiter this test built: building one here would assert the BCL, and swapping
        // GetFixedWindowLimiter for a concurrency limiter — the exact change D5 rests on not happening — would leave it
        // green. The map and the partition's factory are internal, so both are read by reflection.
        var options = Host.Factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var policyMap = AssertEx.NotNull(Property(options, "PolicyMap") as IDictionary,
            "RateLimiterOptions no longer exposes a policy map; this assertion has to be rewritten against whatever replaced it.");
        var policy = AssertEx.NotNull(policyMap[NodeAuthRateLimits.IntegrationApiPolicy], $"{NodeAuthRateLimits.IntegrationApiPolicy} is not registered.");

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        var partition = AssertEx.NotNull(policy.GetType().GetMethod("GetPartition")?.Invoke(policy, [context]), "The policy no longer answers GetPartition.");
        AssertEx.Equal("203.0.113.7",
            Property(AssertEx.NotNull(Property(partition, "PartitionKey"), "The partition carries no key."), "Key")?.ToString(),
            "The integration family partitions on the peer address through the SAME helper as the three policies beside it: this middleware runs before authentication, so no claim exists to partition on.");

        var factory = AssertEx.NotNull(Property(partition, "Factory") as Delegate, "The partition carries no limiter factory.");
        using var limiter = AssertEx.NotNull(factory.DynamicInvoke(Property(partition, "PartitionKey")) as RateLimiter, "The factory produced no limiter.");

        AssertEx.True(limiter is FixedWindowRateLimiter,
            $"The policy must be a FIXED WINDOW and is {limiter.GetType().Name}: a fixed-window lease returns nothing on disposal, so holding one for a whole SSE response is indistinguishable from releasing it at once — which is the premise D5 rests on. A concurrency limiter would make an open stream cost a slot for its lifetime.");
        AssertEx.Equal(IntegrationIpPermitLimit,
            (int)AssertEx.NotNull(limiter.GetStatistics(), "The limiter reports no statistics.").CurrentAvailablePermits,
            "The window must carry the configured per-IP permit limit, which is what this fixture lowered to make the ceiling observable.");
    }

    /// <summary>Reads a property whatever its visibility: the rate-limiting policy map and partition factory are internal.</summary>
    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static Task<HttpResponseMessage> PostInvokeAsync(HttpClient client)
    {
        return client.PostAsJsonAsync(new Uri("/api/local/v1/integration-api/triggers/rate-limit-probe/invoke", UriKind.Relative), new
        {
            requestId = Guid.NewGuid()
        });
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
