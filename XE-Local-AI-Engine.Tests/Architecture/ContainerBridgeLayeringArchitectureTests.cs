namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Architecture.Support;
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
///         Comments are stripped first, and deliberately, through the shared <see cref="SourceCommentStripper" />.
///         A bridge doc comment naming <c>ExternalApps:Enabled</c> — the other flag the listener is gated on — or
///         explaining which feature is its first user documents the seam rather than crossing it, and a guard that
///         banned the prose too would be answered by deleting the explanation instead of the dependency. String
///         literals stay visible: a banned name in one still describes a coupling, and the shared stripper is what
///         makes that distinction safe — the regex this file used to carry read a <c>//</c> inside a URL literal as
///         a comment start and erased every reference that followed it on that line.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
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

    /// <summary>
    ///     Constructs whose content only LOOKS like a comment, each followed on the same line by a real reference.
    ///     These are the cases the old private regex stripper got wrong: it erased the rest of the line after the
    ///     <c>//</c> in a URL, so the reference behind it passed a guard that never saw it.
    /// </summary>
    private static readonly (string Case, string Source, string Symbol)[] StringShapedReferences =
    [
        ("URL in a regular string, then a real reference",
            """var docs = "https://localhost/bridge"; var store = services.GetRequiredService<IExternalAppInstanceStore>();""",
            "ExternalApp"),
        ("double slash in a verbatim string, then a real reference",
            """var socket = @"npipe://./pipe/bridge//engine"; using XE_Local_AI_Engine.Client.Application.Services.ExternalApps;""",
            "Services.ExternalApps"),
        ("URL in a raw string, then a real reference",
            """"var docs = """see https://learn.microsoft.com/dotnet"""; var plan = ExternalAppDeploymentPlan.Empty;"""",
            "ExternalApp"),
        ("interpolation hole either side of a double slash, then a real reference",
            """var origin = $"{scheme}://{authority}"; var manifest = ExternalAppManifest.Parse(text);""",
            "ExternalApp")
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

        foreach (var (name, source, symbol) in StringShapedReferences)
        {
            AssertEx.Contains(FindBannedSymbols(source), symbol,
                $"The '{name}' case lost its '{symbol}' reference. A comment delimiter inside a string literal was "
                + "read as a real comment, and every reference that followed it on the line went unseen.");
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
        var code = SourceCommentStripper.StripComments(text);
        return [.. BannedSymbols.Where(symbol => code.Contains(symbol, StringComparison.Ordinal))];
    }

    private static string GuardedDirectory()
    {
        return Path.Combine(RepositoryPaths.Root, "XE-Local-AI-Engine.Client.Application", "Services", "Containers");
    }
}
