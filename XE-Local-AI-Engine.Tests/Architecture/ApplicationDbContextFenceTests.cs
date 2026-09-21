namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using System.Runtime.CompilerServices;
using NetArchTest.Rules;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Operator decision D5: no <c>DbContext</c> in the application layer, bar a reasoned allowlist entry.</summary>
/// <remarks>
///     Type-based, not a text scan: the measurement behind this fence found files naming the context only through a
///     lowercase <c>dbContext</c> lambda parameter, and most holders resolve it in a method body. NetArchTest reads IL
///     through Mono.Cecil, so a body reference, a generic argument and a lambda parameter all count — proved by
///     <see cref="TheFence_SeesTheShapesThatHideFromAText" /> rather than assumed. The allowlist is shrink-only in
///     both directions, like the two guards beside it. Rule: <c>docs/wiki/16-code-conventions.md</c>.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ApplicationDbContextFenceTests
{
    /// <summary>Sections of the allowlist. The token is the second field of every entry.</summary>
    private const string PermanentSection = "permanent";

    private const string MigrateSection = "migrate";

    /// <summary>
    ///     A non-vacuity floor under today's count: a marker type that starts resolving to a different assembly would
    ///     otherwise let the rule below assert over an empty type set and report green for the wrong reason.
    /// </summary>
    private const int ApplicationTypeFloor = 2000;

    private static readonly Assembly ApplicationAssembly = typeof(RuntimePackageValidationResult).Assembly;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "DbContextUserAllowlist.txt");

    /// <summary>The two node contexts, by full type name.</summary>
    /// <remarks>
    ///     NetArchTest matches a dependency by name PREFIX, so <c>NodeChatDbContext</c> also covers
    ///     <c>NodeChatDbContextFactory</c> and any <c>IDbContextFactory&lt;NodeChatDbContext&gt;</c> instantiation —
    ///     the same reach, and none of them belongs in the application layer unlisted.
    /// </remarks>
    private static readonly string[] ForbiddenDependencies =
    [
        typeof(NodeChatDbContext).FullName!,
        typeof(NodeIdentityDbContext).FullName!
    ];

    /// <summary>The scan is pure and both rules need all of it, so one walk serves them.</summary>
    private static readonly Lazy<IReadOnlyList<string>> Holders = new(Scan, isThreadSafe: true);

    [Test]
    public void ApplicationLayer_HoldsNoUnreasonedDbContext()
    {
        var scanned = Types.InAssembly(ApplicationAssembly).GetTypes().Count();

        AssertEx.True(scanned >= ApplicationTypeFloor,
            $"Scanned {scanned} types in '{ApplicationAssembly.GetName().Name}', below the non-vacuity floor of "
            + $"{ApplicationTypeFloor}. The fence would assert over an empty type set.");

        var allowed = Allowlist().Keys.ToHashSet(StringComparer.Ordinal);
        var offenders = Holders.Value.Where(holder => !allowed.Contains(holder)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(offenders,
            "An application-layer type must not depend on a node DbContext (operator decision D5): move the query "
            + "behind a *Store in XE-Local-AI-Engine.Client.Persistence. If the type is composition, migration "
            + "bootstrap or a documented maintenance job, add it to Architecture/DbContextUserAllowlist.txt with its "
            + "reason. Holders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     The shrink-only half. Without it the list keeps every reason ever written down, and the next reader cannot
    ///     tell a live exemption from one whose type was migrated three slices ago.
    /// </summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var holders = Holders.Value.ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist().Keys.Where(entry => !holders.Contains(entry)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(stale,
            "Architecture/DbContextUserAllowlist.txt reasons away a type that no longer depends on a node DbContext. "
            + "The list is shrink-only: delete the line(s) below in the commit that migrated the type."
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    public void TheAllowlist_NamesASectionAndAReasonForEveryEntry()
    {
        var malformed = new List<string>();

        foreach (var line in EntryLines())
        {
            var fields = line.Split('|');

            if (fields.Length != 3
                || fields[0].Trim().Length == 0
                || fields[2].Trim().Length == 0
                || (fields[1] is not PermanentSection and not MigrateSection))
            {
                malformed.Add(line);
            }
        }

        AssertEx.Empty(malformed,
            $"Every allowlist entry is '<type full name>|{PermanentSection}|<reason>' or "
            + $"'<type full name>|{MigrateSection}|<reason>'. A section token keeps a documented permanent holder "
            + "from being read as one a later sub-slice still has to migrate, and the reason is what the next reader "
            + "needs. Malformed:"
            + Environment.NewLine + string.Join(Environment.NewLine, malformed));
    }

    /// <summary>The fence's own check: it asserts what the mechanism sees, one live holder per hiding shape.</summary>
    /// <remarks>
    ///     A constructor parameter, an async body resolution (IL in a state machine), a lambda parameter (IL in a
    ///     display class), a static method parameter and a generic argument, each anchored on a type that really
    ///     holds a context. Four shapes have no live holder and are proved by scaffolding in the slice's
    ///     break-proof log instead: a factory generic, a bare field, an implemented interface, and a derived type
    ///     that names nothing — that last one caught only because <see cref="Scan" /> walks the base chain.
    /// </remarks>
    [Test]
    public void TheFence_SeesTheShapesThatHideFromAText()
    {
        var holders = Holders.Value.ToHashSet(StringComparer.Ordinal);

        foreach (var (shape, typeName) in ShapesThatMustBeSeen)
        {
            AssertEx.Contains(holders, typeName,
                $"The fence did not report '{typeName}', which holds a node DbContext as {shape}. A miss here is how "
                + "this fence stops seeing code.");
        }
    }

    /// <summary>One live holder per hiding shape the fence has to see. Each is verified by reading the named type.</summary>
    private static readonly (string Shape, string TypeName)[] ShapesThatMustBeSeen =
    [
        ("a constructor parameter", "XE_Local_AI_Engine.Client.Services.Knowledge.FtsSearch"),
        ("a GetRequiredService<T> call inside an async method body",
            "XE_Local_AI_Engine.Client.Services.Chat.NodeChatPersistenceWriter"),
        ("a lambda parameter of the context type",
            "XE_Local_AI_Engine.Client.Services.Chat.Implementation.NodeChatReadModel"),
        ("a static method parameter",
            "XE_Local_AI_Engine.Client.Services.Chat.Implementation.NodeChatPersistenceSql"),
        ("a generic type argument (AddDbContext<T>)",
            "XE_Local_AI_Engine.Client.DependencyInjection.Modules.AddNodeModelRuntimeExtensions")
    ];

    // ---------------------------------------------------------------- scanning

    /// <summary>
    ///     Every application-layer type that depends on a node context, folded onto its declaring type.
    /// </summary>
    /// <remarks>
    ///     A closure and an async state machine are compiler-generated nested types with unspeakable names; listing
    ///     one would pin the C# compiler's naming rather than the code, so each folds up to the type a reader can go
    ///     and edit.
    /// </remarks>
    private static IReadOnlyList<string> Scan()
    {
        var result = Types.InAssembly(ApplicationAssembly)
                          .ShouldNot()
                          .HaveDependencyOnAny(ForbiddenDependencies)
                          .GetResult();

        var holders = (result.FailingTypes ?? [])
                      .Select(DeclaringKey)
                      .ToHashSet(StringComparer.Ordinal);

        // Inheriting a context-typed member is holding one, but the IL of a type that adds nothing names only its
        // base, so NetArchTest reports the base alone. Walking the chain closes that at any depth.
        foreach (var type in Types.InAssembly(ApplicationAssembly).GetTypes())
        {
            for (var ancestor = type.BaseType; ancestor is not null; ancestor = ancestor.BaseType)
            {
                if (holders.Contains(DeclaringKey(ancestor)))
                {
                    _ = holders.Add(DeclaringKey(type));
                    break;
                }
            }
        }

        return holders.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Folds a compiler-generated nested type up to the declaring type a reader can edit.</summary>
    private static string DeclaringKey(Type type)
    {
        var current = type;

        while (current.DeclaringType is { } declaring && current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            current = declaring;
        }

        return (current.FullName ?? current.Name).Replace('+', '.');
    }

    // ---------------------------------------------------------------- the allowlist

    /// <summary>
    ///     The reasoned exemptions keyed by type full name, read from the repository rather than from the test output
    ///     directory: no <c>CopyToOutputDirectory</c> to keep in step.
    /// </summary>
    private static Dictionary<string, string> Allowlist()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in EntryLines())
        {
            var fields = line.Split('|');
            entries[fields[0].Trim()] = fields.Length > 1 ? fields[1] : string.Empty;
        }

        return entries;
    }

    private static IEnumerable<string> EntryLines()
    {
        return File.ReadLines(AllowlistPath)
                   .Select(raw => raw.Trim())
                   .Where(line => line.Length > 0 && !line.StartsWith('#'));
    }
}
