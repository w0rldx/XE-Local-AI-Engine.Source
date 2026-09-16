namespace XE_Local_AI_Engine.Tests.Architecture;

using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The fence that keeps a third-party SDK out of the layers that must not name it (ruling P4).
///     <para>
///         <c>OllamaSharp</c>, <c>Docker.DotNet</c>, <c>Azure.*</c> and <c>Microsoft.Identity.Client</c> are provider
///         concerns. A host, application, persistence, agent or contracts type that names one has moved a runtime
///         decision out of the provider that owns it, and nothing in the project graph says so today: the packages
///         are referenced by <c>Client.Application</c> itself, so every one of these references compiles.
///     </para>
///     <para>
///         A raw-text scan rather than reflection over the compiled assemblies, matching
///         <c>ContainerBridgeLayeringArchitectureTests</c>: the thing being banned is a NAME appearing in code, so a
///         scan catches a using directive, a fully-qualified reference and a bare type name alike, and it needs no
///         assembly of the scanned project to be loadable from the test host. Comments are stripped first through the
///         shared <see cref="SourceCommentStripper" /> — prose explaining which SDK a seam replaces documents the
///         rule rather than breaking it — and string literals are deliberately NOT stripped: a banned name inside a
///         configuration key still describes a coupling, and over-inclusion beats a miss.
///     </para>
///     <para>
///         Matching is a plain ordinal substring test on each symbol. The trailing dot in <c>Azure.</c> is what makes
///         it safe: the repository's own <c>AzureFoundry*</c> types do not contain it, and a scan for every
///         <c>&lt;identifier-char&gt;Azure.</c> spelling across the five projects returns nothing today, so no
///         word-boundary rule is needed to keep the fence off the repository's own vocabulary.
///     </para>
///     <para>
///         Ratchet (P1): the three allowlists freeze the files that reference an SDK today so no NEW one can land.
///         <see cref="EveryAllowlistedFile_StillReferencesItsSdkToday" /> is the other half — an entry that stopped
///         violating is deleted, so the lists only shrink.
///     </para>
/// </summary>
public sealed class ThirdPartySdkBoundaryTests
{
    /// <summary>
    ///     The scanned projects and their non-vacuity floors, set a little under today's counts (625 / 1420 / 567 /
    ///     80 / 6) the same way <c>PlacementConventionTests</c> sets its own. A renamed directory or a broken walk
    ///     reads too few files and fails here, rather than reporting a fence that could not fire.
    /// </summary>
    private static readonly (string Project, int Floor)[] ScannedProjects =
    [
        ("XE-Local-AI-Engine.Client", 500),
        ("XE-Local-AI-Engine.Client.Application", 1200),
        ("XE-Local-AI-Engine.Client.Persistence", 500),
        ("XE-Local-AI-Engine.AI.Agent", 60),
        ("XE-Local-AI-Engine.AI.Contracts", 5)
    ];

    /// <summary>
    ///     Slice S5 moves this usage into <c>Providers.Ollama</c> behind <c>Providers.Abstractions</c>, removes the
    ///     two <c>OllamaSharp</c> package references and empties this list.
    /// </summary>
    private static readonly string[] OllamaSharpAllowlist =
    [
        "XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeDraftingExtensions.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Agents/Implementation/EmbeddingPlaybookRetrievalRanker.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Agents/Implementation/EmbeddingToolRelevanceSelector.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Capabilities/Implementation/ModelCapabilityProber.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Chat/IOllamaModelService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/OllamaModelService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/UnavailableOllamaModelService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Chat/OllamaModelDetails.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Drafting/Implementation/DefaultConfigDraftService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Knowledge/EmbeddingModelResolver.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Knowledge/KnowledgeChunkEmbedder.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Knowledge/KnowledgeSearchService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Memory/Implementation/MemorySemanticDeduplicator.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Models/ILocalModelCatalogService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Models/LocalModelCatalogService.cs",
        "XE-Local-AI-Engine.Client/Endpoints/LocalModels/V1/Mappers/LocalModelsMapper.cs"
    ];

