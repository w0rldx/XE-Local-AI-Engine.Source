namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins WHAT the host is allowed to take as a constructor dependency: a FastEndpoints endpoint, a SignalR hub, the
///     DI-constructed collaborators that live beside the endpoints, and — since the rule was widened past the request
///     edge — every other class in the host that DI builds.
///     <para>
///         The host is the process boundary: HTTP, WebSockets, the composition root, and the adapters that sit on the
///         framework's own seams. It may compose the application layer, the shared contracts and framework services; it
///         may not reach past them into a store, a concrete provider's implementation surface, the EF Core API, the
///         Docker SDK, or a raw process / socket / file handle. Those are decisions the application layer owns, and a
///         host type that makes them itself is a second, undocumented copy of a service.
///     </para>
///     <para>
///         The rule once stopped at the endpoints and hubs, on the stated ground that a background service is not a
///         request handler. That line did not hold: the services behind it held the llama.cpp supervisor, the binary
///         manager and the release catalog by constructor, and three more reached the persistence stores through an
///         <see cref="IServiceScopeFactory" /> scope, which no constructor scan can see. They moved into
///         <c>Client.Application</c>, the fence moved with them, and
///         <see cref="HostServiceResolutionTests" /> closes the scope-factory hole.
///     </para>
///     <para>
///         Reflection over the compiled host rather than a source scan or an ArchUnitNET/NetArchTest rule: the rule is
///         per-constructor-PARAMETER and inspects each parameter's generic arguments recursively, which neither rule
///         engine expresses. A bespoke loop over <see cref="ConstructorInfo" /> is the smallest mechanism that can, and
///         it reads the same IL the host actually runs. The classifier itself lives in
///         <see cref="HostDependencyRule" /> so the source scan cannot drift from it.
///     </para>
///     <para>
///         Generic arguments, array element types and <c>Nullable&lt;T&gt;</c> underlying types are inspected
///         recursively, so <c>IOptions&lt;CodexOptions&gt;</c> counts as a dependency on <c>CodexOptions</c> even
///         though <c>IOptions&lt;T&gt;</c> itself is an allowed framework type. Classifying only the outer type
///         silently exempts any forbidden type wrapped in an allowed generic.
///     </para>
///     <para>
///         The rule holds for every type it scans and has no exemption list. It once carried one, frozen at the
///         violations that existed when the rule was written; each was migrated behind an application-layer service
///         until the list was empty, and it was then deleted. None may be reintroduced: a new violation is fixed by
///         moving the dependency into a <c>Client.Application</c> service the host type takes instead. The one
///         namespace outside the scan is the Data Protection key ring — see
///         <see cref="HostDependencyRule.DataProtectionNamespace" /> for why that one is about persisted data rather
///         than about taste.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class EndpointDependencyTests
{
    private static readonly Assembly ClientAssembly = typeof(WorkerHealthCheck).Assembly;

    /// <summary>Every FastEndpoints endpoint in the host: one scan's subject, and every scan's peer set.</summary>
    private static readonly IReadOnlyList<Type> EndpointTypes =
    [
        .. ClientAssembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(BaseEndpoint).IsAssignableFrom(type))
    ];

    private static readonly HashSet<string> EndpointNames =
        EndpointTypes.Select(type => type.FullName ?? type.Name).ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlyList<Type> HubTypes =
    [
        .. ClientAssembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(Hub).IsAssignableFrom(type))
    ];

    // Non-vacuity floors for the four scans below, set UNDER the counts measured on 2026-09-18 (446 endpoint types,
    // 19 hub types, 2 DI-constructed classes under Endpoints/, 40 elsewhere in the host) so a planned removal
    // is not brittle. A floor equal to its measurement is not rounded down: retiring one hub would turn a real
    // regression into a red herring about the floor. Each floor only has to catch a scan that found nothing or nearly
    // nothing; the EXACT hub inventory is pinned elsewhere, by EndpointAuthorizationPolicyTests, which is where a new
    // hub is meant to be noticed.
    private const int EndpointFloor = 400;
    private const int HubFloor = 15;

    /// <summary>
    ///     The collaborator scan counts the classes under the host's <c>Endpoints</c> namespace that DI builds and an
    ///     endpoint reaches, and deliberately not the DTOs, requests, responses, validators, size-limit metadata or
    ///     static mappers beside them — none of those takes a service. As measured there are two:
    ///     <c>DevWorkflowRunComposer</c> and <c>LocalApiSecurityMiddleware</c>.
    /// </summary>
    private const int CollaboratorFloor = 1;

    /// <summary>
    ///     The floor on the rest of the host: the hosted services, the hub-side event publishers and relays, the
    ///     exception handlers, the authentication handlers, the app-update and proxy services, the desktop lifecycle,
    ///     the boot backfills. Forty as measured on 2026-09-18.
    /// </summary>
    private const int HostFloor = 30;

    [Test]
    public void EndpointConstructors_TakeNoForbiddenDependency()
    {
        var violations = HostDependencyRule.Scan(EndpointTypes, EndpointNames);

        AssertEx.True(EndpointTypes.Count >= EndpointFloor,
            $"Expected the host's known endpoint surface; found {EndpointTypes.Count} endpoint types. The assembly marker or the reflection scan is broken.");

        AssertEx.Empty(violations, Explain("An endpoint", violations));
    }

    /// <summary>
    ///     The same fence over the WebSocket edge. A hub is a request handler with a different transport, and the rule
    ///     it was missing is the reason six hubs held a persistence store while every endpoint beside them did not.
    /// </summary>
    [Test]
    public void HubConstructors_TakeNoForbiddenDependency()
    {
        var violations = HostDependencyRule.Scan(HubTypes, EndpointNames);

        AssertEx.True(HubTypes.Count >= HubFloor,
            $"Only {HubTypes.Count} hub types were discovered; the floor is {HubFloor}. A scan that finds no hubs proves nothing.");

        AssertEx.Empty(violations, Explain("A hub", violations));
    }

    /// <summary>
    ///     The classes that sit BESIDE the endpoints and are built by DI for them — the run composer, the loopback
    ///     middleware — are the endpoint's own code by another name, so the fence follows them. Without this, moving a forbidden
    ///     dependency out of an endpoint's constructor and into a helper it injects would pass the rule while changing
    ///     nothing about who reaches the store.
    /// </summary>
    [Test]
    public void EndpointSideCollaboratorConstructors_TakeNoForbiddenDependency()
    {
        var collaborators = ClientAssembly.GetTypes().Where(IsEndpointSideCollaborator).ToList();
        var violations = HostDependencyRule.Scan(collaborators, EndpointNames);

        AssertEx.True(collaborators.Count >= CollaboratorFloor,
            $"Only {collaborators.Count} DI-constructed classes were found under {HostDependencyRule.EndpointsNamespace}; the floor is "
            + $"{CollaboratorFloor}. A predicate that matches nothing would make this rule vacuous. Found: "
            + string.Join(", ", collaborators.Select(type => type.Name).Order(StringComparer.Ordinal)));

        AssertEx.Empty(violations, Explain("A class built by DI under Endpoints/", violations));
    }

    /// <summary>
    ///     The rest of the host: every remaining class in the assembly that DI builds. An endpoint that may not take a
    ///     store while the hosted service two folders over takes three is not a rule about the host, it is a rule about
    ///     endpoints — and the dependency it forbids moves one file sideways to escape it.
    /// </summary>
    /// <remarks>
    ///     Subtracted from the assembly, and why: the three sets above (already scanned, and reported with their own
    ///     message); FluentValidation validators (declarative, no services); the composition root, which is excluded by
    ///     construction rather than by name — <c>Program</c>, <c>ConfigureServices</c> and every <c>Add…Extensions</c>
    ///     class is either static or parameterless, so <see cref="HostDependencyRule.IsDiConstructible" /> never
    ///     admits one, and a composition root is the one place that is SUPPOSED to name a concrete implementation;
    ///     records, exceptions, compiler-generated types and hand-constructed adapters, all dropped by that same
    ///     predicate; and <see cref="HostDependencyRule.DataProtectionNamespace" />.
    /// </remarks>
    [Test]
    public void HostClassConstructors_TakeNoForbiddenDependency()
    {
        var hostClasses = ClientAssembly.GetTypes().Where(IsHostClass).ToList();
        var violations = HostDependencyRule.Scan(hostClasses, EndpointNames);

        AssertEx.True(hostClasses.Count >= HostFloor,
            $"Only {hostClasses.Count} DI-constructed host classes were found outside the endpoints and hubs; the floor "
            + $"is {HostFloor}. A predicate that matches nothing would make this rule vacuous. Found: "
            + string.Join(", ", hostClasses.Select(type => type.Name).Order(StringComparer.Ordinal)));

        AssertEx.Empty(violations, Explain("A class built by DI in the host", violations));
    }

    /// <summary>
    ///     A class under the endpoints' namespace that DI constructs for an endpoint.
    /// </summary>
    private static bool IsEndpointSideCollaborator(Type type) =>
        HostDependencyRule.IsUnder(type.Namespace ?? string.Empty, HostDependencyRule.EndpointsNamespace)
        && !typeof(BaseEndpoint).IsAssignableFrom(type)
        && !typeof(IValidator).IsAssignableFrom(type)
        && HostDependencyRule.IsDiConstructible(type);

    /// <summary>Any other class in the host assembly that DI constructs.</summary>
    private static bool IsHostClass(Type type)
    {
        var space = type.Namespace ?? string.Empty;

        if (HostDependencyRule.IsUnder(space, HostDependencyRule.EndpointsNamespace)
            || HostDependencyRule.IsUnder(space, HostDependencyRule.DataProtectionNamespace)
            || typeof(Hub).IsAssignableFrom(type)
            || typeof(IValidator).IsAssignableFrom(type))
        {
            return false;
        }

        return HostDependencyRule.IsDiConstructible(type);
    }

    private static string Explain(string subject, IReadOnlyList<string> violations) =>
        $"{subject} may take constructor parameters only from Client.Application, AI.Contracts, Providers.Abstractions, "
        + "non-endpoint host types, Microsoft.*, System.* and FastEndpoints.*. There is no exemption list: take the "
        + "dependency below through an application-layer service the type injects instead, or move the type itself into "
        + "Client.Application:"
        + Environment.NewLine + string.Join(Environment.NewLine, violations);
}
