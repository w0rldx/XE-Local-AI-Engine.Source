namespace XE_Local_AI_Engine.Tests.Architecture.Support;

using System.Reflection;
using System.Runtime.CompilerServices;

/// <summary>
///     The one classifier behind the host-dependency fence: given a type a host class depends on, is that dependency
///     allowed? <see cref="Architecture.EndpointDependencyTests" /> applies it to the request edge and to the rest of
///     the host's DI-constructed classes; <see cref="Architecture.HostServiceResolutionTests" /> applies it to the
///     service types those same classes pull out of an <see cref="IServiceProvider" /> by hand.
///     <para>
///         Shared rather than duplicated because the two scans must never disagree: a rule that forbids a store in a
///         constructor while allowing the same store through <c>GetRequiredService&lt;T&gt;</c> is not a rule, it is a
///         hint. Everything specific to a scan — which types it reads, its non-vacuity floor, its failure message —
///         stays with that scan.
///     </para>
/// </summary>
internal static class HostDependencyRule
{
    internal const string ClientNamespace = "XE_Local_AI_Engine.Client";
    internal const string EndpointsNamespace = ClientNamespace + ".Endpoints";
    internal const string PersistenceNamespace = "XE_Local_AI_Engine.Client.Persistence";
    internal const string ProvidersNamespace = "XE_Local_AI_Engine.Providers";
    internal const string AbstractionsNamespace = "XE_Local_AI_Engine.Providers.Abstractions";

    /// <summary>
    ///     The ASP.NET Core Data Protection key-ring types. They are the one part of the host this fence does not
    ///     reach, and the reason is durable data rather than taste: Data Protection writes the DECRYPTOR'S
    ///     assembly-qualified type name into every encrypted key-ring element and activates it by that name on read, so
    ///     the type's identity is persisted on disk in every existing installation. Moving the pair behind an
    ///     application-layer service would make every key ring already written unreadable, which locks every encrypted
    ///     column on an upgrading node. They take <c>Client.Persistence.Cryptography</c>'s AEAD primitive directly and
    ///     always will. This is a namespace, not a list of types: a new key-ring participant belongs here, and anything
    ///     else that lands in this namespace is still outside the fence, which is the cost of the carve-out.
    /// </summary>
    internal const string DataProtectionNamespace = ClientNamespace + ".Security.DataProtection";

    /// <summary>
    ///     Namespace roots a host constructor may take a parameter from. Anything outside this set is a violation,
    ///     which is what makes the rule a fence rather than a blacklist: a new third-party SDK injected straight into a
    ///     host class fails without anyone having remembered to ban it.
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

    internal static bool IsForbidden(Type type, IReadOnlySet<string> endpointNames)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(endpointNames);

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
    ///     Every type a parameter declaration actually depends on: the declared type plus, recursively, its generic
    ///     arguments, its array/by-ref element type and <c>Nullable&lt;T&gt;</c>'s underlying type. Open generic
    ///     parameters are skipped — they name no concrete dependency.
    /// </summary>
    internal static IEnumerable<Type> Flatten(Type type)
    {
        // No argument guard: an iterator method would defer it to the first MoveNext (S4456), and every caller here
        // passes a parameter type reflection just handed it.
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

    /// <summary>
    ///     One pass over a set of host types. Endpoint names are the peer set for every scan, so no scanned type may
    ///     take another route's handler; hubs are deliberately NOT peers, because a type pushing through
    ///     <c>IHubContext&lt;TFooHub&gt;</c> names a hub legitimately.
    /// </summary>
    internal static IReadOnlyList<string> Scan(IReadOnlyList<Type> types, IReadOnlySet<string> endpointNames)
    {
        ArgumentNullException.ThrowIfNull(types);

        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var subject in types)
        {
            foreach (var constructor in subject.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    foreach (var dependency in Flatten(parameter.ParameterType))
                    {
                        // A type naming itself is not a dependency on itself: everything that logs takes an
                        // ILogger<TSelf>, where the type argument is a logging CATEGORY, not something resolved and
                        // called. Skipping the self-reference keeps the "a host type may not take an ENDPOINT" half of
                        // the host-namespace rule intact.
                        if (dependency != subject && IsForbidden(dependency, endpointNames))
                        {
                            violations.Add($"{subject.FullName}|{NameOf(dependency)}");
                        }
                    }
                }
            }
        }

        return [.. violations];
    }

    /// <summary>
    ///     A class DI can build: instantiable, not a record (every DTO, request and response is one), not an exception
    ///     (its <c>(string, Exception)</c> constructor is not a service dependency), not compiler-generated, nameable by
    ///     a registration, and taking at least one service-shaped constructor parameter — an interface or a non-record
    ///     class. That last clause is what keeps the hand-written property-bag classes, which have a parameterless
    ///     constructor, out of the set.
    /// </summary>
    /// <remarks>
    ///     Only PUBLIC instance constructors count, and a private nested type is excluded: neither can be named by a
    ///     registration outside its declaring type, so neither is something the container builds. That is what keeps
    ///     the hand-constructed adapters — <c>VelopackUpdateManager</c> over Velopack's own <c>UpdateManager</c>, the
    ///     launcher's <c>IProcessImpl</c> shim — out of a fence about what DI is allowed to inject.
    /// </remarks>
    internal static bool IsDiConstructible(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!type.IsClass || type.IsAbstract || type.IsNestedPrivate || typeof(Exception).IsAssignableFrom(type))
        {
            return false;
        }

        // CompilerGeneratedAttribute is what drops the closures and async state machines the compiler nests inside
        // these types; a hand-written nested helper stays in scope, because DI would build that one too.
        if (type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
            || type.DeclaringType?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
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
    internal static bool IsRecord(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;
    }

    /// <summary>
    ///     Namespace containment on segment boundaries. A plain <c>StartsWith</c> would put
    ///     <c>…Providers.OpenAICompatible.Core</c> under <c>…Providers.OpenAICompat</c>.
    /// </summary>
    internal static bool IsUnder(string space, string root) =>
        string.Equals(space, root, StringComparison.Ordinal) || space.StartsWith(root + ".", StringComparison.Ordinal);

    /// <summary>
    ///     The stable key half for a dependency: the namespace-qualified name with the CLR's generic arity suffix and
    ///     any nested-type <c>+</c> left as-is, so the key is greppable and unambiguous.
    /// </summary>
    internal static string NameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // A constructed generic's FullName carries the assembly-qualified argument list; the open definition's name
        // is what a human greps for, and the arguments are visited separately by Flatten anyway.
        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.FullName ?? (definition.Namespace is null ? definition.Name : $"{definition.Namespace}.{definition.Name}");
    }
}
