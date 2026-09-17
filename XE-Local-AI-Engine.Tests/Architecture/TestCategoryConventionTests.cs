namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Keeps the test classification honest: every backend test class carries exactly one category, and a
///     <c>Unit</c> class reaches nothing that starts a host, a database, a socket or a child process.
/// </summary>
/// <remarks>
///     Reflection sees which <c>CategoryAttribute</c> a class bound to, and <c>System.ComponentModel</c> declares one
///     that a file-scoped <c>using</c> can win with; the source scan reaches the sibling test projects, which this one
///     does not reference. Markers derive at run time from <see cref="IntegrationPrimitives" />. Scan granularity is
///     the top-level type, so a nested test class is covered by the reflection rule alone. Rationale:
///     <c>docs/wiki/17-writing-tests.md</c> §1b.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class TestCategoryConventionTests
{
    private static readonly string[] CategoryValues =
        [TestCategories.Unit, TestCategories.Integration, TestCategories.ExternalInfra];

    /// <summary>The test projects, then the test-support projects whose fixtures the closure walks through.</summary>
    private static readonly string[] ScannedProjects =
    [
        "XE-Local-AI-Engine.Tests",
        "XE-Local-AI-Engine.Client.Persistence.Tests",
        "XE-Local-AI-Engine.AI.Agent.Tests",
        "XE-Local-AI-Engine.Client.Testing",
        "XE-Local-AI-Engine.Testing.FakeOllama",
        "XE-Local-AI-Engine.Testing.FakeDocker"
    ];

    /// <summary>
    ///     What "starts something real in-process" spells as. A name that also reads as a plain type reference is
    ///     matched at its construction or call site, so naming a type is not using it.
    /// </summary>
    private static readonly (string Label, Regex Pattern)[] IntegrationPrimitives =
    [
        Primitive("TestServerWebAppFactory"), Primitive("WebApplicationFactory"),
        Primitive("SqliteConnection"), Primitive("UseSqlite"), Primitive("DbContextOptionsBuilder"),
        Primitive("MigratedDatabaseTemplate"), Primitive("EnsureCreated", @"\bEnsureCreated\w*\b"),
        Primitive("Database.Migrate", @"\bDatabase\.Migrate\w*\b"),
        Primitive("new NodeChatDbContext", @"\bnew\s+NodeChatDbContext\b"),
        Primitive("FakeOllamaServer"), Primitive("FakeDockerServer"),
        Primitive("TcpListener"), Primitive("HttpListener"), Primitive("TcpClient"),
        Primitive("new Socket", @"\bnew\s+Socket\b"), Primitive("NamedPipe", @"\bNamedPipe\w*\b"),
        Primitive("KestrelServerOptions"), Primitive("UseKestrel"),
        Primitive("WebApplication.Create", @"\bWebApplication\.Create\w*\b"),
        Primitive("Host.CreateApplicationBuilder", @"\bHost\.CreateApplication\w*\b"),
        Primitive("HubConnection", @"\bHubConnection\w*\b"),
        Primitive("Process.Start", @"\bProcess\.Start\b"), Primitive("ProcessStartInfo"),
        Primitive("WaitForExit", @"\.WaitForExit\b"), Primitive("LoopbackPort"),
        // The composition root: AddNodeModelRuntime is the only UseSqlite in the product DI path, and the
        // entries above it reach it through product code no scan over test sources can follow.
        Primitive("Program.CreateAppAsync", @"\bCreateAppAsync\s*\("),
        Primitive("builder.AddServices", @"\.AddServices\s*\("),
        Primitive("builder.AddNodeApplication", @"\.AddNodeApplication\s*\("),
        Primitive("builder.AddNodeModelRuntime", @"\.AddNodeModelRuntime\s*\(")
    ];

    // This file holds the vocabulary above as data, so scanning it reports itself.
    private const string GuardFileName = "TestCategoryConventionTests.cs";

    private static readonly Regex TopLevelDeclaration = NewRegex(
        @"^(?:(?:public|internal|sealed|abstract|partial|static|file)\s+)*"
        + @"(?:class|record\s+struct|record\s+class|record|struct|interface)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline);

    // Anchoring on a bracket or a comma reads a combined list ([Test, Repeat(2)], [NotInParallel, Category(…)])
    // and a line break anywhere inside it, while a longer name such as TestCase still is not an attribute use.
    private static readonly Regex CategoryAttributeUse =
        NewRegex(@"[\[,]\s*Category\s*\(\s*TestCategories\s*\.\s*(?<value>[A-Za-z]+)\s*\)");

    private static readonly Regex TestAttributeUse = NewRegex(@"[\[,]\s*Test\s*[\]\(,]");
    private static readonly Regex Identifier = NewRegex("[A-Za-z_][A-Za-z0-9_]*");

    // The source rules share one corpus and the scan is pure, so one walk serves them all.
    private static readonly Lazy<TopLevelType[]> TopLevelTypes = new(ReadTopLevelTypes, isThreadSafe: true);

    /// <summary>Reflection, so the assertion is about the attribute the compiler bound, not the text in the file.</summary>
    [Test]
    public void EveryTestClassInThisAssembly_CarriesExactlyOneCategory()
    {
        var testClasses = typeof(TestCategoryConventionTests).Assembly.GetTypes()
                                                             .Where(type => type is { IsClass: true, IsAbstract: false })
                                                             .Where(DeclaresATest)
                                                             .ToArray();

        // Non-vacuity: a reflection failure that returned nothing would otherwise read as a clean pass.
        AssertEx.True(testClasses.Length >= 900,
            $"Expected at least nine hundred test classes in this assembly; found {testClasses.Length}. The reflection walk is broken.");

        var offenders = testClasses
                        .Select(type => (type, categories: CategoriesOf(type)))
                        .Where(candidate => candidate.categories.Length != 1 || !CategoryValues.Contains(candidate.categories[0], StringComparer.Ordinal))
                        .Select(candidate => $"{candidate.type.FullName} has [{string.Join(", ", candidate.categories)}]")
                        .ToArray();

        AssertEx.True(offenders.Length == 0,
            "Every test class carries exactly one of [Category(TestCategories.Unit|Integration|ExternalInfra)]. "
            + $"A class with none, or with two, breaks the category filters the gate documents:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>The same rule over source, which is how the sibling test projects are reached without a reference.</summary>
    [Test]
    public void EveryTestClassInEveryTestProject_CarriesExactlyOneCategory()
    {
        var spans = TopLevelTypes.Value;

        AssertEx.True(spans.Length >= 1500,
            $"Expected at least fifteen hundred top-level types across the scanned projects; found {spans.Length}. The walk is broken.");

        var offenders = spans
                        .Where(span => TestAttributeUse.IsMatch(span.Text))
                        .Where(span => span.Categories.Length != 1 || !CategoryValues.Contains(span.Categories[0], StringComparer.Ordinal))
                        .Select(span => $"{span.RelativePath} :: {span.Name} has [{string.Join(", ", span.Categories)}]")
                        .ToArray();

        AssertEx.True(offenders.Length == 0,
            "Every class with [Test] methods carries exactly one class-level [Category(TestCategories.…)]. "
            + $"Add the missing one, or delete the duplicate:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>A <c>Unit</c> class must not reach an Integration mechanism, directly or through any helper.</summary>
    [Test]
    public void NoUnitClass_ReachesAnIntegrationMarker()
    {
        var spans = TopLevelTypes.Value;
        var markers = DeriveMarkers(spans);

        // Non-vacuity: with an empty marker set the transitive half of this rule cannot fire at all.
        AssertEx.True(markers.Count >= 20,
            $"Expected at least twenty derived Integration marker types; found {markers.Count}. The closure is broken.");

        var offenders = new List<string>();
        foreach (var span in spans.Where(candidate => candidate.Categories is [TestCategories.Unit]))
        {
            var primitive = PrimitiveIn(span.Text);
            if (primitive is not null)
            {
                offenders.Add($"{span.RelativePath} :: {span.Name} uses {primitive} — tag it Integration or remove the dependency");
                continue;
            }

            var marker = markers.Keys.Order(StringComparer.Ordinal)
                                .FirstOrDefault(name => !string.Equals(name, span.Name, StringComparison.Ordinal) && span.Tokens.Contains(name));
            if (marker is not null)
            {
                offenders.Add($"{span.RelativePath} :: {span.Name} reaches {markers[marker]} — tag it Integration or remove the dependency");
            }
        }

        AssertEx.True(offenders.Count == 0,
            $"A Unit class starts nothing real. These do:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>The half of the ExternalInfra question a scan can decide: a name that advertises a live suite.</summary>
    /// <remarks>
    ///     A <c>*RealDaemonTests</c> class needs a real daemon, so it must be <c>ExternalInfra</c>. A
    ///     <c>*LiveTests</c>/<c>*LiveSmokeTests</c> class may be <c>Integration</c> when only some of its tests are
    ///     gated, so there the rule forbids <c>Unit</c> alone.
    /// </remarks>
    [Test]
    public void ALiveSuiteName_IsNotClassifiedAsAUnitTest()
    {
        var spans = TopLevelTypes.Value.Where(span => span.Categories.Length == 1).ToArray();

        var daemonSuites = spans.Where(span => span.Name.EndsWith("RealDaemonTests", StringComparison.Ordinal)).ToArray();
        var liveSuites = spans.Where(span => span.Name.EndsWith("LiveTests", StringComparison.Ordinal)
                                             || span.Name.EndsWith("LiveSmokeTests", StringComparison.Ordinal)).ToArray();

        // Non-vacuity: renaming the suites away, or a broken walk, would otherwise report a clean pass.
        AssertEx.True(daemonSuites.Length >= 4 && liveSuites.Length >= 6,
            $"Expected at least four *RealDaemonTests and six *Live(Smoke)Tests classes to scan; found {daemonSuites.Length} and {liveSuites.Length}.");

        var offenders = daemonSuites
                        .Where(span => span.Categories[0] != TestCategories.ExternalInfra)
                        .Select(span => $"{span.RelativePath} :: {span.Name} is [{span.Categories[0]}] — a *RealDaemonTests class needs a daemon the box may not have, so it is ExternalInfra")
                        .Concat(liveSuites
                                .Where(span => span.Categories[0] == TestCategories.Unit)
                                .Select(span => $"{span.RelativePath} :: {span.Name} is [Unit] — a live suite is ExternalInfra when every test is gated, Integration when only some are"))
                        .ToArray();

        AssertEx.True(offenders.Length == 0,
            $"A class named after a live or real-daemon suite is never a unit test:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>Seeds on helpers that name a primitive, then closes over helpers that name a marker helper.</summary>
    private static Dictionary<string, string> DeriveMarkers(TopLevelType[] spans)
    {
        // A name declared by more than one top-level type cannot be resolved by name alone, so it is never a marker.
        var ambiguous = spans.GroupBy(span => span.Name, StringComparer.Ordinal)
                             .Where(group => group.Count() > 1)
                             .Select(group => group.Key)
                             .ToHashSet(StringComparer.Ordinal);

        // A tagged class is a test class: classified in its own right, never a marker others inherit.
        var helpers = spans.Where(span => span.Categories.Length == 0 && !ambiguous.Contains(span.Name)).ToArray();
        var markers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var helper in helpers)
        {
            var primitive = PrimitiveIn(helper.Text);
            if (primitive is not null)
            {
                markers[helper.Name] = $"{helper.Name} (uses {primitive}, in {helper.RelativePath})";
            }
        }

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var helper in helpers.Where(candidate => !markers.ContainsKey(candidate.Name)))
            {
                var reached = markers.Keys.Order(StringComparer.Ordinal)
                                     .FirstOrDefault(name => !string.Equals(name, helper.Name, StringComparison.Ordinal) && helper.Tokens.Contains(name));
                if (reached is null)
                {
                    continue;
                }

                markers[helper.Name] = $"{helper.Name} -> {markers[reached]}";
                changed = true;
            }
        }

        return markers;
    }

    private static string? PrimitiveIn(string text)
    {
        return IntegrationPrimitives.FirstOrDefault(primitive => primitive.Pattern.IsMatch(text)).Label;
    }

    /// <summary>
    ///     Every top-level type in the scanned projects. A span runs to the next column-0 declaration, which needs no
    ///     brace matching and keeps C# embedded in an indented raw string literal from parsing as code.
    /// </summary>
    private static TopLevelType[] ReadTopLevelTypes()
    {
        var spans = new List<TopLevelType>();

        foreach (var project in ScannedProjects)
        {
            var root = RepositoryPaths.Combine(project);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || Path.GetFileName(file).Equals(GuardFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                spans.AddRange(SplitIntoTopLevelTypes(Path.GetRelativePath(RepositoryPaths.Root, file),
                    SourceCommentStripper.StripComments(File.ReadAllText(file))));
            }
        }

        return [.. spans];
    }

    private static IEnumerable<TopLevelType> SplitIntoTopLevelTypes(string relativePath, string source)
    {
        var declarations = TopLevelDeclaration.Matches(source);

        for (var index = 0; index < declarations.Count; index++)
        {
            var start = declarations[index].Index;
            var end = index + 1 < declarations.Count ? declarations[index + 1].Index : source.Length;
            var text = source[start..end];

            // The attribute block sits between the previous blank line and the declaration. Categories read only
            // that head, which is what keeps a method-level [Category] inside the body out of the class's list.
            var headStart = source.LastIndexOf($"{Environment.NewLine}{Environment.NewLine}", start, StringComparison.Ordinal);
            var head = source[(headStart < 0 ? 0 : headStart)..start];

            yield return new TopLevelType
            {
                Name = declarations[index].Groups["name"].Value,
                RelativePath = relativePath,
                Text = text,
                Categories = [.. CategoryAttributeUse.Matches(head).Select(match => match.Groups["value"].Value)],
                Tokens = [.. Identifier.Matches(text).Select(match => match.Value)]
            };
        }
    }

    private static bool DeclaresATest(Type type)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                   .Any(method => method.GetCustomAttributes(inherit: true).Any(attribute => attribute is TestAttribute));
    }

    private static string[] CategoriesOf(Type type)
    {
        return [.. type.GetCustomAttributes<CategoryAttribute>(inherit: true).Select(attribute => attribute.Category)];
    }

    private static (string Label, Regex Pattern) Primitive(string label, string? pattern = null)
    {
        return (label, NewRegex(pattern ?? $@"\b{Regex.Escape(label)}\b"));
    }

    private static Regex NewRegex(string pattern, RegexOptions options = RegexOptions.None)
    {
        return new Regex(pattern, options, TimeSpan.FromSeconds(5));
    }

    /// <summary>One top-level type, with the attribute block above it and the identifiers inside it.</summary>
    private sealed class TopLevelType
    {
        public required string Name { get; init; }

        public required string RelativePath { get; init; }

        public required string Text { get; init; }

        public required string[] Categories { get; init; }

        public required HashSet<string> Tokens { get; init; }
    }
}
