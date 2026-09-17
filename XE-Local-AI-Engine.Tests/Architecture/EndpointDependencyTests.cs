namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using FastEndpoints;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins WHAT a FastEndpoints endpoint in the host is allowed to take as a constructor dependency.
///     <para>
///         An endpoint is the HTTP edge of the host. It may compose the application layer, the shared contracts and
///         framework services; it may not reach past them into a store, a concrete provider's implementation surface,
///         the EF Core API, the Docker SDK, or a raw process / socket / file handle. Those are decisions the
///         application layer owns, and an endpoint that makes them itself is a second, undocumented copy of a service.
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
///         The rule holds for every endpoint in the host and has no exemption list. It once carried one, frozen at the
///         violations that existed when the rule was written; each was migrated behind an application-layer service
///         until the list was empty, and it was then deleted. None may be reintroduced: a new violation is fixed by
///         moving the dependency into a <c>Client.Application</c> service the endpoint takes instead.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class EndpointDependencyTests
{
    private const string ClientNamespace = "XE_Local_AI_Engine.Client";
    private const string PersistenceNamespace = "XE_Local_AI_Engine.Client.Persistence";
    private const string ProvidersNamespace = "XE_Local_AI_Engine.Providers";
    private const string AbstractionsNamespace = "XE_Local_AI_Engine.Providers.Abstractions";

    private static readonly Assembly ClientAssembly = typeof(WorkerHealthCheck).Assembly;

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

    [Test]
    public void EndpointConstructors_TakeNoForbiddenDependency()
    {
        var (violations, endpointCount) = Scan();

        AssertEx.True(endpointCount >= 400,
            $"Expected the host's known endpoint surface; found {endpointCount} endpoint types. The assembly marker or the reflection scan is broken.");

        AssertEx.Empty(violations,
            "An endpoint may take constructor parameters only from Client.Application, AI.Contracts, "
            + "Providers.Abstractions, non-endpoint host types, Microsoft.*, System.* and FastEndpoints.*. There is no "
            + "exemption list: take the dependency below through an application-layer service the endpoint injects "
            + "instead:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static (IReadOnlyList<string> Violations, int EndpointCount) Scan()
    {
        var endpointTypes = ClientAssembly.GetTypes()
                                          .Where(type => type.IsClass && !type.IsAbstract && typeof(BaseEndpoint).IsAssignableFrom(type))
                                          .ToList();
        var endpointNames = endpointTypes.Select(type => type.FullName ?? type.Name).ToHashSet(StringComparer.Ordinal);

        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpointTypes)
        {
            foreach (var constructor in endpoint.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    foreach (var dependency in Flatten(parameter.ParameterType))
                    {
                        // An endpoint naming itself is not a dependency on itself: every endpoint that logs takes an
                        // ILogger<TSelf>, where the type argument is a logging CATEGORY, not something resolved and
                        // called. Skipping the self-reference keeps the "an endpoint may not take ANOTHER endpoint"
                        // half of the host-namespace rule intact.
                        if (dependency != endpoint && IsForbidden(dependency, endpointNames))
                        {
                            violations.Add($"{endpoint.FullName}|{NameOf(dependency)}");
                        }
                    }
                }
            }
        }

        return (violations.ToList(), endpointTypes.Count);
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
