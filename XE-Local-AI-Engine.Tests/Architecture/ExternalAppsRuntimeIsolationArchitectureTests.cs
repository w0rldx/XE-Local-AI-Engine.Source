namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
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

    [Test]
    public void NoExternalAppsTypeReferencesDevelopmentModesContainerSurface()
    {
        var guarded = GuardedDirectory();
        var offenders = new List<string>();

        // The directory does not exist until the slice that fills it lands, and that is the point of shipping the
        // guard first: it is the state where a new file can be added without anybody remembering the rule.
        if (Directory.Exists(guarded))
        {
            foreach (var file in Directory.EnumerateFiles(guarded, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (var symbol in FindBannedSymbols(text))
                {
                    offenders.Add($"{Path.GetRelativePath(guarded, file).Replace('\\', '/')} references '{symbol}'");
                }
            }
        }

        AssertEx.Empty(offenders,
            "Application containers use the container-runtime layer's own names — RunContainerAsync, InspectAsync, "
            + "CreateNetworkAsync(ContainerNetworkSpecification), StopContainerAsync(id, TimeSpan, ct) and "
            + "ProbeWritablePathAsync. Development Mode's members compile from the derived interface and do the wrong "
            + $"thing silently: {string.Join("; ", offenders)}");
    }

    /// <summary>
    ///     The guard's own check. Until <c>Services/ExternalApps/</c> exists the assertion above reads zero files, so
    ///     without this the suite would report a passing guard that could not fail — and would go on doing so if the
    ///     project were renamed or the detection quietly stopped matching.
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
    ///     Whole-word matches only. <c>\b</c> on both ends means a banned name embedded in a longer identifier — a
    ///     hypothetical <c>MyCreateContainerAsyncHelper</c> — does NOT match, because neither end of the name sits on
    ///     a word boundary there. A guard that fired on names it was not given is one somebody eventually deletes, so
    ///     it fires only on the banned member itself.
    /// </summary>
    private static IReadOnlyList<string> FindBannedSymbols(string text)
    {
        return [.. BannedSymbols.Where(symbol => Regex.IsMatch(text,
            @"\b" + Regex.Escape(symbol) + @"\b",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)))];
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
