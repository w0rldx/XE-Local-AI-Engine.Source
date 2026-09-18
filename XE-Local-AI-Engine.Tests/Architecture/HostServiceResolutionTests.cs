namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The other half of the host-dependency fence: the services a host class pulls out of an
///     <see cref="IServiceProvider" /> by hand must satisfy the same rule
///     <see cref="EndpointDependencyTests" /> applies to its constructor.
/// </summary>
/// <remarks>
///     <para>
///         A constructor scan cannot see this. A hosted service that takes an <see cref="IServiceScopeFactory" /> and
///         calls <c>scope.ServiceProvider.GetRequiredService&lt;INodeRetentionStore&gt;()</c> has exactly the
///         dependency the rule forbids, expressed as a generic argument in a method body instead of a parameter — and
///         that is not a corner case: it is how three of the host's retention sweepers reached the persistence stores
///         while the constructor fence beside them read clean. Without this scan, "move the store out of the
///         constructor" is a legal way to keep the store.
///     </para>
///     <para>
///         A source scan, for the same reason <see cref="ConfigureAwaitPolicyTests" /> is one: the generic argument
///         survives into IL only as a method-spec token inside a method body, and reading those means a full IL
///         decoder. The text is the cheaper and more honest read. Comments are stripped through
///         <see cref="SourceCommentStripper" />; string literals deliberately are not, matching the house policy that a
///         banned name inside a literal should still trip a guard.
///     </para>
///     <para>
///         Each generic argument is resolved back to a real <see cref="Type" /> and classified by
///         <see cref="HostDependencyRule" />, so this scan and the constructor scan cannot drift. Resolution is by
///         SIMPLE name across the assemblies whose types the rule can forbid, and a name that resolves to several types
///         is a violation if ANY of them is forbidden — conservative on purpose: a scan that lets an ambiguous name
///         through is a scan somebody routes around. A name that resolves to nothing is framework or third-party
///         surface the rule already allows, which is what <see cref="TheGuard_ResolvesTheNamesItClassifies" /> exists
///         to keep honest.
///     </para>
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class HostServiceResolutionTests
{
    /// <summary>
    ///     The calls that name a service type the container will hand back or construct. Each is matched in both
    ///     spellings: the generic one, whose type argument sits between angle brackets, and the reflective one, whose
    ///     type arrives as a <c>typeof(...)</c> argument — not always the FIRST argument, which is why the reflective
    ///     branch reads the whole argument list rather than just what follows the parenthesis.
    /// </summary>
    /// <remarks>
    ///     <c>CreateInstance</c> is matched unqualified, which covers both <c>ActivatorUtilities.CreateInstance</c>
    ///     (the container-backed one this rule is about) and <c>Activator.CreateInstance</c> beside it. Classifying
    ///     both is deliberate: a host class reflectively constructing a persistence store is the same violation
    ///     whichever activator it went through, and narrowing the match to the qualified spelling would be a fence a
    ///     `using static` walks around.
    /// </remarks>
    private static readonly string[] ResolutionMethods =
    [
        "GetRequiredService",
        "GetRequiredKeyedService",
        "GetService",
        "GetKeyedService",
        "CreateInstance"
    ];

    /// <summary>
    ///     Non-vacuity floors, set under the counts measured on 2026-09-18 (603 host files, 7 resolutions) the way
    ///     <see cref="ConfigureAwaitPolicyTests" /> sets its own: a renamed directory reads too few files, and a
    ///     matcher that stopped recognising the call reads no resolutions, and either fails here rather than passing on
    ///     an empty scan.
    /// </summary>
    private const int FileFloor = 500;
    private const int ResolutionFloor = 5;

    [Test]
    public void HostSources_ResolveNoForbiddenServiceFromTheContainer()
    {
        var root = RepositoryPaths.ClientProject();
        AssertEx.True(Directory.Exists(root),
            $"The host project directory '{root}' was not found, so this fence scanned nothing. Skipping is not an "
            + "option here: the rule it enforces has no other enforcement.");

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var files = 0;
        var resolutions = 0;

        foreach (var (path, relative) in ScannedFiles(root))
        {
            files++;
            var stripped = SourceCommentStripper.StripComments(File.ReadAllText(path));

            foreach (var (line, expression) in Resolutions(stripped))
            {
                resolutions++;

                foreach (var forbidden in ForbiddenNamesIn(expression))
                {
                    offenders.Add($"{relative}:{line}  {expression} -> {forbidden}");
                }
            }
        }

        AssertEx.True(files >= FileFloor,
            $"Only {files} host C# files were scanned, below the floor of {FileFloor}. The walk is reading the wrong "
            + "directories, so this fence cannot fire.");
        AssertEx.True(resolutions >= ResolutionFloor,
            $"Only {resolutions} container resolutions were inspected, below the floor of {ResolutionFloor}. The "
            + "matcher has stopped seeing the call, so this fence cannot fire.");

        AssertEx.Empty(offenders,
            "A host class may not resolve a persistence store, a concrete provider's contract, the EF Core API or the "
            + "Docker SDK from the container — the same rule its constructor obeys, and resolving by hand does not "
            + "make the dependency someone else's. There is no exemption list: take the dependency below through an "
            + "application-layer service, or move the resolving type itself into Client.Application:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     The guard's own check. A matcher that stopped recognising the call, or a resolver that stopped finding the
    ///     types it classifies, reads clean source everywhere and reports a fence that cannot fire.
    /// </summary>
    [Test]
    public void TheGuard_ResolvesTheNamesItClassifies()
    {
        (string Case, string Source, string[] Expected)[] mustBeFound =
        [
            ("a plain resolution", "var x = sp.GetRequiredService<INodeRetentionStore>();", ["INodeRetentionStore"]),
            ("a keyed resolution", "sp.GetRequiredKeyedService<NodeChatDbContext>(key);", ["NodeChatDbContext"]),
            ("an optional resolution", "_ = sp.GetService<IScheduledJobRunStore>();", ["IScheduledJobRunStore"]),
            ("a generic argument nested in an allowed one",
                "sp.GetRequiredService<IOptions<NodeChatDbContext>>();", ["IOptions<NodeChatDbContext>"]),
            ("a resolution split over lines",
                "var store = scope.ServiceProvider\n    .GetRequiredService<INodeRetentionStore>();", ["INodeRetentionStore"]),
            ("two on one line",
                "Pair(sp.GetRequiredService<INodeRetentionStore>(), sp.GetService<IScheduledJobRunStore>());",
                ["INodeRetentionStore", "IScheduledJobRunStore"]),
            ("a reflective resolution", "sp.GetRequiredService(typeof(INodeRetentionStore));", ["INodeRetentionStore"]),
            ("a reflective optional resolution", "_ = sp.GetService(typeof(IScheduledJobRunStore));", ["IScheduledJobRunStore"]),
            ("a reflective keyed resolution whose type is not the last argument",
                "sp.GetRequiredKeyedService(typeof(NodeChatDbContext), key);", ["NodeChatDbContext"]),
            ("a generic ActivatorUtilities construction",
                "ActivatorUtilities.CreateInstance<INodeRetentionStore>(sp);", ["INodeRetentionStore"]),
            ("a reflective construction whose type is NOT the first argument",
                "ActivatorUtilities.CreateInstance(sp, typeof(NodeChatDbContext), extra);", ["NodeChatDbContext"]),
            ("a reflective construction against a fully-qualified type",
                "ActivatorUtilities.CreateInstance(sp, typeof(XE_Local_AI_Engine.Client.Persistence.NodeChatDbContext));",
                ["XE_Local_AI_Engine.Client.Persistence.NodeChatDbContext"]),
            ("a reflective call split over lines",
                "var store = scope.ServiceProvider\n    .GetRequiredService(\n        typeof(INodeRetentionStore));",
                ["INodeRetentionStore"])
        ];

        foreach (var (name, source, expected) in mustBeFound)
        {
            var found = Resolutions(SourceCommentStripper.StripComments(source)).Select(hit => hit.Expression).ToArray();
            AssertEx.Equal(expected.Length, found.Length,
                $"The '{name}' case matched {found.Length} resolution(s) instead of {expected.Length}. A miss here is "
                + $"how this fence stops seeing code. Found: {string.Join(", ", found)}");
            AssertEx.True(expected.SequenceEqual(found, StringComparer.Ordinal),
                $"The '{name}' case read the wrong generic argument(s): {string.Join(", ", found)}");
        }

        (string Case, string Source)[] mustNotBeFound =
        [
            ("a comment naming the call", "// Never sp.GetRequiredService<INodeRetentionStore>() from here."),
            ("a doc comment naming the call", "/// <summary>Calls GetRequiredService&lt;T&gt; once.</summary>"),
            ("a less-than comparison", "if (GetRequiredServiceCount < limit) { }"),
            ("a different method that ends in the same word",
                "var handler = registry.GetKeyedServiceDescriptor(key);"),
            ("a reflective call carrying no typeof at all",
                "var service = sp.GetRequiredService(descriptorType);"),
            ("a typeof that is not an argument to one of these calls",
                "_logger = factory.CreateLogger(typeof(NodeChatDbContext));")
        ];

        foreach (var (name, source) in mustNotBeFound)
        {
            AssertEx.Empty(Resolutions(SourceCommentStripper.StripComments(source)).ToList(),
                $"The '{name}' case was read as a container resolution. A guard that fires on prose is one somebody deletes.");
        }

        // The classifier is only as good as the name resolution behind it: if these stop resolving, every offender
        // silently becomes "framework surface the rule allows" and the scan passes on anything.
        AssertEx.NotEmpty(ForbiddenNamesIn("INodeRetentionStore").ToList(),
            "A persistence store no longer resolves to a forbidden type, so this scan would pass on every store.");
        AssertEx.NotEmpty(ForbiddenNamesIn("NodeChatDbContext").ToList(),
            "The EF DbContext no longer resolves to a forbidden type, so this scan would pass on it.");
        AssertEx.NotEmpty(ForbiddenNamesIn("ILlamaServerProcessSupervisor").ToList(),
            "A concrete provider's contract no longer resolves to a forbidden type, so this scan would pass on it.");
        AssertEx.Empty(ForbiddenNamesIn("IScheduledJobManagementService").ToList(),
            "An application-layer service was classified as forbidden, so this scan reports offenders that are not.");
        AssertEx.Empty(ForbiddenNamesIn("ILoggerFactory").ToList(),
            "A framework service was classified as forbidden, so this scan reports offenders that are not.");

        // The reflective spelling goes through the same classifier, so a name written fully qualified inside a
        // typeof must land on the same verdict as the bare one the generic spelling produces.
        AssertEx.NotEmpty(ForbiddenNamesIn("XE_Local_AI_Engine.Client.Persistence.NodeChatDbContext").ToList(),
            "A fully-qualified store name no longer resolves, so every reflective resolution would pass unclassified.");
    }

    // ---------------------------------------------------------------- scanning

    /// <summary>
    ///     Every host source file the rule applies to. The composition root is excluded BY NAME here rather than by
    ///     construction: <c>Program</c>, <c>ConfigureServices</c> and the <c>Add…Extensions</c> classes are where the
    ///     host is supposed to name concrete implementations — that is what composing the object graph IS — and a
    ///     startup routine that migrates the database or seeds a durable job legitimately resolves the store to do it.
    ///     The cost, stated rather than hidden: a <c>Program.*</c> file also holds request-time lambdas — a rate-limiter
    ///     <c>OnRejected</c>, an endpoint filter, a minimal-route handler — and a store resolved inside one of those is
    ///     not composition at all, yet it is outside this scan for as long as it lives in a file named this way. Moving
    ///     such a handler into its own file under <c>Endpoints/</c> or <c>Hosting/</c> is what brings it back in.
    ///     The Data Protection key ring is excluded for the reason recorded on
    ///     <see cref="HostDependencyRule.DataProtectionNamespace" />. Generated sources are regenerated, never
    ///     hand-edited, so the rule cannot be applied to them.
    /// </summary>
    private static IEnumerable<(string Path, string Relative)> ScannedFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');
            var name = Path.GetFileName(path);

            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/Security/DataProtection/", StringComparison.Ordinal)
                || relative.Contains("/DependencyInjection/", StringComparison.Ordinal)
                || relative.EndsWith(".g.cs", StringComparison.Ordinal)
                || name.StartsWith("Program.", StringComparison.Ordinal)
                || string.Equals(name, "ConfigureServices.cs", StringComparison.Ordinal)
                || (name.StartsWith("Add", StringComparison.Ordinal) && name.EndsWith("Extensions.cs", StringComparison.Ordinal)))
            {
                continue;
            }

            yield return (path, relative);
        }
    }

    /// <summary>
    ///     Every container resolution in comment-stripped source, as the one-based line the call starts on and the type
    ///     expression it names. Brackets and parentheses are matched by depth, so a nested generic argument comes back
    ///     whole; an unterminated one is skipped rather than swallowing the rest of the file. A reflective call yields
    ///     one hit per <c>typeof(...)</c> in its argument list, so <c>CreateInstance(sp, typeof(X), y)</c> is read even
    ///     though the type is not the first argument.
    /// </summary>
    private static IEnumerable<(int Line, string Expression)> Resolutions(string stripped)
    {
        var index = 0;
        while (index < stripped.Length)
        {
            var method = ResolutionMethods.FirstOrDefault(candidate => StartsCallAt(stripped, index, candidate));
            if (method is null)
            {
                index++;
                continue;
            }

            var open = index + method.Length;
            var close = MatchingClose(stripped, open);
            if (close < 0)
            {
                index++;
                continue;
            }

            var line = Line(stripped, index);
            if (stripped[open] == '<')
            {
                var expression = stripped[(open + 1)..close].Trim();
                if (expression.Length > 0)
                {
                    yield return (line, expression);
                }
            }
            else
            {
                foreach (var expression in TypeofArguments(stripped[(open + 1)..close]))
                {
                    yield return (line, expression);
                }
            }

            index = close + 1;
        }
    }

    /// <summary>
    ///     Whether <paramref name="method" /> starts a real call at <paramref name="index" />: preceded by a
    ///     non-identifier character (a <c>.</c> counts, so a qualified spelling matches the same token) and followed
    ///     immediately by <c>&lt;</c> or <c>(</c>. That second half is what keeps <c>GetKeyedServiceDescriptor</c> out.
    /// </summary>
    private static bool StartsCallAt(string text, int index, string method)
    {
        if (index + method.Length >= text.Length
            || string.CompareOrdinal(text, index, method, 0, method.Length) != 0
            || (index > 0 && IsWordCharacter(text[index - 1])))
        {
            return false;
        }

        return text[index + method.Length] is '<' or '(';
    }

    /// <summary>
    ///     The index of the bracket closing the one at <paramref name="open" />, or <c>-1</c> when it never closes.
    ///     Angle brackets and parentheses are counted separately, so neither kind can balance the other.
    /// </summary>
    private static int MatchingClose(string text, int open)
    {
        var (opener, closer) = text[open] == '<' ? ('<', '>') : ('(', ')');
        var depth = 0;

        for (var scan = open; scan < text.Length; scan++)
        {
            if (text[scan] == opener)
            {
                depth++;
            }
            else if (text[scan] == closer)
            {
                depth--;
                if (depth == 0)
                {
                    return scan;
                }
            }
        }

        return -1;
    }

    /// <summary>
    ///     The type expression inside every <c>typeof(...)</c> in one argument list, at any nesting depth — a lambda
    ///     argument that names a type is read too, which errs toward scanning more.
    /// </summary>
    private static IEnumerable<string> TypeofArguments(string arguments)
    {
        const string Keyword = "typeof";

        var index = 0;
        while (index < arguments.Length)
        {
            if (!StartsCallAt(arguments, index, Keyword) || arguments[index + Keyword.Length] != '(')
            {
                index++;
                continue;
            }

            var open = index + Keyword.Length;
            var close = MatchingClose(arguments, open);
            if (close < 0)
            {
                index++;
                continue;
            }

            var expression = arguments[(open + 1)..close].Trim();
            if (expression.Length > 0)
            {
                yield return expression;
            }

            index = close + 1;
        }
    }

    private static int Line(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    // ---------------------------------------------------------------- classification

    /// <summary>
    ///     Assemblies whose types the rule can forbid. Everything else a host file could name is framework or
    ///     third-party surface the rule already allows, so a name that resolves in none of these is not a violation.
    /// </summary>
    private static readonly IReadOnlyList<Assembly> ClassifiableAssemblies =
    [
        .. new[]
           {
               typeof(WorkerHealthCheck).Assembly,
               typeof(XE_Local_AI_Engine.Client.Services.ModelFit.LlamaCppRuntimeOrchestrationService).Assembly,
               typeof(XE_Local_AI_Engine.Client.Persistence.NodeChatDbContext).Assembly,
               typeof(XE_Local_AI_Engine.Providers.Abstractions.Gguf.IGgufModelStore).Assembly,
               typeof(XE_Local_AI_Engine.Providers.LlamaServer.Contracts.IInstalledRuntimeStore).Assembly,
               typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly
           }
           .Concat(AppDomain.CurrentDomain.GetAssemblies()
                            .Where(assembly => (assembly.GetName().Name ?? string.Empty)
                                       .StartsWith("XE-Local-AI-Engine", StringComparison.Ordinal)))
           .Distinct()
    ];

    /// <summary>Simple type name to every type carrying it across <see cref="ClassifiableAssemblies" />.</summary>
    private static readonly Dictionary<string, List<Type>> TypesBySimpleName = BuildNameIndex();

    /// <summary>
    ///     Endpoint names are the peer set the constructor scan uses; the source scan does not need one (a host file
    ///     resolving an endpoint from the container is a different smell, and FastEndpoints owns endpoint activation),
    ///     so it passes an empty set rather than pretending to check for it.
    /// </summary>
    private static readonly HashSet<string> NoPeerSet = new(StringComparer.Ordinal);

    /// <summary>
    ///     The forbidden types named by one generic-argument expression: the outer name plus, recursively, every
    ///     nested generic argument, so <c>IOptions&lt;SomeStore&gt;</c> is classified on <c>SomeStore</c>.
    /// </summary>
    private static IEnumerable<string> ForbiddenNamesIn(string expression)
    {
        foreach (var name in SimpleNames(expression))
        {
            if (!TypesBySimpleName.TryGetValue(name, out var candidates))
            {
                continue;
            }

            var forbidden = candidates.Where(candidate => HostDependencyRule.IsForbidden(candidate, NoPeerSet))
                                      .Select(HostDependencyRule.NameOf)
                                      .Order(StringComparer.Ordinal)
                                      .FirstOrDefault();

            if (forbidden is not null)
            {
                yield return forbidden;
            }
        }
    }

    /// <summary>
    ///     Splits a generic-argument expression into the simple type names it mentions, dropping namespace
    ///     qualifiers, array ranks, nullable marks and whitespace. <c>?</c> is dropped rather than resolved: the
    ///     underlying type is the dependency either way.
    /// </summary>
    private static IEnumerable<string> SimpleNames(string expression)
    {
        foreach (var token in expression.Split(['<', '>', ',', '[', ']', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = token.TrimEnd('?');
            var lastDot = name.LastIndexOf('.');
            if (lastDot >= 0)
            {
                name = name[(lastDot + 1)..];
            }

            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    private static Dictionary<string, List<Type>> BuildNameIndex()
    {
        var index = new Dictionary<string, List<Type>>(StringComparer.Ordinal);

        foreach (var assembly in ClassifiableAssemblies)
        {
            foreach (var type in SafeTypes(assembly))
            {
                // The CLR's arity suffix is not what a source file writes, so the index is keyed on the bare name.
                var name = type.Name;
                var tick = name.IndexOf('`', StringComparison.Ordinal);
                if (tick >= 0)
                {
                    name = name[..tick];
                }

                if (!index.TryGetValue(name, out var bucket))
                {
                    bucket = [];
                    index[name] = bucket;
                }

                bucket.Add(type);
            }
        }

        return index;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Select(type => type!);
        }
    }
}
