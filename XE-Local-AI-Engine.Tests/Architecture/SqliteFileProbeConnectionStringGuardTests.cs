namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Turns <c>SqliteFileProbe</c>'s "open the probed file with this exact bare connection string" doc comment
///     into a rule the build checks, over every connection string the backend test projects build.
/// </summary>
/// <remarks>
///     Microsoft.Data.Sqlite keys its pool on the exact string, so a caller that opened the probed file with a
///     decorated one leaves its own pool untouched: the probe clears a pool nobody populated, never checkpoints
///     the WAL, and the at-rest scan passes on stale bytes. Scanning only the files that NAME the probe missed
///     the shared context factories, which build the string for nine suites without mentioning it. Every literal
///     is scanned instead; a decorated one passes only where it is named below, in a file that never reaches the probe.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class SqliteFileProbeConnectionStringGuardTests
{
    private const string ProbeName = "SqliteFileProbe";
    private const string Marker = "Data Source=";

    // This file carries the rule and its exceptions as data, so scanning it would report itself.
    private const string GuardFileName = "SqliteFileProbeConnectionStringGuardTests.cs";

    private static readonly string[] ScannedProjects =
    [
        "XE-Local-AI-Engine.Tests",
        "XE-Local-AI-Engine.Client.Persistence.Tests",
        "XE-Local-AI-Engine.AI.Agent.Tests",
        "XE-Local-AI-Engine.Client.Testing"
    ];

    /// <summary>
    ///     Every decorated connection string the test projects legitimately build, keyed by the file that builds
    ///     it and carrying the reason it is safe. A listed file still fails if it ever reaches the probe.
    /// </summary>
    private static readonly (string File, string Tail, string Reason)[] KnownDecoratedStrings =
    [
        ("XE-Local-AI-Engine.Tests/Chat/NodeChatPersistenceWriterTests.cs", ":memory:",
            "In-memory database: there is no file at rest for a probe to read."),
        ("XE-Local-AI-Engine.Tests/Persistence/NodeSqlitePragmasTests.cs", ":memory:",
            "In-memory database: the fake connection keeps the WAL pragmas out of the way."),
        ("XE-Local-AI-Engine.Tests/Persistence/NodeSqlitePragmasTests.cs", "{asyncPath};Foreign Keys=False",
            "Subject of the test: the applier must turn enforcement ON from a connection that asked for it off."),
        ("XE-Local-AI-Engine.Tests/Persistence/NodeSqlitePragmasTests.cs", "{syncPath};Foreign Keys=False",
            "Subject of the test: the same proof for the synchronous path EF's ConnectionOpened takes."),
        ("XE-Local-AI-Engine.Tests/Hosting/DesktopBootstrapTests.cs", "/already/supplied/node.sqlite",
            "A configuration VALUE asserted verbatim; no connection is opened from it."),
        ("XE-Local-AI-Engine.Tests/Hosting/KnowledgeDowngradeCliIntegrationTests.cs", "{databasePath};Mode=ReadOnly",
            "Read-only by design: the schema assertion must not be able to write to what it inspects."),
        ("XE-Local-AI-Engine.Tests/Images/GeneratedImageStoreTests.cs", "{databasePath};Foreign Keys=True",
            "Pins the production posture explicitly, after this fixture once ran with enforcement off."),
        ("XE-Local-AI-Engine.Tests/HealthChecks/NodeSqliteHealthCheckTests.cs", "{dbPath};Pooling=False",
            "Subject of the test: each probe must open a fresh handle rather than reuse a pooled one."),
        ("XE-Local-AI-Engine.Client.Persistence.Tests/KnowledgeDowngradeSafetyServiceTests.cs", "{databasePath};Mode=ReadOnly",
            "Read-only by design: the count must not be able to alter what it counts."),
        ("XE-Local-AI-Engine.Client.Persistence.Tests/NativeSqliteVersionTests.cs", ":memory:",
            "In-memory database: the test reads the native library's version, not a file."),
        ("XE-Local-AI-Engine.Client.Persistence.Tests/Training/TrainingEvaluationEncryptionTests.cs", "{databasePath};Foreign Keys=False",
            "Models a raw database writer moving a row, which the declared reference would otherwise refuse."),
        ("XE-Local-AI-Engine.Client.Persistence.Tests/Training/TrainingRunEncryptionTests.cs", "{databasePath};Foreign Keys=False",
            "Models a raw database writer moving a row, which the declared reference would otherwise refuse."),
        ("XE-Local-AI-Engine.Client.Persistence.Tests/Scheduler/NodeSchedulerRegistrationTests.cs", ":memory:",
            "In-memory database: the registration assertions never open it.")
    ];

    /// <summary>The scan is pure and both tests need all of it, so one walk serves them.</summary>
    private static readonly Lazy<(string[] Files, (string File, string Tail, bool NamesProbe)[] Literals)> Scan = new(Walk, isThreadSafe: true);

    [Test]
    public void EveryConnectionString_InTheTestProjects_IsBareOrANamedException()
    {
        var (files, literals) = Scan.Value;

        AssertEx.NotEmpty(files, "The scan reached no source file; the walk is broken, not the tree.");
        AssertEx.NotEmpty(literals, $"The scan found no '{Marker}' literal at all; the walk is broken, not the tree.");

        var offenders = literals.Where(literal => !IsAllowed(literal))
                                .Select(literal => $"{literal.File}: \"{Marker}{literal.Tail}\"")
                                .ToArray();

        AssertEx.Empty(offenders,
            $"{ProbeName} clears the pool for the bare '{Marker}{{path}}' key only, so a file on the path from a "
            + $"probed test to its connection must not decorate it; otherwise the probe clears an empty pool and the "
            + $"at-rest scan reads stale bytes. Add the file and its literal to KnownDecoratedStrings with a reason if it is safe. "
            + $"Offenders:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Test]
    public void TheScan_ReachesTheFilesThatNeverNameTheProbe_AndItsExceptionsAreStillReal()
    {
        var (_, literals) = Scan.Value;

        // The blind spot this guard was widened to close: the shared factories build the string for the probed
        // suites without ever mentioning the probe, so a by-name scan saw none of these files.
        var silent = literals.Where(literal => !literal.NamesProbe).Select(literal => literal.File).Distinct(StringComparer.Ordinal).ToArray();
        AssertEx.True(silent.Length >= 50,
            $"The scan must reach the files that build a connection string without naming {ProbeName}; it found {silent.Length}.");

        var speaking = literals.Where(literal => literal.NamesProbe).Select(literal => literal.File).Distinct(StringComparer.Ordinal).ToArray();
        AssertEx.NotEmpty(speaking, $"The scan must also reach the files that do name {ProbeName}, or the rule has no subject.");

        var stale = KnownDecoratedStrings
                    .Where(known => !literals.Any(literal => string.Equals(literal.File, known.File, StringComparison.Ordinal)
                                                             && string.Equals(literal.Tail, known.Tail, StringComparison.Ordinal)))
                    .Select(known => $"{known.File}: \"{Marker}{known.Tail}\" — {known.Reason}")
                    .ToArray();

        AssertEx.Empty(stale,
            "An exception that no longer matches a literal widens the rule for nothing; delete it. "
            + $"Stale:{Environment.NewLine}{string.Join(Environment.NewLine, stale)}");
    }

    private static bool IsAllowed((string File, string Tail, bool NamesProbe) literal)
    {
        return IsBare(literal.Tail)
               || (!literal.NamesProbe
                   && KnownDecoratedStrings.Any(known => string.Equals(known.File, literal.File, StringComparison.Ordinal)
                                                         && string.Equals(known.Tail, literal.Tail, StringComparison.Ordinal)));
    }

    /// <summary>A single interpolation hole spanning the whole tail, i.e. <c>$"Data Source={path}"</c> and nothing else.</summary>
    private static bool IsBare(string tail)
    {
        if (tail.Length < 3 || !tail.StartsWith('{'))
        {
            return false;
        }

        var depth = 0;
        for (var index = 0; index < tail.Length; index++)
        {
            if (tail[index] == '{')
            {
                depth++;
            }
            else if (tail[index] == '}' && --depth == 0)
            {
                return index == tail.Length - 1;
            }
        }

        return false;
    }

    /// <summary>
    ///     The literal's text from after <c>Data Source=</c> to the quote that ends the string, reading over
    ///     quotes nested inside interpolation holes so <c>{Path.Combine(root, "x.sqlite")}</c> stays one hole.
    /// </summary>
    private static string ReadTail(string source, int start)
    {
        var tail = new StringBuilder();
        var depth = 0;
        var index = start;
        while (index < source.Length)
        {
            var character = source[index];
            if (character == '\n' || (character == '"' && depth == 0))
            {
                break;
            }

            if (depth == 0 && (character == '{' || character == '}') && index + 1 < source.Length && source[index + 1] == character)
            {
                // An escaped brace is literal text, never a hole: two placeholders keep it out of the bare shape.
                _ = tail.Append("__");
                index += 2;
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
            }

            _ = tail.Append(character);
            index++;
        }

        return tail.ToString();
    }

    private static (string[] Files, (string File, string Tail, bool NamesProbe)[] Literals) Walk()
    {
        var files = new List<string>();
        var literals = new List<(string File, string Tail, bool NamesProbe)>();

        foreach (var project in ScannedProjects)
        {
            foreach (var path in Directory.EnumerateFiles(RepositoryPaths.Combine(project), "*.cs", SearchOption.AllDirectories)
                                          .Order(StringComparer.Ordinal))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || Path.GetFileName(path).Equals(GuardFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');
                files.Add(relativePath);

                var text = File.ReadAllText(path);
                var namesProbe = text.Contains(ProbeName, StringComparison.Ordinal);
                var source = SourceCommentStripper.StripComments(text);
                for (var index = source.IndexOf(Marker, StringComparison.Ordinal);
                     index >= 0;
                     index = source.IndexOf(Marker, index + Marker.Length, StringComparison.Ordinal))
                {
                    literals.Add((relativePath, ReadTail(source, index + Marker.Length), namesProbe));
                }
            }
        }

        return ([.. files], [.. literals]);
    }
}