    /// <summary>
    ///     The two files ADR 0004 (development-mode container execution, Docker stopgap) names as the sanctioned
    ///     Docker SDK surface. This list shrinks when that stopgap is replaced, not before.
    /// </summary>
    private static readonly string[] DockerDotNetAllowlist =
    [
        "XE-Local-AI-Engine.Client.Application/Services/Containers/Implementation/PullProgressAggregator.cs",
        "XE-Local-AI-Engine.Client.Application/Services/Sandbox/Container/Implementation/DockerDotNetRuntimeClient.cs"
    ];

    /// <summary>
    ///     The Azure SDK and MSAL surface as found on this baseline: every file is under
    ///     <c>Client.Application/Services/CloudProviders/</c>, which is where the Entra and Foundry integration lives.
    ///     No slice is scheduled to empty this list; a new entry means the cloud-provider seam leaked.
    /// </summary>
    private static readonly string[] AzureAndMsalAllowlist =
    [
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Auth/EntraAuthCodeConfidentialClientFactory.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Auth/EntraAuthCodeSignInCoordinator.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Auth/EntraDeviceCodeSignInCoordinator.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Auth/IEntraAuthCodeRedeemer.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Auth/MsalDelegatedTokenCredential.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/EntraCachePersistenceFailure.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/IEntraLiveCredentialCache.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/IEntraTokenCacheStore.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/AzureFoundryChatClientFactory.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/AzureFoundryErrorTranslatingChatClient.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/EntraBearerTokenPipelinePolicy.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/EntraLiveCredentialCache.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/EntraPersistenceFallbackCredential.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/EntraTokenCacheStore.cs"
    ];

    /// <summary>The three fences: what is banned, where it is still allowed, and what to do instead.</summary>
    private static readonly (string Name, string[] Symbols, string[] Allowlist, string Remedy)[] Fences =
    [
        ("OllamaSharp", ["OllamaSharp"], OllamaSharpAllowlist,
            "Reach Ollama through Providers.Abstractions; the SDK type belongs in Providers.Ollama (slice S5)."),
        ("Docker.DotNet", ["Docker.DotNet"], DockerDotNetAllowlist,
            "Go through IContainerRuntime; the Docker SDK stays behind the two files ADR 0004 names."),
        ("Azure / MSAL", ["Azure.", "Microsoft.Identity.Client"], AzureAndMsalAllowlist,
            "Go through the cloud-provider services under Services/CloudProviders; Azure and MSAL types stay there.")
    ];

    /// <summary>Constructs whose content only LOOKS like a comment. The reference after them must still be found.</summary>
    private static readonly (string Case, string Source, string Symbol)[] MustBeSeen =
    [
        ("URL in a regular string, then a real reference",
            """var endpoint = "https://localhost:11434"; _ = typeof(OllamaSharp.OllamaApiClient);""",
            "OllamaSharp"),
        ("double slash in a verbatim string, then a real reference",
            """var socket = @"npipe://./pipe/docker//engine"; _ = typeof(Docker.DotNet.DockerClient);""",
            "Docker.DotNet"),
        ("URL in a raw string, then a real reference",
            """"var docs = """see https://learn.microsoft.com/dotnet"""; _ = typeof(Azure.Identity.DefaultAzureCredential);"""",
            "Azure."),
        ("interpolation hole either side of a double slash, then a real reference",
            """var origin = $"{scheme}://{authority}"; _ = typeof(Microsoft.Identity.Client.PublicClientApplication);""",
            "Microsoft.Identity.Client")
    ];

    /// <summary>Prose naming an SDK. Documenting which dependency a seam replaces is not the dependency.</summary>
    private static readonly (string Case, string Source, string Symbol)[] MustNotBeSeen =
    [
        ("line comment", "// The OllamaSharp client moves into Providers.Ollama in slice S5.", "OllamaSharp"),
        ("XML doc comment", "/// <summary>Replaces the direct Docker.DotNet dependency.</summary>", "Docker.DotNet"),
        ("block comment", "/* Azure.Identity is reached through the cloud-provider service. */", "Azure."),
        ("comment after a URL string",
            """var endpoint = "https://localhost"; Configure(endpoint); // Microsoft.Identity.Client is not used here""",
            "Microsoft.Identity.Client")
    ];

