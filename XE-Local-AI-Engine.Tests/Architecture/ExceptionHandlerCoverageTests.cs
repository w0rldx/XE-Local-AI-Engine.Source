namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Every public not-found, conflict, validation or unavailable exception has a host handler or a reasoned catch.</summary>
/// <remarks>
///     An exception no <c>IExceptionHandler</c> maps reaches <c>DefaultExceptionHandler</c> as a generic 500. A type
///     counts as handled when a file under <c>XE-Local-AI-Engine.Client/ExceptionHandling/</c> names it, or names a
///     base type declared in the scanned or persistence assemblies, in comment- and literal-stripped code. The rest sit in
///     <c>Architecture/UnhandledExceptionAllowlist.txt</c> with the symbol that catches them, shrink-only.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class ExceptionHandlerCoverageTests
{
    private const string ServiceSection = "service";

    private const string HostSection = "host";

    /// <summary>Non-vacuity floors under today's counts: a moved anchor type or handler folder would otherwise scan nothing.</summary>
    private const int ExceptionTypeFloor = 25;

    private const int HandlerFileFloor = 15;

    private static readonly string[] Suffixes = ["NotFoundException", "ConflictException", "ValidationException", "UnavailableException"];

    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(RuntimePackageValidationResult).Assembly,
        typeof(IInvocationAgentFactory).Assembly
    ];

    /// <summary>
    ///     Where a named base type may live: the scanned assemblies plus the persistence one, which declares the shared
    ///     <see cref="ResourceNotFoundException" /> base. Never the BCL, whose <c>Exception</c> every handler names.
    /// </summary>
    private static readonly Assembly[] BaseTypeAssemblies = [.. ScannedAssemblies, typeof(ResourceNotFoundException).Assembly];

    private static readonly string HandlerDirectory =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Client", "ExceptionHandling");

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "UnhandledExceptionAllowlist.txt");

    private static readonly Lazy<IReadOnlyList<string>> Unhandled = new(Scan, isThreadSafe: true);

    [Test]
    public void FeatureExceptions_AreHandledOrReasoned()
    {
        var allowed = Allowlist().Keys.ToHashSet(StringComparer.Ordinal);
        var offenders = Unhandled.Value.Where(type => !allowed.Contains(type)).ToList();

        AssertEx.Empty(offenders,
            "A public *NotFoundException/*ConflictException/*ValidationException/*UnavailableException that no "
            + "XE-Local-AI-Engine.Client/ExceptionHandling handler names becomes a generic 500. Map it in a handler "
            + "(ConflictExceptionHandler for a conflict), or, if every caller catches it, add "
            + "'<type full name>|service|<catching symbol>' (or '|host|' when a host endpoint or hub catches it) to Architecture/UnhandledExceptionAllowlist.txt. Types:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an entry whose type a handler now names, or that no longer exists.</summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var unhandled = Unhandled.Value.ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist().Keys.Where(entry => !unhandled.Contains(entry)).Order(StringComparer.Ordinal).ToList();

        AssertEx.Empty(stale,
            "Architecture/UnhandledExceptionAllowlist.txt lists a type a handler now names, or one that no longer "
            + "exists. The list is shrink-only: delete the line(s) below."
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    public void TheAllowlist_NamesASectionAndACatchingSymbolForEveryEntry()
    {
        var malformed = EntryLines()
                        .Where(line => line.Split('|') is not [{ } type, ServiceSection or HostSection, { } reason]
                                       || type.Trim().Length == 0
                                       || reason.Trim().Length == 0)
                        .ToList();

        AssertEx.Empty(malformed,
            $"Every allowlist entry is '<type full name>|{ServiceSection}|<catching symbol>' or "
            + $"'<type full name>|{HostSection}|<catching symbol>'. Malformed:"
            + Environment.NewLine + string.Join(Environment.NewLine, malformed));
    }

    /// <summary>Full names of the in-scope exception types no handler file names, sorted.</summary>
    private static IReadOnlyList<string> Scan()
    {
        var types = ScannedAssemblies
                    .SelectMany(assembly => assembly.GetExportedTypes())
                    .Where(type => type.IsAssignableTo(typeof(Exception))
                                   && Suffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal)))
                    .ToList();

        var handlerFiles = Directory.GetFiles(HandlerDirectory, "*.cs");
        var handlerNames = handlerFiles
                           .SelectMany(path => Identifier().Matches(SourceCommentStripper.StripCommentsAndLiterals(File.ReadAllText(path))))
                           .Select(match => match.Value)
                           .ToHashSet(StringComparer.Ordinal);

        AssertEx.True(types.Count >= ExceptionTypeFloor,
            $"Found {types.Count} in-scope exception types, below the floor of {ExceptionTypeFloor}.");
        AssertEx.True(handlerFiles.Length >= HandlerFileFloor,
            $"Found {handlerFiles.Length} handler files under {HandlerDirectory}, below the floor of {HandlerFileFloor}.");

        return types.Where(type => !IsNamed(type, handlerNames))
                    .Select(type => type.FullName!)
                    .Order(StringComparer.Ordinal)
                    .ToList();
    }

    /// <summary>True when the handlers name <paramref name="type" /> or a base type declared in <see cref="BaseTypeAssemblies" />.</summary>
    private static bool IsNamed(Type type, HashSet<string> handlerNames)
    {
        for (var current = type; current is not null && BaseTypeAssemblies.Contains(current.Assembly); current = current.BaseType)
        {
            if (handlerNames.Contains(current.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Entries keyed by type full name, read from the repository rather than the test output directory.</summary>
    private static Dictionary<string, string> Allowlist() =>
        EntryLines().Select(line => line.Split('|'))
                    .ToDictionary(fields => fields[0].Trim(), fields => fields.Length > 1 ? fields[1] : string.Empty,
                        StringComparer.Ordinal);

    private static IEnumerable<string> EntryLines() =>
        File.ReadLines(AllowlistPath)
            .Select(raw => raw.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'));

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex Identifier();
}
