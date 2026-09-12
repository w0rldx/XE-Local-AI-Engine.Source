namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The replacement for a project reference that cannot exist.
///     <para>
///         The container-runtime layer and External Apps live in the same assembly, so nothing stops a bridge type
///         from reaching straight into an instance store, a manifest or a deployment plan — it would compile. The
///         operator ruling says it must not: the bridge is a runtime-layer facility that External Apps happens to be
///         the first user of, and a bridge that knows what an external application IS cannot later serve anything
///         else. The token seam is the whole shape of the rule — <c>IContainerBridgeTokenVerifier</c> lives under
///         Containers, its one implementation under ExternalApps, and the dependency points that way only.
///     </para>
///     <para>
///         A raw-text scan rather than a type-graph assertion, matching
///         <c>ExternalAppsRuntimeIsolationArchitectureTests</c>: the thing being banned is a NAME appearing in code,
///         so a scan catches a using directive, a fully-qualified reference and a bare type name alike.
///     </para>
///     <para>
///         Comments are stripped first, and deliberately. A bridge doc comment naming <c>ExternalApps:Enabled</c> —
///         the other flag the listener is gated on — or explaining which feature is its first user documents the
///         seam rather than crossing it, and a guard that banned the prose too would be answered by deleting the
///         explanation instead of the dependency.
///     </para>
/// </summary>
public sealed class ContainerBridgeLayeringArchitectureTests
{
    /// <summary>
    ///     Every spelling that would mean the containers layer had learned about External Apps: the namespace, and
    ///     the prefix every type in that feature carries.
    /// </summary>
    private static readonly string[] BannedSymbols =
    [
        "Services.ExternalApps",
        "ExternalApp"
    ];

    [Test]
    public void NoContainersTypeReferencesExternalApps()
    {
        var guarded = GuardedDirectory();
        AssertEx.True(Directory.Exists(guarded), $"'{guarded}' does not exist, so this guard reads nothing whatever is written under it.");

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
            "The container-runtime layer must not know what an external application is. A bridge type needing "
            + "something from that feature takes it through a seam declared under Services/Containers and "
            + "implemented under Services/ExternalApps — IContainerBridgeTokenVerifier is the pattern: "
            + $"{string.Join("; ", offenders)}");
    }

    /// <summary>
    ///     The guard's own check. Without it a renamed directory, or detection that quietly stopped matching, would
    ///     read zero files and report a passing guard that could not fail.
    /// </summary>
    [Test]
    public void TheGuard_DetectsEachBannedSymbolAndIgnoresTheBridgesOwnNames()
    {
        foreach (var symbol in BannedSymbols)
        {
            AssertEx.Contains(FindBannedSymbols($"using XE_Local_AI_Engine.Client.{symbol}Instance;"), symbol);
            AssertEx.Empty(FindBannedSymbols($"// The listener opens only when {symbol}s:Enabled is also true."),
                $"A doc comment naming '{symbol}' documents the seam; it does not cross it.");
            AssertEx.Empty(FindBannedSymbols($"/// <summary>The first user of the bridge is {symbol}s.</summary>"),
                $"An XML doc comment naming '{symbol}' documents the seam; it does not cross it.");
            AssertEx.Empty(FindBannedSymbols($"/* {symbol}Instance lives one layer over. */"),
                $"A block comment naming '{symbol}' documents the seam; it does not cross it.");
        }

        AssertEx.Empty(FindBannedSymbols("""
                                            var caller = await _verifier.VerifyAsync(presented, context.RequestAborted);
                                            var endpoint = ContainerBridgeEndpointResolver.Resolve(options, desktop);
                                            context.Features.Set(new ContainerBridgeCaller(instanceId));
                                            builder.Services.AddSingleton<ContainerBridgeAddressWatcher>();
                                        """),
            "The bridge's own vocabulary must not trip the guard, or the slice that uses it cannot be written.");
    }

    private static IReadOnlyList<string> FindBannedSymbols(string text)
    {
        var code = StripComments(text);
        return [.. BannedSymbols.Where(symbol => code.Contains(symbol, StringComparison.Ordinal))];
    }

    /// <summary>
    ///     Removes line and block comments so only code is scanned. Deliberately simple — it does not model string
    ///     literals, because a banned name inside one is not a dependency either, and erring toward scanning LESS
    ///     never turns a real reference into a pass: a reference that compiles is code, and code is what remains.
    /// </summary>
    private static string StripComments(string text)
    {
        var withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    private static string GuardedDirectory()
    {
        return Path.Combine(RepositoryPaths.Root, "XE-Local-AI-Engine.Client.Application", "Services", "Containers");
    }
}