    [Test]
    public void ProductionLayers_ReferenceNoUnlistedThirdPartySdk()
    {
        var offenders = Scan();

        foreach (var (name, _, allowlist, remedy) in Fences)
        {
            var unlisted = offenders[name].Where(path => !allowlist.Contains(path, StringComparer.Ordinal)).ToList();

            AssertEx.Empty(unlisted,
                $"'{name}' is fenced out of the host, application, persistence, agent and contracts layers. {remedy} "
                + "Do not add the file(s) below to the ratchet list — the list only shrinks:"
                + Environment.NewLine + string.Join(Environment.NewLine, unlisted));
        }
    }

    [Test]
    public void EveryAllowlistedFile_StillReferencesItsSdkToday()
    {
        var offenders = Scan();

        foreach (var (name, _, allowlist, _) in Fences)
        {
            var stale = allowlist.Where(path => !offenders[name].Contains(path)).ToList();

            AssertEx.Empty(stale,
                $"These files are on the '{name}' ratchet list but no longer reference it — the usage was removed and "
                + "the exemption outlived it, or the file was renamed. Delete the line(s) below:"
                + Environment.NewLine + string.Join(Environment.NewLine, stale));
        }
    }

    /// <summary>
    ///     The guard's own check. A scan that stopped matching, or one whose comment stripping ate the code after a
    ///     string literal, reads clean source everywhere and reports a fence that cannot fire.
    /// </summary>
    [Test]
    public void TheGuard_SeesAReferenceBehindEveryStringFormAndIgnoresProse()
    {
        foreach (var (name, source, symbol) in MustBeSeen)
        {
            AssertEx.True(References(source, symbol),
                $"The '{name}' case lost its '{symbol}' reference. A comment delimiter inside a string literal was "
                + $"read as a real comment, which is how this fence silently stops seeing code: {Strip(source)}");
        }

        foreach (var (name, source, symbol) in MustNotBeSeen)
        {
            AssertEx.False(References(source, symbol),
                $"The '{name}' case tripped on '{symbol}' inside prose. A comment naming an SDK documents the fence "
                + $"rather than crossing it, and a guard that fires on prose is one somebody deletes: {Strip(source)}");
        }

        foreach (var (name, symbols, allowlist, _) in Fences)
        {
            AssertEx.NotEmpty(symbols, $"Fence '{name}' bans nothing.");
            AssertEx.True(allowlist.SequenceEqual(allowlist.Order(StringComparer.Ordinal), StringComparer.Ordinal),
                $"Fence '{name}''s allowlist must stay sorted, one path per line, so a diff to it is readable.");
        }
    }

    /// <summary>
    ///     Walks the five projects once and returns, per fence, the repo-relative paths that name a banned symbol in
    ///     code. Skips <c>bin/</c> and <c>obj/</c>: build output is not source, and the generated files under them
    ///     would both inflate the floors and report references nobody wrote.
    /// </summary>
    private static IReadOnlyDictionary<string, SortedSet<string>> Scan()
    {
        var offenders = Fences.ToDictionary(fence => fence.Name,
            _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var (project, floor) in ScannedProjects)
        {
            var directory = Path.Combine(RepositoryPaths.Root, project);

            if (!Directory.Exists(directory))
            {
                throw new SkipTestException($"SKIPPED — '{project}' does not exist under '{RepositoryPaths.Root}', so this "
                                            + "fence would scan nothing and pass for the wrong reason. Fix the project list.");
            }

            var scanned = 0;

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepositoryPaths.Root, file).Replace('\\', '/');

                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                scanned++;
                var code = SourceCommentStripper.StripComments(File.ReadAllText(file));

                foreach (var (name, symbols, _, _) in Fences)
                {
                    if (symbols.Any(symbol => code.Contains(symbol, StringComparison.Ordinal)))
                    {
                        offenders[name].Add(relative);
                    }
                }
            }

            AssertEx.True(scanned >= floor,
                $"Scanned only {scanned} .cs files under '{project}' (floor {floor}). The project was renamed, moved or "
                + "emptied, and this fence is reading far less than it is supposed to guard.");
        }

        return offenders;
    }

    private static bool References(string source, string symbol)
    {
        return Strip(source).Contains(symbol, StringComparison.Ordinal);
    }

    private static string Strip(string source)
    {
        return SourceCommentStripper.StripComments(source);
    }
}
