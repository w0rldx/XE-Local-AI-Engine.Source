namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The replacement for a compile error that cannot exist.
///     <para>
///         The application-container runtime derives from Development Mode's client, so
///         <c>CreateContainerAsync</c>, <c>InspectContainerAsync</c> and <c>ExecuteAsync</c> resolve and compile from
///         an <c>IContainerRuntime</c> reference. They do the wrong thing silently: Development Mode's create carries
///         no environment, no published ports, no network, no healthcheck and no restart policy, so an application
///         created through it would come up empty and stay up unmanaged, and its specification record is the one a
///         hardening contract reads as the complete statement of what must be true.
///     </para>
///     <para>
///         Nothing in the type system says so, which is why this file does. The banned list is fixed by ruling R1-2
///         and is not widened by the write-probe: that member takes no command, so a type wanting to run a command of
///         its own choosing still has to name <c>ExecuteAsync</c> and is still caught here.
///     </para>
///     <para>
///         Comments are stripped first through the shared <see cref="SourceCommentStripper" />, the same helper
///         <c>ContainerBridgeLayeringArchitectureTests</c> uses, and for the same reason: a doc comment naming
///         <c>ExecuteAsync</c> to say why an application container must not call it documents the rule rather than
///         breaking it. String literals stay visible — the stripper recognises a comment delimiter only in code, so
///         a <c>//</c> inside a URL can never erase the reference that follows it on the line.
///     </para>
/// </summary>
public sealed class ExternalAppsRuntimeIsolationArchitectureTests
{
    /// <summary>
    ///     Development Mode's container surface, exactly as ruled. Do not widen and do not narrow: each name is either
    ///     a member that silently does the wrong thing for an application container, or a record whose meaning belongs
    ///     to the sandbox hardening contract.
    /// </summary>
    private static readonly string[] BannedSymbols =
    [
        "ExecuteAsync",
        "DockerExecutionRequest",
        "DockerContainerSpecification",
        "CreateContainerAsync"
    ];

    /// <summary>
    ///     Constructs whose content only LOOKS like a comment, each followed on the same line by a real call to a
    ///     banned member. A stripper that reads the <c>//</c> in a URL as a comment start erases the call behind it,
    ///     and the guard passes having seen nothing.
    /// </summary>
    private static readonly (string Case, string Source, string Symbol)[] StringShapedReferences =
    [
        ("URL in a regular string, then a real call",
            """var probe = "https://localhost/health"; await client.ExecuteAsync(request, cancellationToken);""",
            "ExecuteAsync"),
        ("double slash in a verbatim string, then a real call",
            """var socket = @"npipe://./pipe/docker//engine"; await client.CreateContainerAsync(parameters, cancellationToken);""",
            "CreateContainerAsync"),
        ("URL in a raw string, then a real reference",
            """"var docs = """see https://learn.microsoft.com/dotnet"""; var request = new DockerExecutionRequest(command);"""",
            "DockerExecutionRequest"),
        ("interpolation hole either side of a double slash, then a real reference",
            """var origin = $"{scheme}://{authority}"; var spec = new DockerContainerSpecification(image);""",
            "DockerContainerSpecification")
    ];

    [Test]
    public void NoExternalAppsTypeReferencesDevelopmentModesContainerSurface()
    {
        var guarded = GuardedDirectory();

        // The directory exists today (the External Apps feature landed 2026-09-11), so this branch is defensive:
        // if it is ever removed or renamed, the guard must report Skipped. Completing with an empty offender list
        // would be indistinguishable in a pass/fail summary from a real scan that found nothing.
        if (!Directory.Exists(guarded))
        {
            throw new SkipTestException("SKIPPED — Services/ExternalApps does not exist; this guard has nothing to scan. "
                                        + "A pass here would look exactly like a scan that read every file and found nothing.");
        }

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(guarded, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var symbol in FindBannedSymbols(text))
            {
                offenders.Add($"{Path.GetRelativePath(guarded, file).Replace('\\', '/')} references '{symbol}'");
            }
        }

