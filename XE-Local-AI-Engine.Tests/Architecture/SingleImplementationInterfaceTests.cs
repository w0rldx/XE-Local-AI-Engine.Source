namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>An interface declared in Client.Application earns its existence by more than one implementation.</summary>
/// <remarks>
///     It passes with two production implementations (counted through derived interfaces), an
///     <c>IEnumerable&lt;T&gt;</c> consumer, a production implementation outside Client.Application, or a test that
///     substitutes or fakes it. Anything else is listed with its reason in
///     <c>Architecture/SingleImplementationInterfaceAllowlist.txt</c>, shrink-only.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class SingleImplementationInterfaceTests
{
    private const string DeclaringProject = "XE-Local-AI-Engine.Client.Application/";

    /// <summary>A floor under today's count of Client.Application interfaces: a broken walk or regex fails here.</summary>
    private const int DeclaredInterfaceFloor = 250;

    private static readonly string AllowlistPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Tests", "Architecture", "SingleImplementationInterfaceAllowlist.txt");

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Verdicts = new(Judge, isThreadSafe: true);

    [Test]
    public void ClientApplicationInterfaces_HaveMoreThanOneImplementationOrAListedReason()
    {
        var verdicts = Verdicts.Value;
        var allowed = Allowlist();

        AssertEx.True(verdicts.Count >= DeclaredInterfaceFloor,
            $"Only {verdicts.Count} Client.Application interfaces were read, below the floor of "
            + $"{DeclaredInterfaceFloor}. The scan has stopped seeing declarations, so the rule cannot fire.");

        var offenders = verdicts.Where(verdict => verdict.Value.Length == 0 && !allowed.ContainsKey(verdict.Key))
                                .Select(verdict => verdict.Key)
                                .Order(StringComparer.Ordinal)
                                .ToList();

        AssertEx.Empty(offenders,
            "These Client.Application interfaces have one implementation and nothing else that needs the seam "
            + "(docs/wiki/16-code-conventions.md, \"No single-implementation interface\"). Inject the class and delete "
            + "the interface. If a test needs it, substitute it in that test; a kept interface goes in "
            + "Architecture/SingleImplementationInterfaceAllowlist.txt with its reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The shrink-only half: an entry that is gone or now passes by rule is deleted.</summary>
    [Test]
    public void TheAllowlist_HasNoStaleEntry()
    {
        var verdicts = Verdicts.Value;
        var stale = new List<string>();

        foreach (var type in Allowlist().Keys.Order(StringComparer.Ordinal))
        {
            if (!verdicts.TryGetValue(type, out var reason))
            {
                stale.Add($"{type}   -> delete the line, no Client.Application interface has that name");
            }
            else if (reason.Length > 0)
            {
                stale.Add($"{type}   -> delete the line, it passes: {reason}");
            }
        }

        AssertEx.Empty(stale,
            "Architecture/SingleImplementationInterfaceAllowlist.txt lists an interface that no longer needs a reason. "
            + "The list is shrink-only:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    [Arguments("observer", "public interface IObserver { }",
        "sealed class A : IObserver { }", "sealed class U(IEnumerable<IObserver> all) { }", "", "IEnumerable<T> consumer")]
    [Arguments("host implementation", "public interface INotifier { }",
        "", "", "HOST:sealed class Publisher : INotifier { }", "implemented outside Client.Application")]
    [Arguments("two implementations", "public interface IStore { }",
        "sealed class A : IStore { }\nsealed class B : Base, IStore { }", "", "", "2 production implementations")]
    [Arguments("through a derived interface", "public interface IBase { }\npublic interface IDerived : IBase { }",
        "sealed class A : IBase { }\nsealed class B : IDerived { }", "", "", "2 production implementations")]
    [Arguments("substituted in a test", "public interface IClock { }",
        "sealed class Clock : IClock { }", "", "TEST:var c = Substitute.For<IClock>();", "substituted in a test")]
    [Arguments("faked in a test", "public interface IClock { }",
        "sealed class Clock : IClock { }", "", "TEST:sealed class FakeClock : IClock { }", "implemented by a test fake")]
    [Arguments("one implementation", "public interface ISolo { }",
        "sealed class Solo : ISolo { }", "", "", "")]
    [Arguments("resolved in a test only", "public interface ISolo { }",
        "sealed class Solo : ISolo { }", "", "TEST:var s = sp.GetRequiredService<ISolo>();", "")]
    [Arguments("a lookalike name", "public interface ISolo { }",
        "sealed class Solo : ISolo { }\nsealed class Other : ISoloExtra { }", "", "", "")]
    [Arguments("a mention in a comment", "public interface ISolo { }",
        "sealed class Solo : ISolo { }\n// sealed class Other : ISolo { }", "", "", "")]
    [Arguments("a type argument", "public interface ISolo { }",
        "sealed class Solo : ISolo { }\nsealed class Watcher : IProgress<ISolo> { }", "", "", "")]
    [Arguments("a type argument outside", "public interface ISolo { }",
        "sealed class Solo : ISolo { }", "", "HOST:sealed class Watcher : IProgress<ISolo> { }", "")]
    [Arguments("a constraint", "public interface ISolo { }",
        "sealed class Solo : ISolo { }\nsealed class Box<T> : Base where T : ISolo { }", "", "", "")]
    [Arguments("a partial type", "public interface ISolo { }",
        "sealed partial class Solo : ISolo { }\nsealed partial class Solo : ISolo { }", "", "", "")]
    [Arguments("qualified implementations", "public interface IStore { }",
        "sealed class A : Ns.IStore { }\nsealed class B : global::Ns.IStore, IOther<IStore> { }", "", "",
        "2 production implementations")]
    public void TheRule_ReadsEveryReasonAnInterfaceMayStay(
        string name, string declaration, string implementations, string consumer, string elsewhere, string expected)
    {
        var interfaceName = InterfaceDeclaration().Matches(declaration)[^1].Groups["name"].Value;
        var application = string.Join("\n", declaration, implementations, consumer);
        var host = elsewhere.StartsWith("HOST:", StringComparison.Ordinal) ? elsewhere[5..] : string.Empty;
        var test = elsewhere.StartsWith("TEST:", StringComparison.Ordinal) ? elsewhere[5..] : string.Empty;

        var verdicts = JudgeSources(
        [
            Read(DeclaringProject + "A.cs", application, isTest: false),
            Read("XE-Local-AI-Engine.Client/H.cs", host, isTest: false),
            Read("XE-Local-AI-Engine.Tests/T.cs", test, isTest: true)
        ]);

        AssertEx.Equal(expected, verdicts[name == "through a derived interface" ? "IBase" : interfaceName],
            $"The '{name}' fixture was judged wrongly.");
    }

    private static IReadOnlyDictionary<string, string> Judge() =>
        JudgeSources([
            .. EnforcedSourceFiles.All().Select(file => Read(
                file.Relative,
                File.ReadAllText(file.Path),
                isTest: !EnforcedSourceFiles.IsProduction(file.Relative)))
        ]);

    private static Source Read(string relative, string text, bool isTest) =>
        new()
        {
            Relative = relative,
            Text = SourceCommentStripper.StripCommentsAndLiterals(text),
            IsTest = isTest
        };

    /// <summary>Each Client.Application interface and why it may stay; an empty reason means it may not.</summary>
    private static Dictionary<string, string> JudgeSources(IReadOnlyList<Source> sources)
    {
        var declared = sources.Where(source => !source.IsTest
                                               && source.Relative.StartsWith(DeclaringProject, StringComparison.Ordinal))
                              .SelectMany(source => InterfaceDeclaration().Matches(source.Text))
                              .Select(match => match.Groups["name"].Value)
                              .ToHashSet(StringComparer.Ordinal);

        var types = sources.SelectMany(source => TypeDeclaration().Matches(source.Text).Select(match => new Implementer
                           {
                               Source = source,
                               IsInterface = match.Groups["kind"].Value == "interface",
                               Name = match.Groups["name"].Value,
                               Bases = match.Groups["bases"].Value
                           }))
                           .ToList();

        var production = sources.Where(source => !source.IsTest).Select(source => source.Text).ToList();
        var tests = sources.Where(source => source.IsTest).Select(source => source.Text).ToList();
        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in declared)
        {
            verdicts[name] = ReasonToStay(name, ImplementersOf(name, types), production, tests);
        }

        return verdicts;
    }

    /// <summary>The first rule that lets <paramref name="name" /> stay, or empty when none does.</summary>
    private static string ReasonToStay(
        string name, List<Implementer> implementers, List<string> production, List<string> tests)
    {
        var productionClasses = implementers.Where(type => !type.IsInterface && !type.Source.IsTest).ToList();

        if (productionClasses.Count >= 2)
        {
            return $"{productionClasses.Count} production implementations";
        }

        if (productionClasses.Any(type => !type.Source.Relative.StartsWith(DeclaringProject, StringComparison.Ordinal)))
        {
            return "implemented outside Client.Application";
        }

        if (production.Any(text => text.Contains($"IEnumerable<{name}>", StringComparison.Ordinal)))
        {
            return "IEnumerable<T> consumer";
        }

        if (tests.Any(text => IsSubstituted(text, name)))
        {
            return "substituted in a test";
        }

        return implementers.Any(type => !type.IsInterface && type.Source.IsTest) ? "implemented by a test fake" : string.Empty;
    }

    /// <summary>Whether a test substitutes <paramref name="name" /> as the first type argument of <c>Substitute.For</c>.</summary>
    private static bool IsSubstituted(string text, string name) =>
        new[] { ">", ",", "<" }.Any(next => text.Contains($"Substitute.For<{name}{next}", StringComparison.Ordinal));

    /// <summary>
    ///     Every distinct type whose base list implements <paramref name="name" />, followed through derived interfaces.
    ///     A type argument (<c>IProgress&lt;ISolo&gt;</c>) or a constraint does not implement it.
    /// </summary>
    private static List<Implementer> ImplementersOf(string name, List<Implementer> types)
    {
        var found = new List<Implementer>();
        var pending = new Queue<string>([name]);
        var visited = new HashSet<string>(StringComparer.Ordinal) { name };

        while (pending.TryDequeue(out var current))
        {
            foreach (var type in types.Where(type => OuterBaseNames(type.Bases).Contains(current, StringComparer.Ordinal)))
            {
                found.Add(type);

                if (type.IsInterface && visited.Add(type.Name))
                {
                    pending.Enqueue(type.Name);
                }
            }
        }

        // A partial type repeats its base list in each part.
        return found.DistinctBy(type => $"{type.Source.IsTest}|{type.IsInterface}|{type.Name}").ToList();
    }

    /// <summary>The unqualified outer name of each base-list entry; type arguments and <c>where</c> clauses are dropped.</summary>
    private static List<string> OuterBaseNames(string bases)
    {
        var names = new List<string>();
        var entry = new StringBuilder();
        var depth = 0;

        foreach (var token in BaseListToken().Matches(bases).Select(match => match.Value))
        {
            if (depth == 0 && token == "where")
            {
                break;
            }

            depth += token switch
            {
                "<" => 1,
                ">" => -1,
                _ => 0
            };

            if (depth == 0 && token == ",")
            {
                names.Add(entry.ToString());
                entry.Clear();
            }
            else if (depth == 0 && token != ">")
            {
                entry.Append(token);
            }
        }

        names.Add(entry.ToString());
        return names.Select(qualified => qualified[(qualified.LastIndexOfAny([':', '.']) + 1)..]).ToList();
    }

    /// <summary>Entries keyed by interface name; a line without a reason is malformed.</summary>
    private static Dictionary<string, string> Allowlist()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(AllowlistPath).Select(raw => raw.Trim())
                                 .Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var fields = line.Split('|');
            AssertEx.True(fields.Length == 2 && fields.All(field => field.Trim().Length > 0) && entries.TryAdd(fields[0], fields[1]),
                $"Malformed or duplicate line in Architecture/SingleImplementationInterfaceAllowlist.txt (want type|reason): {line}");
        }

        return entries;
    }

    [GeneratedRegex(@"\w+|::|\S")]
    private static partial Regex BaseListToken();

    [GeneratedRegex(@"\binterface\s+(?<name>I[A-Z]\w*)")]
    private static partial Regex InterfaceDeclaration();

    // A type head up to its body or `;`: a primary-constructor parameter list sits between the name and the base list.
    [GeneratedRegex(@"\b(?<kind>class|struct|record|interface)\s+(?<name>\w+)\s*(?:<[^>{;]*>)?\s*(?:\([^)]*\))?\s*:(?<bases>[^{;]*)")]
    private static partial Regex TypeDeclaration();

    /// <summary>One scanned file: its repository-relative path, its stripped text, and whether it is test code.</summary>
    private sealed class Source
    {
        public required string Relative { get; init; }

        public required string Text { get; init; }

        public required bool IsTest { get; init; }
    }

    /// <summary>A type declaration whose base list was read.</summary>
    private sealed class Implementer
    {
        public required Source Source { get; init; }

        public required bool IsInterface { get; init; }

        public required string Name { get; init; }

        public required string Bases { get; init; }
    }
}
