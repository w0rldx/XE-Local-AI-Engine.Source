namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Holds the <c>Try*</c> shape of wiki 16 "Absence": a <c>Try*</c> never returns null, and a nullable out
///     parameter of a <c>bool Try*</c> says when it is null.
/// </summary>
/// <remarks>
///     Reflection over the production assemblies beside the test binary, the set
///     <see cref="ExceptionInnerExceptionContractTests" /> scans, private members included and compiler-generated ones
///     excluded. A <c>Try*</c> that means "attempt this and swallow the failure" may return <c>void</c>, <c>bool</c>,
///     <c>Task</c>, an outcome type or an enum; only a nullable result is refused. Out parameters that are null on
///     SUCCESS sit in <c>Architecture/TryOutParameterNullOnSuccessAllowlist.txt</c>, shrink-only.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class TryMethodConventionTests
{
    /// <summary>Non-vacuity floors under today's counts: a scan that stops loading types fails here.</summary>
    private const int TryMethodFloor = 300;

    private const int CheckedOutParameterFloor = 100;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "TryOutParameterNullOnSuccessAllowlist.txt");

    private static readonly string[] ConditionAttributes =
        ["NotNullWhenAttribute", "MaybeNullWhenAttribute", "NotNullIfNotNullAttribute"];

    private static readonly Lazy<TryScan> Scan = new(ScanAssemblies, isThreadSafe: true);

    [Test]
    public void NoTryMethod_ReturnsANullableResult()
    {
        var scan = Scan.Value;

        AssertEx.Empty(scan.LoadFailures, "a production assembly's types could not be loaded: " + string.Join("; ", scan.LoadFailures));
        AssertEx.True(scan.TryMethods >= TryMethodFloor,
            $"Found {scan.TryMethods} Try* methods, below the non-vacuity floor of {TryMethodFloor}: the scan broke.");

        AssertEx.Empty(scan.NullableReturns,
            "A Try* method returns a nullable result (docs/wiki/16-code-conventions.md, \"Absence\", rules 1 and 2): "
            + "a lookup where absence is normal is named Find*/Get* and returns T?, and Try* means "
            + "bool Try*(…, [NotNullWhen(true)] out T? x). Rename or reshape:"
            + Environment.NewLine + string.Join(Environment.NewLine, scan.NullableReturns));
    }

    [Test]
    public void EveryNullableOutParameterOfABoolTry_SaysWhenItIsNull()
    {
        var scan = Scan.Value;

        AssertEx.True(scan.CheckedOutParameters >= CheckedOutParameterFloor,
            $"Checked {scan.CheckedOutParameters} nullable out parameters, below the floor of {CheckedOutParameterFloor}: the scan broke.");

        var allowed = Allowlist();
        var offenders = scan.UnattributedOutParameters.Where(key => !allowed.Contains(key)).ToList();

        AssertEx.Empty(offenders,
            "A nullable out parameter of a bool Try* method carries no [NotNullWhen], [MaybeNullWhen] or "
            + "[NotNullIfNotNull] (docs/wiki/16-code-conventions.md, \"Absence\", rule 2). Add the attribute; only a "
            + "parameter that is legitimately null on SUCCESS goes in Architecture/TryOutParameterNullOnSuccessAllowlist.txt "
            + "with its reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an entry whose parameter now carries an attribute, or is gone.</summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var unattributed = Scan.Value.UnattributedOutParameters.ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist().Where(entry => !unattributed.Contains(entry)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(stale,
            "Architecture/TryOutParameterNullOnSuccessAllowlist.txt lists an out parameter that now carries a "
            + "nullability condition, is no longer nullable, or no longer exists. The list is shrink-only: delete the "
            + "line(s) below."
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    private static TryScan ScanAssemblies()
    {
        var context = new NullabilityInfoContext();
        var failures = new List<string>();
        var nullableReturns = new SortedSet<string>(StringComparer.Ordinal);
        var unattributed = new SortedSet<string>(StringComparer.Ordinal);
        var tryMethods = 0;
        var checkedOut = 0;

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "XE-Local-AI-Engine.*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (ExceptionInnerExceptionContractTests.NonProductionAssemblies.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            Type?[] types;
            try
            {
                types = Assembly.LoadFrom(path).GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                failures.Add($"{name} loaded {exception.Types.Count(static type => type is not null)} of {exception.Types.Length} types");
                types = exception.Types;
            }

            foreach (var type in types)
            {
                if (type is null || type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    continue;
                }

                foreach (var method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                                       | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (!IsTryName(method.Name) || method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                    {
                        continue;
                    }

                    tryMethods++;
                    var member = $"{TypeName(type)}.{method.Name}";

                    if (ReturnsNullable(context, method.ReturnParameter))
                    {
                        nullableReturns.Add($"{member} returns {method.ReturnType} with a nullable result");
                    }

                    if (method.ReturnType != typeof(bool))
                    {
                        continue;
                    }

                    foreach (var parameter in method.GetParameters().Where(static parameter => parameter.IsOut))
                    {
                        if (!IsNullable(context.Create(parameter)))
                        {
                            continue;
                        }

                        checkedOut++;
                        if (!parameter.GetCustomAttributesData().Any(static data => ConditionAttributes.Contains(data.AttributeType.Name)))
                        {
                            unattributed.Add($"{member}({parameter.Name})");
                        }
                    }
                }
            }
        }

        return new TryScan
        {
            LoadFailures = failures,
            TryMethods = tryMethods,
            CheckedOutParameters = checkedOut,
            NullableReturns = [.. nullableReturns],
            UnattributedOutParameters = [.. unattributed]
        };
    }

    private static bool IsTryName(string name) =>
        name.Length > 3 && name.StartsWith("Try", StringComparison.Ordinal) && char.IsUpper(name[3]);

    /// <summary><c>T?</c>, <c>Nullable&lt;T&gt;</c>, or a <c>Task</c>/<c>ValueTask</c> of either.</summary>
    private static bool ReturnsNullable(NullabilityInfoContext context, ParameterInfo returnParameter)
    {
        var info = context.Create(returnParameter);
        var type = returnParameter.ParameterType;

        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            return IsNullable(info.GenericTypeArguments[0]);
        }

        return IsNullable(info);
    }

    private static bool IsNullable(NullabilityInfo info) =>
        info.ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(info.Type) is not null;

    private static string TypeName(Type type) =>
        (type.FullName ?? type.Name).Replace('+', '.');

    /// <summary><c>Namespace.Type.Method(parameter)</c> entries; text after <c>#</c> is the reason.</summary>
    private static HashSet<string> Allowlist() =>
        File.ReadLines(AllowlistPath)
            .Select(static raw => raw.Split('#', 2)[0].Trim())
            .Where(static line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private sealed class TryScan
    {
        public required IReadOnlyList<string> LoadFailures { get; init; }

        public required int TryMethods { get; init; }

        public required int CheckedOutParameters { get; init; }

        public required IReadOnlyList<string> NullableReturns { get; init; }

        public required IReadOnlyList<string> UnattributedOutParameters { get; init; }
    }
}