        AssertEx.Empty(offenders,
            "Application containers use the container-runtime layer's own names — RunContainerAsync, InspectAsync, "
            + "CreateNetworkAsync(ContainerNetworkSpecification), StopContainerAsync(id, TimeSpan, ct) and "
            + "ProbeWritablePathAsync. Development Mode's members compile from the derived interface and do the wrong "
            + $"thing silently: {string.Join("; ", offenders)}");
    }

    /// <summary>
    ///     The guard's own check. Without it, detection that quietly stopped matching — a renamed path, a comment
    ///     stripper that ate the code after a string literal — would read source it no longer understands and report
    ///     a passing guard that could not fail.
    /// </summary>
    [Test]
    public void TheGuard_DetectsEachBannedSymbolAndIgnoresTheRuntimeLayersOwnNames()
    {
        AssertEx.True(Directory.Exists(Path.GetDirectoryName(GuardedDirectory())),
            $"'{Path.GetDirectoryName(GuardedDirectory())}' does not exist, so the path this guard scans no longer "
            + "points at the services folder and the guard reads nothing whatever is written under it.");

        foreach (var symbol in BannedSymbols)
        {
            AssertEx.Contains(FindBannedSymbols($"        await client.{symbol}(request, cancellationToken);"), symbol);
        }

        foreach (var symbol in BannedSymbols)
        {
            AssertEx.Empty(FindBannedSymbols($"// Development Mode's {symbol} does the wrong thing for an application container."),
                $"A comment naming '{symbol}' explains the rule; it does not break it.");
            AssertEx.Empty(FindBannedSymbols($"/// <summary>Never route an application container through {symbol}.</summary>"),
                $"An XML doc comment naming '{symbol}' explains the rule; it does not break it.");
            AssertEx.Empty(FindBannedSymbols($"/* {symbol} belongs to the sandbox hardening contract. */"),
                $"A block comment naming '{symbol}' explains the rule; it does not break it.");
        }

        foreach (var (name, source, symbol) in StringShapedReferences)
        {
            AssertEx.Contains(FindBannedSymbols(source), symbol,
                $"The '{name}' case lost its '{symbol}' reference. A comment delimiter inside a string literal was "
                + "read as a real comment, and every reference that followed it on the line went unseen.");
        }

        AssertEx.Empty(FindBannedSymbols("""
                                             var id = await runtime.RunContainerAsync(specification, cancellationToken);
                                             var inspection = await runtime.InspectAsync(id, cancellationToken);
                                             await runtime.CreateNetworkAsync(network, cancellationToken);
                                             await runtime.StopContainerAsync(id, grace, cancellationToken);
                                             await runtime.ProbeWritablePathAsync(id, "/data", cancellationToken);
                                             var summaries = await runtime.ListContainersDetailedAsync(labels, cancellationToken);
                                         """),
            "The runtime layer's own vocabulary must not trip the guard, or the slice that uses it cannot be written.");
    }

    /// <summary>
    ///     Whole-word matches only, over code only. <c>\b</c> on both ends means a banned name embedded in a longer
    ///     identifier — a hypothetical <c>MyCreateContainerAsyncHelper</c> — does NOT match, because neither end of the
    ///     name sits on a word boundary there. A guard that fired on names it was not given is one somebody eventually
    ///     deletes, so it fires only on the banned member itself, and only where it is written as code.
    /// </summary>
    private static IReadOnlyList<string> FindBannedSymbols(string text)
    {
        var code = SourceCommentStripper.StripComments(text);

        return
        [
            .. BannedSymbols.Where(symbol => Regex.IsMatch(code,
                @"\b" + Regex.Escape(symbol) + @"\b",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)))
        ];
    }

    private static string GuardedDirectory()
    {
        return Path.Combine(FindRepositoryRoot(), "XE-Local-AI-Engine.Client.Application", "Services", "ExternalApps");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "XE-Local-AI-Engine.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("The repository root containing XE-Local-AI-Engine.slnx was not found.");
    }
}
