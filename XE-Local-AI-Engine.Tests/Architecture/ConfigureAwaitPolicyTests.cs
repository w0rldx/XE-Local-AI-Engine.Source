namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text;
using System.Xml.Linq;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Keeps <c>ConfigureAwait</c> contextual: every awaited call in a library project is configured, and no other
///     project writes the call at all.
/// </summary>
/// <remarks>
///     A raw-text scan over comment-stripped source, like <see cref="ThirdPartySdkBoundaryTests" />; generated sources
///     are excluded. The library rule counts per statement and ignores the disposal of an <c>await using</c>, which
///     is why CA2007 is not the gate. <see cref="LibraryProjects" /> is the closed list, so a new project is an
///     application project until it is named there. Rationale: <c>docs/wiki/16-code-conventions.md</c>.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ConfigureAwaitPolicyTests
{
    /// <summary>
    ///     The projects that may be consumed from a caller with a synchronization context, so they configure every
    ///     awaited call. Everything else in the solution is an application project by subtraction.
    /// </summary>
    private static readonly string[] LibraryProjects =
    [
        "XE-Local-AI-Engine.AI.Agent",
        "XE-Local-AI-Engine.AI.Contracts",
        "XE-Local-AI-Engine.Providers.Abstractions",
        "XE-Local-AI-Engine.Providers.Capabilities",
        "XE-Local-AI-Engine.Providers.CodexOAuth",
        "XE-Local-AI-Engine.Providers.HuggingFace",
        "XE-Local-AI-Engine.Providers.LlamaServer",
        "XE-Local-AI-Engine.Providers.Ollama",
        "XE-Local-AI-Engine.Providers.OpenAICompat",
        "XE-Local-AI-Engine.Providers.OpenAICompatible.Core",
        "XE-Local-AI-Engine.Providers.StableDiffusionCpp",
        "XE-Local-AI-Engine.Providers.Training",
        "XE-Local-AI-Engine.Providers.WhisperCpp"
    ];

    /// <summary>
    ///     C# that ships outside the solution. <c>tools/</c> holds a code generator run by hand; it is application
    ///     code for this rule, and naming the root here is what keeps it inside the scan.
    /// </summary>
    private static readonly string[] ExtraApplicationRoots = ["tools"];

    /// <summary>
    ///     Non-vacuity floors, set under today's counts the same way <see cref="ThirdPartySdkBoundaryTests" /> sets
    ///     its own: a renamed directory reads too few files and fails here rather than passing on an empty scan.
    /// </summary>
    private const int ApplicationFileFloor = 3500;
    private const int LibraryFileFloor = 450;

    /// <summary>
    ///     The floor on awaited calls the library rule actually inspected (1,080 today). A statement splitter that
    ///     stopped recognising <c>await</c> would find no violations anywhere and pass in silence.
    /// </summary>
    private const int LibraryAwaitFloor = 900;

    /// <summary>Code whose awaits must be counted. A scan that misses these stops fencing.</summary>
    private static readonly (string Case, string Source, int Required, int Available)[] MustBeCounted =
    [
        ("a plain awaited call", "await Store.SaveAsync(ct);", 1, 0),
        ("an awaited call that is configured", "await Store.SaveAsync(ct).ConfigureAwait(false);", 1, 1),
        ("the configuration on its own line",
            "await Store.SaveAsync(ct)\n    .ConfigureAwait(false);", 1, 1),
        ("two awaits in one statement, one configured",
            "var pair = (await A().ConfigureAwait(false), await B());", 2, 1),
        ("an await inside an await-using initializer",
            "await using var stream = await Open(path).ConfigureAwait(false);", 1, 1),
        ("an await-foreach source", "await foreach (var x in Read(ct).ConfigureAwait(false)) { }", 0, 1),
        ("a URL in a string, then a real await",
            """var url = "https://localhost"; await Fetch(url);""", 1, 0)
    ];

    /// <summary>Text that only looks like an await or a configuration. A guard that fires on prose is one somebody deletes.</summary>
    private static readonly (string Case, string Source)[] MustNotBeCounted =
    [
        ("a line comment naming the call", "// Call .ConfigureAwait(false) here; await the result afterwards."),
        ("an XML doc comment", "/// <summary>Awaits nothing; see await and ConfigureAwait above.</summary>"),
        ("Task.Yield, which has no ConfigureAwait overload", "await Task.Yield();"),
        ("an identifier that merely starts with await", "var awaited = Pending;")
    ];

    /// <summary>
    ///     The one exemption: this file, whose cases above quote the banned token as data. A guard has to be able to
    ///     name what it forbids.
    /// </summary>
    /// <remarks>
    ///     String content is deliberately not stripped anywhere else, so a token inside a configuration key still
    ///     trips the fence; that is what makes the test data look like a violation here.
    ///     <see cref="EveryProjectInTheSolution_IsOnExactlyOneSideOfTheRule" /> fails if this path stops resolving, so
    ///     the exemption cannot outlive the class.
    /// </remarks>
    private const string SelfPath =
        "XE-Local-AI-Engine.Tests/Architecture/ConfigureAwaitPolicyTests.cs";

    [Test]
    public void ApplicationAndTestProjects_WriteNoConfigureAwait()
    {
        var offenders = new List<string>();
        var files = 0;

        foreach (var root in ApplicationRoots())
        {
            foreach (var (path, relative) in SourceFiles(root))
            {
                files++;

                if (string.Equals(relative, SelfPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var stripped = SourceCommentStripper.StripComments(File.ReadAllText(path));

                offenders.AddRange(Occurrences(stripped, ".ConfigureAwait(")
                    .Select(site => $"{relative}:{site.Line}  {site.Code}"));
            }
        }

        AssertEx.True(files >= ApplicationFileFloor,
            $"Only {files} application C# files were scanned, below the floor of {ApplicationFileFloor}. The walk is "
            + "reading the wrong directories, so this fence cannot fire.");

        AssertEx.Empty(offenders,
            "ConfigureAwait is contextual and belongs in the library projects only: these hosts install no "
            + "synchronization context, so the call changes nothing and only reads as though it did. Delete it from "
            + "the site(s) below rather than adding an exemption here:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Test]
    public void LibraryProjects_ConfigureEveryAwaitedCall()
    {
        var offenders = new List<string>();
        var files = 0;
        var awaits = 0;

        foreach (var project in LibraryProjects)
        {
            foreach (var (path, relative) in SourceFiles(RepositoryPaths.Combine(project)))
            {
                files++;
                var stripped = SourceCommentStripper.StripComments(File.ReadAllText(path));

                foreach (var (line, text) in Statements(stripped))
                {
                    var required = AwaitedCallCount(text);
                    awaits += required;

                    if (Count(text, ".ConfigureAwait(") < required)
                    {
                        offenders.Add($"{relative}:{line}  {Condense(text)}");
                    }
                }
            }
        }

        AssertEx.True(files >= LibraryFileFloor,
            $"Only {files} library C# files were scanned, below the floor of {LibraryFileFloor}.");
        AssertEx.True(awaits >= LibraryAwaitFloor,
            $"Only {awaits} awaited calls were inspected, below the floor of {LibraryAwaitFloor}. The statement "
            + "splitter has stopped seeing awaits, so this fence cannot fire.");

        AssertEx.Empty(offenders,
            "A library may be consumed from a caller that has a synchronization context, so every awaited call in "
            + "one configures its continuation. Add .ConfigureAwait(false) to the awaited call(s) below:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Test]
    public void EveryProjectInTheSolution_IsOnExactlyOneSideOfTheRule()
    {
        var solution = XDocument.Load(RepositoryPaths.Combine("XE-Local-AI-Engine.slnx"));
        var projects = solution.Descendants("Project")
                               .Select(project => (string?)project.Attribute("Path"))
                               .Where(path => path is not null)
                               .Select(path => Path.GetFileNameWithoutExtension(path!.Replace('\\', '/')))
                               .Order(StringComparer.Ordinal)
                               .ToArray();

        AssertEx.NotEmpty(projects, "No projects were read from the solution file.");

        var missing = LibraryProjects.Where(project => !projects.Contains(project, StringComparer.Ordinal)).ToList();
        AssertEx.Empty(missing,
            "These library projects are named here but are not in the solution — they were renamed or removed, and "
            + "the entry outlived them. Delete the line(s) below:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));

        var absent = projects.Where(project => !Directory.Exists(RepositoryPaths.Combine(project))).ToList();
        AssertEx.Empty(absent,
            "A solution project's directory was not found under the repository root, so its sources were never "
            + "scanned: " + string.Join(", ", absent));

        AssertEx.True(LibraryProjects.SequenceEqual(LibraryProjects.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "The library project list must stay sorted, one per line, so a diff to it is readable.");

        AssertEx.True(File.Exists(RepositoryPaths.Combine(SelfPath.Split('/'))),
            $"The one self-exemption no longer resolves to a file ('{SelfPath}'). This class was renamed or moved, so "
            + "the application scan is now reading its own test data as a violation — update the path.");
    }

    /// <summary>
    ///     The guard's own check. A scan whose comment stripping ate the code after a string literal, or whose await
    ///     counting stopped matching, reads clean source everywhere and reports a fence that cannot fire.
    /// </summary>
    [Test]
    public void TheGuard_CountsRealAwaitsAndIgnoresProse()
    {
        foreach (var (name, source, required, available) in MustBeCounted)
        {
            var statements = Statements(SourceCommentStripper.StripComments(source)).ToList();

            AssertEx.Equal(required, statements.Sum(statement => AwaitedCallCount(statement.Text)),
                $"The '{name}' case counted the wrong number of awaited calls. A miss here is how this fence stops "
                + "seeing code.");
            AssertEx.Equal(available, statements.Sum(statement => Count(statement.Text, ".ConfigureAwait(")),
                $"The '{name}' case counted the wrong number of ConfigureAwait calls.");
        }

        // A report is only actionable if the line is the one the await is on. The newline and indentation after a
        // terminator belong to the next statement, so a naive split reports the line above.
        const string ThreeLines = "var a = Read();\nvar b = Read();\nawait Save();\n";
        var (awaitLine, _) = Statements(ThreeLines).Single(statement => AwaitedCallCount(statement.Text) == 1);
        AssertEx.Equal(3, awaitLine,
            "The statement splitter reported the wrong line for an awaited call, so every offender it names points a "
            + "reader at the line above the problem.");

        foreach (var (name, source) in MustNotBeCounted)
        {
            var statements = Statements(SourceCommentStripper.StripComments(source)).ToList();

            AssertEx.Equal(0, statements.Sum(statement => AwaitedCallCount(statement.Text)),
                $"The '{name}' case was counted as an awaited call. A guard that fires on prose, or on an await with "
                + "no ConfigureAwait overload, is one somebody deletes.");
            AssertEx.Empty(Occurrences(SourceCommentStripper.StripComments(source), ".ConfigureAwait(").ToList(),
                $"The '{name}' case was read as a real ConfigureAwait call.");
        }
    }

    // ---------------------------------------------------------------- scanning

    private static IEnumerable<string> ApplicationRoots()
    {
        var solution = XDocument.Load(RepositoryPaths.Combine("XE-Local-AI-Engine.slnx"));

        var applicationProjects = solution.Descendants("Project")
                                          .Select(project => (string?)project.Attribute("Path"))
                                          .Where(path => path is not null)
                                          .Select(path => Path.GetFileNameWithoutExtension(path!.Replace('\\', '/')))
                                          .Where(project => !LibraryProjects.Contains(project, StringComparer.Ordinal))
                                          .Order(StringComparer.Ordinal);

        return applicationProjects.Concat(ExtraApplicationRoots).Select(root => RepositoryPaths.Combine(root));
    }

    private static IEnumerable<(string Path, string Relative)> SourceFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');

            if (IsGenerated(relative, path))
            {
                continue;
            }

            yield return (path, relative);
        }
    }

    /// <summary>Generated sources are regenerated, never hand-edited, so the rule cannot be applied to them.</summary>
    private static bool IsGenerated(string relative, string path) =>
        relative.Contains("/obj/", StringComparison.Ordinal)
        || relative.Contains("/bin/", StringComparison.Ordinal)
        || relative.Contains("/Migrations/", StringComparison.Ordinal)
        || relative.EndsWith(".Designer.cs", StringComparison.Ordinal)
        || relative.EndsWith(".g.cs", StringComparison.Ordinal)
        || HasGeneratedHeader(path);

    private static bool HasGeneratedHeader(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[400];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read).Contains("<auto-generated", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Splits comment-stripped source at every <c>;</c>, <c>{</c> or <c>}</c> outside parentheses and brackets, so
    ///     a <c>for (;;)</c> header stays whole and an <c>await foreach</c> header is separated from its body.
    /// </summary>
    /// <remarks>
    ///     The reported line is the statement's first line of CODE, not the line the previous statement's terminator
    ///     sat on: the newline and the indentation after a <c>;</c> belong to the next statement's text, so taking the
    ///     start at the first character reports every offender one line high.
    /// </remarks>
    private static IEnumerable<(int Line, string Text)> Statements(string stripped)
    {
        var text = new StringBuilder();
        var depth = 0;
        var line = 1;
        var startLine = 1;
        var started = false;

        foreach (var character in stripped)
        {
            if (character == '\n')
            {
                line++;
            }

            switch (character)
            {
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth = Math.Max(0, depth - 1);
                    break;
                default:
                    break;
            }

            if (depth == 0 && character is ';' or '{' or '}')
            {
                yield return (startLine, text.ToString());
                text.Clear();
                started = false;
                continue;
            }

            if (!started && !char.IsWhiteSpace(character))
            {
                startLine = line;
                started = true;
            }

            text.Append(character);
        }

        yield return (startLine, text.ToString());
    }

    /// <summary>
    ///     Counts the awaited calls in one statement: every <c>await</c> keyword except those opening an
    ///     <c>await using</c> or <c>await foreach</c>, and except <c>await Task.Yield()</c>.
    /// </summary>
    /// <remarks>
    ///     An <c>await using</c> or <c>await foreach</c> awaits a disposal or an enumerator move rather than the call,
    ///     and <c>Task.Yield()</c> has no <c>ConfigureAwait</c> overload to carry.
    /// </remarks>
    private static int AwaitedCallCount(string statement)
    {
        var count = 0;

        for (var index = statement.IndexOf("await", StringComparison.Ordinal);
             index >= 0;
             index = statement.IndexOf("await", index + 1, StringComparison.Ordinal))
        {
            if (!IsWholeWord(statement, index, "await".Length))
            {
                continue;
            }

            var rest = statement.AsSpan(index + "await".Length).TrimStart();

            if (rest.StartsWith("using", StringComparison.Ordinal)
                || rest.StartsWith("foreach", StringComparison.Ordinal)
                || rest.StartsWith("Task.Yield(", StringComparison.Ordinal))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    private static bool IsWholeWord(string text, int start, int length) =>
        (start == 0 || !IsWordCharacter(text[start - 1]))
        && (start + length >= text.Length || !IsWordCharacter(text[start + length]));

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static int Count(string text, string token)
    {
        var count = 0;

        for (var index = text.IndexOf(token, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(token, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    ///     Every occurrence of <paramref name="token" />, with the line it sits on and the code around it.
    /// </summary>
    /// <remarks>
    ///     The line is counted in the comment-stripped text. That matches the file because a multi-line block comment
    ///     — the one construct that would shift it — appears nowhere in the scanned projects, and the quoted code
    ///     identifies the site either way.
    /// </remarks>
    private static IEnumerable<(int Line, string Code)> Occurrences(string stripped, string token)
    {
        for (var index = stripped.IndexOf(token, StringComparison.Ordinal);
             index >= 0;
             index = stripped.IndexOf(token, index + 1, StringComparison.Ordinal))
        {
            var line = 1;

            for (var scan = 0; scan < index; scan++)
            {
                if (stripped[scan] == '\n')
                {
                    line++;
                }
            }

            var from = stripped.LastIndexOfAny(['\n'], index) + 1;
            var to = stripped.IndexOfAny(['\r', '\n'], index);
            yield return (line, Condense(stripped[from..(to < 0 ? stripped.Length : to)]));
        }
    }

    private static string Condense(string text)
    {
        var condensed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return condensed.Length <= 160 ? condensed : condensed[..160];
    }
}
