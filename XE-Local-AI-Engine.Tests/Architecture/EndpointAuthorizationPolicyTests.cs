namespace XE_Local_AI_Engine.Tests.Architecture;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;
using EndpointDefinition = FastEndpoints.EndpointDefinition;

/// <summary>
///     Deny-by-default, asserted against the routes the host actually built rather than against the source that
///     built them. Every check below reads the EFFECTIVE ASP.NET Core metadata on the mapped
///     <see cref="RouteEndpoint" /> — <see cref="IAuthorizeData" /> and <see cref="IAllowAnonymous" /> — never
///     FastEndpoints' own <c>AnonymousVerbs</c>/<c>PreBuiltUserPolicies</c> bookkeeping, because those can diverge
///     from what is enforced: <c>Options(Action&lt;RouteHandlerBuilder&gt;)</c> lets an endpoint's
///     <c>Configure()</c> attach <see cref="IAllowAnonymous" /> straight onto the route without touching
///     <c>AnonymousVerbs</c>, and ASP.NET Core honours it over any policy also present.
///     <para>
///         The global <c>Endpoints.Configurator</c> in <c>Program.cs</c> is what makes a forgotten
///         <c>Policies()</c> call harmless. Four hundred and forty-five of the endpoints also call it themselves,
///         so deleting the Configurator would leave a "does everything resolve to Operator" test green —
///         <see cref="ConfiguratorCanaryProbeEndpoint" /> is the one endpoint with no protection of its own, and
///         the named assertion on it is the actual regression guard.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class EndpointAuthorizationPolicyTests
{
    /// <summary>
    ///     Non-vacuity floor for the FastEndpoints route set. The real count is four hundred and forty-six
    ///     (four hundred and forty-five plus this slice's canary); the floor sits under it because it exists to
    ///     catch an empty or half-built route table, not to notice a retired endpoint.
    /// </summary>
    private const int EndpointFloor = 400;

    /// <summary>Every SignalR hub type in the host assembly. An exact count, so a twentieth hub fails here.</summary>
    private const int HubTypeCount = 19;

    /// <summary>The eight hand-mapped minimal-API routes plus <c>MapMcp</c>'s single stateless POST.</summary>
    private const int MinimalApiRouteCount = 9;

    /// <summary>
    ///     Microsoft.AspNetCore.TestHost maps this for its own base-address bookkeeping. It exists only under the
    ///     test server, carries no authorization metadata, and is not a surface this repo ships.
    /// </summary>
    private const string TestHostInternalRoute = "_test_url_cache_";

    private const string CanaryEndpointTypeName =
        "XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1.ConfiguratorCanaryProbeEndpoint";

    /// <summary>
    ///     The pre-authentication bootstrap flow — the only endpoints allowed to be anonymous. A fixed list, checked
    ///     in both directions: an endpoint outside it that is anonymous fails, and an entry here that no longer
    ///     matches any endpoint fails too, so the list cannot rot into a silent exemption.
    /// </summary>
    private static readonly string[] AnonymousEndpointTypeNames =
    [
        "XE_Local_AI_Engine.Client.Endpoints.Auth.V1.NodeAuthStatusEndpoint",
        "XE_Local_AI_Engine.Client.Endpoints.Auth.V1.NodeSetupEndpoint",
        "XE_Local_AI_Engine.Client.Endpoints.Auth.V1.NodeLoginEndpoint",
        "XE_Local_AI_Engine.Client.Endpoints.Auth.V1.NodeRefreshEndpoint"
    ];

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public void EveryFastEndpointsEndpoint_ResolvesToOperatorPolicyOrIsExplicitlyAnonymous()
    {
        var endpoints = RouteEndpoints(Factory)
                        .Where(endpoint => endpoint.Metadata.GetMetadata<EndpointDefinition>() is not null)
                        .ToList();

        AssertEx.True(endpoints.Count >= EndpointFloor,
            $"Only {endpoints.Count} FastEndpoints routes were discovered; the floor is {EndpointFloor}. "
            + "A route table this small means the host did not finish mapping, so nothing below proved anything.");

        var seenAnonymous = new HashSet<string>(StringComparer.Ordinal);
        var canarySeen = false;

        foreach (var endpoint in endpoints)
        {
            var endpointType = endpoint.Metadata.GetMetadata<EndpointDefinition>()!.EndpointType;
            var name = endpointType.FullName ?? endpointType.Name;
            var allowAnonymous = IsAnonymous(endpoint);
            var hasOperator = HasPolicy(endpoint, NodeAuthorizationPolicies.Operator);

            if (AnonymousEndpointTypeNames.Contains(name, StringComparer.Ordinal))
            {
                seenAnonymous.Add(name);
                AssertEx.True(allowAnonymous,
                    $"{name} is on the anonymous allowlist but no longer carries IAllowAnonymous metadata.");
                AssertEx.False(hasOperator,
                    $"{name} is on the anonymous allowlist but resolved to the Operator policy; the allowlist is stale.");
                continue;
            }

            if (string.Equals(name, CanaryEndpointTypeName, StringComparison.Ordinal))
            {
                canarySeen = true;
                AssertEx.True(hasOperator,
                    "ConfiguratorCanaryProbeEndpoint calls neither Policies() nor AllowAnonymous(); the global "
                    + "config.Endpoints.Configurator line in Program.cs is its only source of protection. It "
                    + "resolving to anything but the Operator policy means that line was deleted or stopped running, "
                    + "and every future endpoint that forgets Policies() would now ship anonymous.");
                AssertEx.False(allowAnonymous,
                    "ConfiguratorCanaryProbeEndpoint carries IAllowAnonymous metadata; it must never opt out.");
                continue;
            }

            AssertEx.True(hasOperator,
                $"{name} does not resolve to the {NodeAuthorizationPolicies.Operator} policy. Every endpoint outside "
                + "the four-entry pre-authentication allowlist must, and the global Endpoints.Configurator in "
                + "Program.cs supplies it even when Configure() forgets.");
            AssertEx.False(allowAnonymous,
                $"{name} carries IAllowAnonymous metadata but is not on the pre-authentication allowlist. A new "
                + "anonymous endpoint is a deliberate decision: add it to AnonymousEndpointTypeNames on purpose.");
        }

        AssertEx.True(canarySeen,
            $"{CanaryEndpointTypeName} was not among the mapped endpoints. The Configurator canary must always be "
            + "registered — without it, deleting the global Configurator would break nothing in this suite.");

        foreach (var expected in AnonymousEndpointTypeNames)
        {
            AssertEx.True(seenAnonymous.Contains(expected),
                $"{expected} is on the anonymous allowlist but no endpoint of that type was mapped. Remove the stale "
                + "entry rather than leaving an unused exemption behind.");
        }
    }

    [Test]
    public async Task EveryHub_ResolvesToTheOperatorPolicy()
    {
        var hubTypes = typeof(LocalChatHub).Assembly
                                           .GetTypes()
                                           .Where(type => type.IsClass && !type.IsAbstract && typeof(Hub).IsAssignableFrom(type))
                                           .OrderBy(type => type.FullName, StringComparer.Ordinal)
                                           .ToList();

        AssertEx.Equal(expected: HubTypeCount, hubTypes.Count,
            "The host assembly no longer declares exactly nineteen hub types. This is an inventory check: a new hub "
            + "type must be added to this suite's coverage deliberately, because a hub's mapping and its "
            + "authorization are separate decisions.");

        var routes = RouteEndpoints(Factory);

        foreach (var hubType in hubTypes)
        {
            AssertHubIsOperatorProtected(routes, hubType);
        }

        // DevelopmentAttemptHub is the one hub Program.cs maps conditionally. Development Mode defaults to ENABLED
        // (Development:Enabled, defaultValue: true), so the shared factory above already covered it in the
        // configuration where it is reachable — an "assert it is absent by default" check would have proved nothing.
        // The other half of the proof is that turning the feature off genuinely unmaps it rather than leaving an
        // unprotected route behind.
        await using var developmentDisabledFactory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = false
        };

        var developmentDisabledRoutes = RouteEndpoints(developmentDisabledFactory);

        // Non-vacuity for the second host: an empty or half-built route table would make the absence check below
        // pass for the wrong reason. An unconditional hub has to be there before "the conditional one is not" means
        // anything.
        AssertEx.NotEmpty(HubRouteEndpoints(developmentDisabledRoutes, typeof(LocalChatHub)),
            "LocalChatHub is not mapped with Development Mode off, so this host's route table cannot be trusted to "
            + "say anything about DevelopmentAttemptHub's absence.");

        AssertEx.Empty(HubRouteEndpoints(developmentDisabledRoutes, typeof(DevelopmentAttemptHub)),
            "DevelopmentAttemptHub is still mapped with Development Mode off. Its route must disappear with the "
            + "feature, not merely stop being reachable through some other gate.");
    }

    [Test]
    public void NonFastEndpointsMinimalApiRoutes_RequireTheirNamedPolicy()
    {
        var apiPrefix = $"/{LocalApiRoutes.Prefix}/";
        (string Path, string Method, string Policy)[] expectedRoutes =
        [
            (apiPrefix + LocalApiRoutes.Proxy.Models, "GET", NodeAuthorizationPolicies.LocalModelProxy),
            (apiPrefix + LocalApiRoutes.Proxy.ChatCompletions, "POST", NodeAuthorizationPolicies.LocalModelProxy),
            (apiPrefix + LocalApiRoutes.Proxy.Embeddings, "POST", NodeAuthorizationPolicies.LocalModelProxy),
            (apiPrefix + LocalApiRoutes.IntegrationApi.Invoke, "POST", NodeAuthorizationPolicies.IntegrationApi),
            (apiPrefix + LocalApiRoutes.IntegrationApi.ExecutionById, "GET", NodeAuthorizationPolicies.IntegrationApi),
            (apiPrefix + LocalApiRoutes.IntegrationApi.ExecutionEvents, "GET", NodeAuthorizationPolicies.IntegrationApi),
            (apiPrefix + LocalApiRoutes.IntegrationApi.ExecutionCancel, "POST", NodeAuthorizationPolicies.IntegrationApi),
            (apiPrefix + LocalApiRoutes.IntegrationApi.SessionById, "GET", NodeAuthorizationPolicies.IntegrationApi),
            // MCP's transport is stateless (SEP-2567), and the stateless mapping is one POST route — no GET, no
            // DELETE. Its pattern carries a trailing slash the SDK appends to the path Program.cs passes; routing
            // matches the slashless request to it either way, but RawText is what this lookup compares.
            (apiPrefix + LocalApiRoutes.Mcp.ServerEndpoint + "/", "POST", NodeAuthorizationPolicies.McpServer)
        ];

        var routes = RouteEndpoints(Factory);

        // The table above is compared against the host's ACTUAL hand-mapped set, not against its own length: a
        // count of the literals written here proves nothing. Three kinds of route are excluded, each by what it
        // carries rather than by position:
        //   - FastEndpoints endpoints and SignalR hubs, which the other two tests own;
        //   - anything carrying IAllowAnonymous — the two health probes, the SPA shell and the dev-only Scalar
        //     pages opted out deliberately, and the FallbackPolicy design is what covers them;
        //   - the TestServer's own internal route, which exists in no real host.
        // What is left is exactly the surface Program.cs hand-maps and must authorize itself, so a new Map* call
        // that nobody thought about fails here by name.
        var handMapped = routes.Where(endpoint => endpoint.Metadata.GetMetadata<EndpointDefinition>() is null)
                               .Where(endpoint => endpoint.Metadata.GetMetadata<HubMetadata>() is null)
                               .Where(endpoint => !IsAnonymous(endpoint))
                               .Where(endpoint => !string.Equals(endpoint.RoutePattern.RawText,
                                   TestHostInternalRoute,
                                   StringComparison.Ordinal))
                               .SelectMany(RouteKeys)
                               .ToList();

        var expectedKeys = expectedRoutes.Select(route => $"{route.Method} {route.Path}").ToList();
        var unexpected = handMapped.Except(expectedKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var absent = expectedKeys.Except(handMapped, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(unexpected,
            $"The host hand-maps {unexpected.Count} route(s) this test does not know about: "
            + $"{string.Join("; ", unexpected)}. A hand-mapped route sits outside the FastEndpoints Configurator, so "
            + "nothing else forces it to carry a policy — add it to the table with the policy it is meant to "
            + "require, or give it an explicit .AllowAnonymous() if being public is the decision.");
        AssertEx.Empty(absent,
            $"This test expects route(s) the host no longer maps: {string.Join("; ", absent)}.");
        AssertEx.Equal(expected: MinimalApiRouteCount, handMapped.Count,
            $"Expected exactly {MinimalApiRouteCount} hand-mapped, self-authorizing routes (eight minimal-API maps "
            + $"plus MapMcp's single stateless POST); the host has {handMapped.Count}.");

        foreach (var (path, method, policy) in expectedRoutes)
        {
            var match = AssertEx.NotNull(routes.SingleOrDefault(endpoint =>
                    string.Equals(endpoint.RoutePattern.RawText, path, StringComparison.Ordinal)
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { } methods
                    && methods.HttpMethods.Contains(method, StringComparer.Ordinal)),
                $"No route was mapped for {method} {path}. The hand-mapped minimal-API and MCP surfaces are outside "
                + "the FastEndpoints Configurator, so each one's authorization is asserted individually here.");

            AssertEx.True(HasPolicy(match, policy),
                $"{method} {path} does not require the {policy} policy. These routes never fall back to Operator: "
                + "each accepts exactly one API-key scheme, and losing that is a privilege change, not a detail.");
            AssertEx.False(IsAnonymous(match),
                $"{method} {path} carries IAllowAnonymous metadata, which wins over any policy also present.");
        }
    }

    private static void AssertHubIsOperatorProtected(IReadOnlyList<RouteEndpoint> routes, Type hubType)
    {
        var matches = HubRouteEndpoints(routes, hubType);

        AssertEx.NotEmpty(matches, $"{hubType.Name} must be mapped; no route carries its HubMetadata.");

        foreach (var match in matches)
        {
            AssertEx.True(HasPolicy(match, NodeAuthorizationPolicies.Operator),
                $"{hubType.Name} route '{match.RoutePattern.RawText}' does not require the "
                + $"{NodeAuthorizationPolicies.Operator} policy. Both the [Authorize] attribute on the hub class and "
                + "the RequireAuthorization chain at the MapHub call site would have to be gone for this to happen.");
            AssertEx.False(IsAnonymous(match),
                $"{hubType.Name} route '{match.RoutePattern.RawText}' carries IAllowAnonymous metadata.");
        }
    }

    private static IReadOnlyList<RouteEndpoint> HubRouteEndpoints(IReadOnlyList<RouteEndpoint> routes, Type hubType) =>
    [
        .. routes.Where(endpoint => endpoint.Metadata.GetMetadata<HubMetadata>()?.HubType == hubType)
    ];

    private static IReadOnlyList<RouteEndpoint> RouteEndpoints(TestServerWebAppFactory factory) =>
    [
        .. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
    ];

    private static IEnumerable<string> RouteKeys(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return methods is null or { Count: 0 }
            ? [$"(any method) {endpoint.RoutePattern.RawText}"]
            : methods.Select(method => $"{method} {endpoint.RoutePattern.RawText}");
    }

    private static bool IsAnonymous(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    private static bool HasPolicy(RouteEndpoint endpoint, string policy) =>
        endpoint.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Any(data => string.Equals(data.Policy, policy, StringComparison.Ordinal));
}
