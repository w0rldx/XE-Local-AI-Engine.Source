namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using System.Runtime.CompilerServices;
using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins WHAT the host's request edge is allowed to take as a constructor dependency: a FastEndpoints endpoint, a
///     SignalR hub, and the DI-constructed collaborators that live beside the endpoints and are only reached from
///     them.
///     <para>
///         The edge is the HTTP and WebSocket boundary of the host. It may compose the application layer, the shared
///         contracts and framework services; it may not reach past them into a store, a concrete provider's
///         implementation surface, the EF Core API, the Docker SDK, or a raw process / socket / file handle. Those are
///         decisions the application layer owns, and an edge type that makes them itself is a second, undocumented copy
///         of a service.
///     </para>
///     <para>
///         Background services and <c>Services/Proxy/LocalModelProxyForwarder</c> are deliberately outside this rule
///         (operator decision): neither is a request handler, and the forwarder's whole job is to hold the transport an
///         endpoint may not.
///     </para>
///     <para>
///         Reflection over the compiled host rather than a source scan or an ArchUnitNET/NetArchTest rule: the rule is
///         per-constructor-PARAMETER and inspects each parameter's generic arguments recursively, which neither rule
///         engine expresses. A bespoke loop over <see cref="ConstructorInfo" /> is the smallest mechanism that can, and
///         it reads the same IL the host actually runs.
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
///         moving the dependency into a <c>Client.Application</c> service the edge type takes instead.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class EndpointDependencyTests
{
    private const string ClientNamespace = "XE_Local_AI_Engine.Client";
    private const string EndpointsNamespace = ClientNamespace + ".Endpoints";
    private const string PersistenceNamespace = "XE_Local_AI_Engine.Client.Persistence";
    private const string ProvidersNamespace = "XE_Local_AI_Engine.Providers";
    private const string AbstractionsNamespace = "XE_Local_AI_Engine.Providers.Abstractions";

    private static readonly Assembly ClientAssembly = typeof(WorkerHealthCheck).Assembly;

    /// <summary>Every FastEndpoints endpoint in the host: one scan's subject, and every scan's peer set.</summary>
    private static readonly IReadOnlyList<Type> EndpointTypes =
    [
        .. ClientAssembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(BaseEndpoint).IsAssignableFrom(type))
    ];

    private static readonly HashSet<string> EndpointNames =
        EndpointTypes.Select(type => type.FullName ?? type.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Namespace roots an endpoint constructor may take a parameter from. Anything outside this set is a
    ///     violation, which is what makes the rule a fence rather than a blacklist: a new third-party SDK injected
    ///     straight into an endpoint fails without anyone having remembered to ban it.
    /// </summary>
    private static readonly string[] AllowedNamespaces =
    [
        "XE_Local_AI_Engine.Client.Application",
        "XE_Local_AI_Engine.AI.Contracts",
        AbstractionsNamespace,
        ClientNamespace,
        "Microsoft",
        "System",
        "FastEndpoints"
    ];

    /// <summary>
    ///     The roots that are forbidden even though an allowed root is a prefix of them.
    ///     <c>XE_Local_AI_Engine.Client.Persistence</c> sits under the host's own root; <c>Microsoft.EntityFrameworkCore</c>
    ///     under <c>Microsoft</c>. Order matters: forbidden wins over allowed.
    /// </summary>
    private static readonly string[] ForbiddenNamespaces =
    [
        PersistenceNamespace,
        "Microsoft.EntityFrameworkCore",
        "Docker.DotNet"
    ];

    /// <summary>
    ///     Types that are forbidden by name rather than by namespace — the rest of <c>System.Diagnostics</c>,
    ///     <c>System.Net.Http</c> and <c>System.IO</c> is ordinary framework surface.
    /// </summary>
    private static readonly string[] ForbiddenTypes =
    [
        "System.Diagnostics.Process",
        "System.IO.File",
        "System.Net.Http.HttpClient"
    ];

    // Non-vacuity floors for the three scans below, set UNDER the counts measured on 2026-09-17 (446 endpoint types,
    // 19 hub types, 2 DI-constructed classes under Endpoints/) so a planned removal is not brittle. A floor equal to
    // its measurement is not rounded down: retiring one hub would turn a real regression into a red herring about the
    // floor. Each floor only has to catch a scan that found nothing or nearly nothing; the EXACT hub inventory is
    // pinned elsewhere, by EndpointAuthorizationPolicyTests, which is where a new hub is meant to be noticed.
    private const int EndpointFloor = 400;
    private const int HubFloor = 15;

    /// <summary>
    ///     The collaborator scan counts the classes under the host's <c>Endpoints</c> namespace that DI builds and an
    ///     endpoint reaches, and deliberately not the DTOs, requests, responses, validators, size-limit metadata or
    ///     static mappers beside them — none of those takes a service. As measured there are two:
    ///     <c>DevWorkflowRunComposer</c> and <c>LocalApiSecurityMiddleware</c>.
    /// </summary>
    private const int CollaboratorFloor = 1;

    [Test]
    public void EndpointConstructors_TakeNoForbiddenDependency()
    {
        var violations = Scan(EndpointTypes);

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
        var hubs = ClientAssembly.GetTypes()
                                 .Where(type => type.IsClass && !type.IsAbstract && typeof(Hub).IsAssignableFrom(type))
                                 .ToList();
        var violations = Scan(hubs);

        AssertEx.True(hubs.Count >= HubFloor,
            $"Only {hubs.Count} hub types were discovered; the floor is {HubFloor}. A scan that finds no hubs proves nothing.");

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
        var violations = Scan(collaborators);

        AssertEx.True(collaborators.Count >= CollaboratorFloor,
            $"Only {collaborators.Count} DI-constructed classes were found under {EndpointsNamespace}; the floor is "
            + $"{CollaboratorFloor}. A predicate that matches nothing would make this rule vacuous. Found: "
            + string.Join(", ", collaborators.Select(type => type.Name).Order(StringComparer.Ordinal)));

        AssertEx.Empty(violations, Explain("A class built by DI under Endpoints/", violations));
    }

    /// <summary>
    ///     A class under the endpoints' namespace that DI constructs for an endpoint: instantiable, not an endpoint or
    ///     a validator, not a record (every DTO, request and response here is one), and taking at least one
    ///     service-shaped constructor parameter — an interface or a non-record class. That last clause is what keeps
    ///     the hand-written request classes, which are property bags with a parameterless constructor, out of the set.
    /// </summary>
    private static bool IsEndpointSideCollaborator(Type type)
    {
        if (!type.IsClass || type.IsAbstract || !IsUnder(type.Namespace ?? string.Empty, EndpointsNamespace))
        {
            return false;
        }

        // CompilerGeneratedAttribute is what drops the closures and async state machines the compiler nests inside
        // these types; a hand-written nested helper stays in scope, because DI would build that one too.
        if (typeof(BaseEndpoint).IsAssignableFrom(type)
            || typeof(IValidator).IsAssignableFrom(type)
            || type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
            || (type.DeclaringType?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
            || IsRecord(type))
        {
            return false;
        }

        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                   .Any(constructor => constructor.GetParameters()
                                                  .Any(parameter => parameter.ParameterType is { IsInterface: true }
                                                                    || (parameter.ParameterType.IsClass && !IsRecord(parameter.ParameterType))));
    }

    /// <summary>
    ///     Whether the compiler emitted a record's copy constructor for this type. <c>&lt;Clone&gt;$</c> is the only
    ///     reflection-visible mark a record carries, and <c>IEquatable&lt;T&gt;</c> alone would also catch the
    ///     hand-written value types beside them.
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;

    private static string Explain(string subject, IReadOnlyList<string> violations) =>
        $"{subject} may take constructor parameters only from Client.Application, AI.Contracts, Providers.Abstractions, "
        + "non-endpoint host types, Microsoft.*, System.* and FastEndpoints.*. There is no exemption list: take the "
        + "dependency below through an application-layer service the type injects instead:"
        + Environment.NewLine + string.Join(Environment.NewLine, violations);

    /// <summary>
    ///     One pass over a set of edge types. Endpoint names are the peer set for every scan, so no scanned type may
    ///     take another route's handler; hubs are deliberately NOT peers, because an endpoint pushing through
    ///     <c>IHubContext&lt;TFooHub&gt;</c> names a hub legitimately.
    /// </summary>
    private static IReadOnlyList<string> Scan(IReadOnlyList<Type> types)
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var edge in types)
        {
            foreach (var constructor in edge.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    foreach (var dependency in Flatten(parameter.ParameterType))
                    {
                        // A type naming itself is not a dependency on itself: everything that logs takes an
                        // ILogger<TSelf>, where the type argument is a logging CATEGORY, not something resolved and
                        // called. Skipping the self-reference keeps the "an endpoint may not take ANOTHER endpoint"
                        // half of the host-namespace rule intact.
                        if (dependency != edge && IsForbidden(dependency, EndpointNames))
                        {
                            violations.Add($"{edge.FullName}|{NameOf(dependency)}");
                        }
                    }
                }
            }
        }

        return [.. violations];
    }

    /// <summary>
    ///     Every type a parameter declaration actually depends on: the declared type plus, recursively, its generic
    ///     arguments, its array/by-ref element type and <c>Nullable&lt;T&gt;</c>'s underlying type. Open generic
    ///     parameters are skipped — they name no concrete dependency.
    /// </summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.IsGenericParameter)
        {
            yield break;
        }

        if (type.HasElementType)
        {
            var element = type.GetElementType();
            if (element is not null)
            {
                foreach (var nested in Flatten(element))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        yield return type;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }

    private static bool IsForbidden(Type type, IReadOnlySet<string> endpointNames)
    {
        var name = NameOf(type);
        if (ForbiddenTypes.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        var space = type.Namespace ?? string.Empty;
        if (ForbiddenNamespaces.Any(forbidden => IsUnder(space, forbidden)))
        {
            return true;
        }

        // Every concrete provider is forbidden; Providers.Abstractions is the one seam the host may bind to.
        if (IsUnder(space, ProvidersNamespace) && !IsUnder(space, AbstractionsNamespace))
        {
            return true;
        }

        // A host type is composition surface — unless it is itself an endpoint, which would make one route's
        // handler a dependency of another's.
        if (IsUnder(space, ClientNamespace))
        {
            return endpointNames.Contains(name);
        }

        return !AllowedNamespaces.Any(allowed => IsUnder(space, allowed));
    }

    /// <summary>
    ///     Namespace containment on segment boundaries. A plain <c>StartsWith</c> would put
    ///     <c>…Providers.OpenAICompatible.Core</c> under <c>…Providers.OpenAICompat</c>.
    /// </summary>
    private static bool IsUnder(string space, string root) =>
        string.Equals(space, root, StringComparison.Ordinal) || space.StartsWith(root + ".", StringComparison.Ordinal);

    /// <summary>
    ///     The stable key half for a dependency: the namespace-qualified name with the CLR's generic arity suffix and
    ///     any nested-type <c>+</c> left as-is, so the key is greppable and unambiguous.
    /// </summary>
    private static string NameOf(Type type)
    {
        // A constructed generic's FullName carries the assembly-qualified argument list; the open definition's name
        // is what a human greps for, and the arguments are visited separately by Flatten anyway.
        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.FullName ?? (definition.Namespace is null ? definition.Name : $"{definition.Namespace}.{definition.Name}");
    }
}
