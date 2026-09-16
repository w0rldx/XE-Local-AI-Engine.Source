namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Capabilities;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.WindowsLauncher;
using Extensions = Microsoft.Extensions.Hosting.Extensions;

/// <summary>
/// Freezes the layer-dependency direction of the node solution so that an
/// accidental upward reference (a leaf provider reaching back into the host,
/// application, or persistence layer) fails the build instead of shipping.
/// </summary>
public sealed class LayerDependencyTests
{
    // Namespace roots used as forbidden dependency targets. NetArchTest matches
    // on the full type namespace, and the host (Client) and application
    // (Client.Application) assemblies share the same "XE_Local_AI_Engine.Client"
    // root namespace, so that single prefix covers both. Persistence lives under
    // the distinct "...Client.Persistence" sub-namespace.
    // Non-vacuity floors for the two build-file scans below, set under the counts measured on 2026-09-16 (6
    // repository .props/.targets files, 20 production projects) so a planned addition or removal is not brittle.
    // A floor equal to its measurement is not rounded down: deleting one .props file would turn a real regression
    // into a red herring about the floor.
    private const int RepositoryBuildCustomizationFileFloor = 5;
    private const int ApprovedProjectReferencesFloor = 15;

    private const string ClientNamespace = "XE_Local_AI_Engine.Client";
    private const string PersistenceNamespace = "XE_Local_AI_Engine.Client.Persistence";
    private const string ApplicationNamespace = "XE_Local_AI_Engine.Client.Application";

    private const string OllamaNamespace = "XE_Local_AI_Engine.Providers.Ollama";

    // CAUTION for anyone adding a namespace-prefix rule here: "XE_Local_AI_Engine.Providers.OpenAICompat" is a string
    // PREFIX of "XE_Local_AI_Engine.Providers.OpenAICompatible.Core", and NetArchTest matches namespaces by prefix. So
    // forbidding OpenAICompatNamespace also forbids the shared transport core — correct for Abstractions (which must
    // depend on neither), and WRONG for LlamaServer, which legitimately depends on the core. The exact-name
    // assembly-reference maps above are the authoritative guard for those edges; do not add OpenAICompatNamespace to a
    // provider's forbidden list.
    private const string OpenAICompatNamespace = "XE_Local_AI_Engine.Providers.OpenAICompat";
    private const string LlamaServerNamespace = "XE_Local_AI_Engine.Providers.LlamaServer";
    private const string HuggingFaceNamespace = "XE_Local_AI_Engine.Providers.HuggingFace";
    private const string CodexOAuthNamespace = "XE_Local_AI_Engine.Providers.CodexOAuth";
    private const string CapabilitiesNamespace = "XE_Local_AI_Engine.Providers.Capabilities";
    private const string StableDiffusionCppNamespace = "XE_Local_AI_Engine.Providers.StableDiffusionCpp";
    private const string WhisperCppNamespace = "XE_Local_AI_Engine.Providers.WhisperCpp";
    private const string AbstractionsNamespace = "XE_Local_AI_Engine.Providers.Abstractions";
    private const string AiAgentNamespace = "XE_Local_AI_Engine.AI.Agent";

    // Marker types anchor each assembly so we test the real compiled IL, not a
    // namespace string. Every marker is a verified public type in its assembly.
    private static readonly Assembly OllamaAssembly = typeof(OllamaLocalModelProvider).Assembly;
    private static readonly Assembly OpenAICompatAssembly = typeof(ExternalOpenAiModelProvider).Assembly;
    private static readonly Assembly OpenAICompatibleCoreAssembly = typeof(OpenAICompatibleClientFactory).Assembly;
    private static readonly Assembly LlamaServerAssembly = typeof(IInstalledRuntimeStore).Assembly;
    private static readonly Assembly HuggingFaceAssembly = typeof(HuggingFaceOptions).Assembly;
    private static readonly Assembly CodexOAuthAssembly = typeof(ICodexOAuthChatClientFactory).Assembly;
    private static readonly Assembly CapabilitiesAssembly = typeof(CapabilitiesServiceCollectionExtensions).Assembly;
    private static readonly Assembly StableDiffusionCppAssembly = typeof(IStableDiffusionBinaryManager).Assembly;
    private static readonly Assembly TrainingAssembly = typeof(ITrainingRuntimeService).Assembly;
    private static readonly Assembly WhisperCppAssembly = typeof(WhisperCppReleasePins).Assembly;
    private static readonly Assembly AbstractionsAssembly = typeof(ILocalModelProvider).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(MessageRole).Assembly;
    private static readonly Assembly AiAgentAssembly = typeof(IInvocationAgentFactory).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(RuntimePackageValidationResult).Assembly;
    private static readonly Assembly PersistenceAssembly = typeof(NodeChatDbContext).Assembly;
    private static readonly Assembly HostAssembly = typeof(WorkerHealthCheck).Assembly;
    private static readonly Assembly ServiceDefaultsAssembly = typeof(Extensions).Assembly;
    private static readonly Assembly WindowsLauncherAssembly = typeof(WindowsLauncherApplication).Assembly;

    private static readonly IReadOnlyDictionary<string, string[]> ApprovedProjectReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["XE-Local-AI-Engine.AI.Contracts"] = [],
            ["XE-Local-AI-Engine.AI.Agent"] =
            [
                "XE-Local-AI-Engine.AI.Contracts",
                "XE-Local-AI-Engine.Providers.Abstractions"
            ],
            ["XE-Local-AI-Engine.Client"] =
            [
                "XE-Local-AI-Engine.AI.Agent",
                "XE-Local-AI-Engine.AI.Contracts",
                "XE-Local-AI-Engine.Client.Application",
                "XE-Local-AI-Engine.Client.Persistence",
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.Ollama",
                "XE-Local-AI-Engine.ServiceDefaults"
            ],
            ["XE-Local-AI-Engine.Client.Application"] =
            [
                "XE-Local-AI-Engine.AI.Agent",
                "XE-Local-AI-Engine.AI.Contracts",
                "XE-Local-AI-Engine.Client.Persistence",
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.Capabilities",
                "XE-Local-AI-Engine.Providers.CodexOAuth",
                "XE-Local-AI-Engine.Providers.HuggingFace",
                "XE-Local-AI-Engine.Providers.LlamaServer",
                "XE-Local-AI-Engine.Providers.Ollama",
                "XE-Local-AI-Engine.Providers.OpenAICompat",
                // The composition layer joins the shared wire layer for ONE reason (2026-08-27): the encrypted
                // external-provider store normalizes a saved base URL with the same OpenAICompatibleBaseAddress the
                // outbound guard pins against. A private copy of that normalizer in the application layer is precisely
                // how the stored value and the pinned value would come to disagree. Note the ordering trap below: this
                // name is a PREFIX of nothing, but "…Providers.OpenAICompat" IS a prefix of this one.
                "XE-Local-AI-Engine.Providers.OpenAICompatible.Core",
                "XE-Local-AI-Engine.Providers.StableDiffusionCpp",
                "XE-Local-AI-Engine.Providers.Training",
                "XE-Local-AI-Engine.Providers.WhisperCpp",
                "XE-Local-AI-Engine.ServiceDefaults"
            ],
            ["XE-Local-AI-Engine.Client.Persistence"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Client.Testing"] =
            [
                "XE-Local-AI-Engine.Client",
                "XE-Local-AI-Engine.Client.Application"
            ],
            ["XE-Local-AI-Engine.WindowsLauncher"] = [],
            ["XE-Local-AI-Engine.AppHost"] = ["XE-Local-AI-Engine.Client"],
            ["XE-Local-AI-Engine.ServiceDefaults"] = ["XE-Local-AI-Engine.AI.Contracts"],
            ["XE-Local-AI-Engine.Providers.Abstractions"] = [],
            ["XE-Local-AI-Engine.Providers.Capabilities"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.CodexOAuth"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.HuggingFace"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            // DELIBERATE, reviewed second edge (2026-08-27): both OpenAI-compatible providers build on the shared wire
            // layer instead of each carrying a private copy of the client construction and the request-body patch
            // discipline — a duplicated copy is how the two would drift into sending different bodies for the same
            // intent. OpenAICompatible.Core is a LEAF with no project references of its own, so this widens the graph
            // by one downward edge and does not weaken the rule that matters: no provider references a sibling provider.
            ["XE-Local-AI-Engine.Providers.LlamaServer"] =
            [
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.OpenAICompatible.Core"
            ],
            ["XE-Local-AI-Engine.Providers.Ollama"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.OpenAICompat"] =
            [
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.OpenAICompatible.Core"
            ],
            ["XE-Local-AI-Engine.Providers.OpenAICompatible.Core"] = [],
            ["XE-Local-AI-Engine.Providers.StableDiffusionCpp"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.Training"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.WhisperCpp"] = ["XE-Local-AI-Engine.Providers.Abstractions"]
        };

    // Test and support projects, keyed by REPOSITORY-RELATIVE CSPROJ PATH rather than by project name: the negative-fence
    // probe below lives nested inside its parent test project's directory, so the "<name>/<name>.csproj" convention the
    // production dictionary relies on cannot address it.
    //
    // This is a second, parallel dictionary rather than more keys in ApprovedProjectReferences: that one is cross-checked
    // 1:1 against the solution's /Src folder membership, so a non-/Src key would fail that check on its first run.
    // XE-Local-AI-Engine.Client.Testing is a /Src member and is therefore pinned there, not here — pinning it twice would
    // let the two copies disagree.
    private const string NegativeFenceProbeProject = "XE-Local-AI-Engine.Client.Persistence.NegativeFence";

    private static readonly IReadOnlyDictionary<string, string[]> ApprovedTestProjectReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["XE-Local-AI-Engine.AI.Agent.Tests/XE-Local-AI-Engine.AI.Agent.Tests.csproj"] =
            [
                "XE-Local-AI-Engine.AI.Agent",
                "XE-Local-AI-Engine.Providers.Abstractions"
            ],
            ["XE-Local-AI-Engine.Client.Persistence.Tests/XE-Local-AI-Engine.Client.Persistence.Tests.csproj"] =
            [
                "XE-Local-AI-Engine.Client",
                "XE-Local-AI-Engine.Client.Application",
                "XE-Local-AI-Engine.Client.Persistence"
            ],
            // Built on demand by PersistenceEncryptionTests to prove that persistence entities stay internal, so it is
            // deliberately not a solution member and nothing references it. Its single reference is the point of the probe.
            ["XE-Local-AI-Engine.Client.Persistence.Tests/NegativeFence/XE-Local-AI-Engine.Client.Persistence.NegativeFence.csproj"] =
            [
                "XE-Local-AI-Engine.Client.Persistence"
            ],
            ["XE-Local-AI-Engine.Tests.E2ETests/XE-Local-AI-Engine.Tests.E2ETests.csproj"] =
            [
                "XE-Local-AI-Engine.Client",
                "XE-Local-AI-Engine.Client.Application",
                "XE-Local-AI-Engine.Client.Persistence",
                "XE-Local-AI-Engine.Client.Testing",
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.Ollama",
                "XE-Local-AI-Engine.Testing.FakeOllama"
            ],
            // Both fakes are deliberately reference-free: a fake that reaches into the code under test stops being a fake.
            ["XE-Local-AI-Engine.Testing.FakeOllama/XE-Local-AI-Engine.Testing.FakeOllama.csproj"] = [],
            ["XE-Local-AI-Engine.Testing.FakeDocker/XE-Local-AI-Engine.Testing.FakeDocker.csproj"] = [],
            ["XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj"] =
            [
                "XE-Local-AI-Engine.Client",
                "XE-Local-AI-Engine.Client.Application",
                "XE-Local-AI-Engine.Client.Testing",
                "XE-Local-AI-Engine.Providers.Capabilities",
                "XE-Local-AI-Engine.Providers.CodexOAuth",
                "XE-Local-AI-Engine.Providers.HuggingFace",
                "XE-Local-AI-Engine.Providers.LlamaServer",
                "XE-Local-AI-Engine.Providers.Ollama",
                "XE-Local-AI-Engine.Providers.OpenAICompat",
                "XE-Local-AI-Engine.Providers.OpenAICompatible.Core",
                "XE-Local-AI-Engine.Providers.StableDiffusionCpp",
                "XE-Local-AI-Engine.Providers.Training",
                "XE-Local-AI-Engine.Providers.WhisperCpp",
                "XE-Local-AI-Engine.ServiceDefaults",
                "XE-Local-AI-Engine.Testing.FakeDocker",
                "XE-Local-AI-Engine.Testing.FakeOllama",
                "XE-Local-AI-Engine.WindowsLauncher"
            ]
        };

    private static readonly IReadOnlyDictionary<Assembly, string[]> ApprovedInternalAssemblyReferences =
        new Dictionary<Assembly, string[]>
        {
            [ContractsAssembly] = [],
            [AiAgentAssembly] =
            [
                "XE-Local-AI-Engine.AI.Contracts",
                "XE-Local-AI-Engine.Providers.Abstractions"
            ],
            [PersistenceAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [ServiceDefaultsAssembly] = ["XE-Local-AI-Engine.AI.Contracts"],
            [WindowsLauncherAssembly] = [],
            [AbstractionsAssembly] = [],
            [CapabilitiesAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [CodexOAuthAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [HuggingFaceAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [LlamaServerAssembly] =
            [
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.OpenAICompatible.Core"
            ],
            [OllamaAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [OpenAICompatAssembly] =
            [
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.OpenAICompatible.Core"
            ],
            [OpenAICompatibleCoreAssembly] = [],
            [StableDiffusionCppAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [TrainingAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [WhisperCppAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [ApplicationAssembly] =
            [
                "XE-Local-AI-Engine.AI.Agent",
                "XE-Local-AI-Engine.AI.Contracts",
                "XE-Local-AI-Engine.Client.Persistence",
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.Capabilities",
                "XE-Local-AI-Engine.Providers.CodexOAuth",
                "XE-Local-AI-Engine.Providers.HuggingFace",
                "XE-Local-AI-Engine.Providers.LlamaServer",
                "XE-Local-AI-Engine.Providers.Ollama",
                "XE-Local-AI-Engine.Providers.OpenAICompat",
                // See the project-reference list above for why the composition layer reaches the shared wire layer.
                "XE-Local-AI-Engine.Providers.OpenAICompatible.Core",
                "XE-Local-AI-Engine.Providers.StableDiffusionCpp",
                "XE-Local-AI-Engine.Providers.Training",
                "XE-Local-AI-Engine.Providers.WhisperCpp",
                "XE-Local-AI-Engine.ServiceDefaults"
            ],
            [HostAssembly] =
            [
                // The executable Host is today's integration/composition boundary. These concrete-provider assembly
                // references freeze that current state; their presence is not a claim that the inward architecture is
                // already cleaner than the compiled Host actually is.
                "XE-Local-AI-Engine.AI.Agent",
                "XE-Local-AI-Engine.AI.Contracts",
                "XE-Local-AI-Engine.Client.Application",
                "XE-Local-AI-Engine.Client.Persistence",
                "XE-Local-AI-Engine.Providers.Abstractions",
                "XE-Local-AI-Engine.Providers.CodexOAuth",
                "XE-Local-AI-Engine.Providers.LlamaServer",
                "XE-Local-AI-Engine.Providers.Ollama",
                "XE-Local-AI-Engine.Providers.StableDiffusionCpp",
                "XE-Local-AI-Engine.Providers.Training",
                // The Client ASSEMBLY acquires this reference because its transcription endpoints bind the provider's
                // contracts, even though the Client csproj does not reference the project (references are transitive
                // through Client.Application) — the same position StableDiffusionCpp already holds.
                "XE-Local-AI-Engine.Providers.WhisperCpp",
                "XE-Local-AI-Engine.ServiceDefaults"
            ]
        };

    [Test]
    public void ProductionProjects_HaveOnlyTheApprovedDirectProjectReferences()
    {
        var solution = XDocument.Load(RepositoryPaths.Combine("XE-Local-AI-Engine.slnx"));
        var productionProjects = solution.Descendants("Project")
                                         .Where(project => project.Ancestors("Folder")
                                                                  .Select(folder => (string?)folder.Attribute("Name"))
                                                                  .Any(name => name is not null && name.StartsWith("/Src", StringComparison.Ordinal)))
                                         .Select(project => (string?)project.Attribute("Path"))
                                         .Where(path => path is not null)
                                         .Select(path => Path.GetFileNameWithoutExtension(path!.Replace('\\', '/')))
                                         .Order(StringComparer.Ordinal)
                                         .ToArray();
        var approvedProjects = ApprovedProjectReferences.Keys.Order(StringComparer.Ordinal).ToArray();

        AssertExactReferences("Production projects in XE-Local-AI-Engine.slnx", approvedProjects, productionProjects);

        foreach (var (projectName, approvedReferences) in ApprovedProjectReferences)
        {
            var projectPath = RepositoryPaths.Combine(projectName, $"{projectName}.csproj");
            var project = XDocument.Load(projectPath);
            var actualReferences = project.Descendants("ProjectReference")
                                          .Select(reference => (string?)reference.Attribute("Include"))
                                          .Where(include => !string.IsNullOrWhiteSpace(include))
                                          .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                                          .Order(StringComparer.Ordinal)
                                          .ToArray();

            AssertExactReferences(projectName, approvedReferences, actualReferences);
        }
    }

    [Test]
    public void TestAndSupportProjects_HaveOnlyTheApprovedDirectProjectReferences()
    {
        var solution = XDocument.Load(RepositoryPaths.Combine("XE-Local-AI-Engine.slnx"));
        var solutionTestProjects = solution.Descendants("Project")
                                           .Where(project => project.Ancestors("Folder")
                                                                    .Select(folder => (string?)folder.Attribute("Name"))
                                                                    .Any(name => name is not null && name.StartsWith("/Tests/", StringComparison.Ordinal)))
                                           .Select(project => (string?)project.Attribute("Path"))
                                           .Where(path => path is not null)
                                           .Select(path => Path.GetFileNameWithoutExtension(path!.Replace('\\', '/')))
                                           .Append(NegativeFenceProbeProject)
                                           .Order(StringComparer.Ordinal)
                                           .ToArray();
        var approvedProjects = ApprovedTestProjectReferences.Keys
                                                            .Select(path => Path.GetFileNameWithoutExtension(path))
                                                            .Order(StringComparer.Ordinal)
                                                            .ToArray();

        // Catches a new test or fixture project that ships unpinned, which is what makes the per-project loop below
        // worth anything: without this, an unlisted project is simply never scanned.
        AssertExactReferences("Test and support projects in XE-Local-AI-Engine.slnx", approvedProjects, solutionTestProjects);

        foreach (var (projectPath, approvedReferences) in ApprovedTestProjectReferences)
        {
            var project = XDocument.Load(RepositoryPaths.Combine(projectPath.Split('/')));
            var actualReferences = project.Descendants("ProjectReference")
                                          .Select(reference => (string?)reference.Attribute("Include"))
                                          .Where(include => !string.IsNullOrWhiteSpace(include))
                                          .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                                          .Order(StringComparer.Ordinal)
                                          .ToArray();

            AssertExactReferences(Path.GetFileNameWithoutExtension(projectPath), approvedReferences, actualReferences);
        }
    }

    [Test]
    public void ProductionAssemblies_HaveOnlyTheApprovedInternalAssemblyReferences()
    {
        foreach (var (assembly, approvedReferences) in ApprovedInternalAssemblyReferences)
        {
            var actualReferences = assembly.GetReferencedAssemblies()
                                           .Select(reference => reference.Name)
                                           .Where(name => name is not null && name.StartsWith("XE-Local-AI-Engine.", StringComparison.Ordinal))
                                           .Select(name => name!)
                                           .Order(StringComparer.Ordinal)
                                           .ToArray();

            AssertOnlyApprovedReferences(assembly.GetName().Name ?? assembly.FullName ?? "unknown assembly", approvedReferences, actualReferences);
        }
    }

    [Test]
    public void RepositoryBuildCustomization_DoesNotInjectProjectReferences()
    {
        var scannedFiles = EnumerateRepositoryBuildCustomizationFiles(RepositoryPaths.Root).ToArray();

        AssertEx.True(scannedFiles.Length >= RepositoryBuildCustomizationFileFloor,
            $"Scanned {scannedFiles.Length} repository-controlled .props/.targets files below '{RepositoryPaths.Root}', "
            + $"below the non-vacuity floor of {RepositoryBuildCustomizationFileFloor}. A scan that walked nothing "
            + "would report no injected ProjectReference for the wrong reason.");

        var declarations = scannedFiles
                           .SelectMany(path => XDocument.Load(path)
                                                        .Descendants()
                                                        .Where(element => element.Name.LocalName == "ProjectReference")
                                                        .Select(reference =>
                                                            $"{Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/')}: "
                                                            + $"ProjectReference Include=\"{(string?)reference.Attribute("Include") ?? "<missing>"}\""))
                           .Order(StringComparer.Ordinal)
                           .ToArray();

        AssertEx.Empty(declarations,
            "Repository-controlled .props/.targets files must not inject ProjectReference edges outside the explicit production csproj allow-list."
            + $"{Environment.NewLine}Declarations: [{string.Join(", ", declarations)}]");
    }

    [Test]
    public void ProductionProjects_DoNotUseExplicitCustomImports()
    {
        AssertEx.True(ApprovedProjectReferences.Count >= ApprovedProjectReferencesFloor,
            $"Scanned {ApprovedProjectReferences.Count} production csproj files named by ApprovedProjectReferences, "
            + $"below the non-vacuity floor of {ApprovedProjectReferencesFloor}. An emptied allow-list would report "
            + "no custom Imports for the wrong reason.");

        var imports = ApprovedProjectReferences.Keys
                                               .Select(projectName => RepositoryPaths.Combine(projectName, $"{projectName}.csproj"))
                                               .SelectMany(path => XDocument.Load(path)
                                                                            .Descendants()
                                                                            .Where(element => element.Name.LocalName == "Import")
                                                                            .Select(import =>
                                                                                $"{Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/')}: "
                                                                                + $"Import Project=\"{(string?)import.Attribute("Project") ?? "<missing>"}\""))
                                               .Order(StringComparer.Ordinal)
                                               .ToArray();

        AssertEx.Empty(imports,
            "Production csproj files must not use explicit custom Import paths that can inject unreviewed references. "
            + "Implicit SDK and Directory.Build imports remain allowed and are covered by the shared build-customization scan."
            + $"{Environment.NewLine}Imports: [{string.Join(", ", imports)}]");
    }

    [Test]
    public void OllamaProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(OllamaAssembly, OllamaNamespace, 8);

        AssertNoDependency(OllamaAssembly,
            OllamaNamespace,
            ClientNamespace,
            PersistenceNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void LlamaServerProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(LlamaServerAssembly, LlamaServerNamespace, 220);

        AssertNoDependency(LlamaServerAssembly,
            LlamaServerNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            WhisperCppNamespace);
    }

    [Test]
    public void HuggingFaceProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(HuggingFaceAssembly, HuggingFaceNamespace, 70);

        AssertNoDependency(HuggingFaceAssembly,
            HuggingFaceNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            WhisperCppNamespace);
    }

    [Test]
    public void CodexOAuthProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(CodexOAuthAssembly, CodexOAuthNamespace, 20);

        AssertNoDependency(CodexOAuthAssembly,
            CodexOAuthNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void StableDiffusionCppProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(StableDiffusionCppAssembly, StableDiffusionCppNamespace, 100);

        AssertNoDependency(StableDiffusionCppAssembly,
            StableDiffusionCppNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            WhisperCppNamespace);
    }

    [Test]
    public void WhisperCppProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(WhisperCppAssembly, WhisperCppNamespace, 90);

        AssertNoDependency(WhisperCppAssembly,
            WhisperCppNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            StableDiffusionCppNamespace);
    }

    [Test]
    public void CapabilitiesProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(CapabilitiesAssembly, CapabilitiesNamespace, 8);

        AssertNoDependency(CapabilitiesAssembly,
            CapabilitiesNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace);
    }

    [Test]
    public void AiContracts_DoesNotDependOnApplicationProvidersPersistenceOrHost()
    {
        AssertTypesScanned(ContractsAssembly, "XE_Local_AI_Engine.AI.Contracts", 6);

        AssertNoDependency(ContractsAssembly,
            "XE_Local_AI_Engine.AI.Contracts",
            ClientNamespace,
            PersistenceNamespace,
            ApplicationNamespace,
            "XE_Local_AI_Engine.Providers");
    }

    [Test]
    public void ExternalOpenAiProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertTypesScanned(OpenAICompatAssembly, OpenAICompatNamespace, 10);

        // The shared transport core is deliberately absent from the forbidden list — it is this provider's approved
        // downward dependency, not a sibling. See the OpenAICompatNamespace prefix caution above.
        AssertNoDependency(OpenAICompatAssembly,
            OpenAICompatNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            StableDiffusionCppNamespace);
    }

    [Test]
    public void ProvidersAbstractions_DoesNotDependOnConcreteProvidersApplicationPersistenceOrHost()
    {
        AssertTypesScanned(AbstractionsAssembly, AbstractionsNamespace, 130);

        // OpenAICompatNamespace covers BOTH the external provider and the shared transport core by namespace prefix,
        // which is exactly right here: the seam layer must be a leaf and depend on neither.
        AssertNoDependency(AbstractionsAssembly,
            AbstractionsNamespace,
            ClientNamespace,
            PersistenceNamespace,
            ApplicationNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            OpenAICompatNamespace,
            WhisperCppNamespace);
    }

    [Test]
    public void AiAgent_DoesNotDependOnApplicationPersistenceHostOrConcreteProviders()
    {
        AssertTypesScanned(AiAgentAssembly, AiAgentNamespace, 100);

        AssertNoDependency(AiAgentAssembly,
            AiAgentNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            StableDiffusionCppNamespace,
            WhisperCppNamespace);
    }

    /// <summary>
    /// Non-vacuity floor for the dependency rules: proves the scan still sees a real, populated assembly. A marker
    /// type that starts resolving to a different or near-empty assembly would otherwise let
    /// <see cref="AssertNoDependency"/> pass over an empty type set and report green for the wrong reason. Floors are
    /// set below the counts measured on 2026-09-16 so a planned addition or deletion does not make them brittle.
    /// </summary>
    private static void AssertTypesScanned(Assembly assembly, string sourceNamespace, int floor)
    {
        var scanned = Types
                      .InAssembly(assembly)
                      .That()
                      .ResideInNamespaceStartingWith(sourceNamespace)
                      .GetTypes()
                      .Count();

        AssertEx.True(scanned >= floor,
            $"Scanned {scanned} types in namespace '{sourceNamespace}' of assembly '{assembly.GetName().Name}', "
            + $"below the non-vacuity floor of {floor}. The dependency rule that follows would assert over an empty "
            + "type set.");
    }

    /// <summary>
    /// Asserts that no type residing in <paramref name="sourceNamespace"/> within
    /// <paramref name="assembly"/> has a dependency on any of the
    /// <paramref name="forbiddenNamespaces"/>. Reports the offending type names on failure.
    /// </summary>
    private static void AssertNoDependency(Assembly assembly,
        string sourceNamespace,
        params string[] forbiddenNamespaces)
    {
        var result = Types
                     .InAssembly(assembly)
                     .That()
                     .ResideInNamespaceStartingWith(sourceNamespace)
                     .ShouldNot()
                     .HaveDependencyOnAny(forbiddenNamespaces)
                     .GetResult();

        var failing = result.FailingTypeNames is null
            ? string.Empty
            : string.Join(", ", result.FailingTypeNames);

        AssertEx.True(result.IsSuccessful,
            $"Types in '{sourceNamespace}' must not depend on [{string.Join(", ", forbiddenNamespaces)}]. "
            + $"Violating types: {failing}");
    }

    private static void AssertExactReferences(string source, IEnumerable<string> approvedReferences, IEnumerable<string> actualReferences)
    {
        var approved = approvedReferences.Order(StringComparer.Ordinal).ToArray();
        var actual = actualReferences.Order(StringComparer.Ordinal).ToArray();

        AssertEx.Equal(string.Join(Environment.NewLine, approved),
            string.Join(Environment.NewLine, actual),
            $"'{source}' internal references changed. Update the architecture intentionally and review the allow-list."
            + $"{Environment.NewLine}Approved: [{string.Join(", ", approved)}]"
            + $"{Environment.NewLine}Actual: [{string.Join(", ", actual)}]");
    }

    private static void AssertOnlyApprovedReferences(string source, IEnumerable<string> approvedReferences, IEnumerable<string> actualReferences)
    {
        var approved = approvedReferences.ToHashSet(StringComparer.Ordinal);
        var actual = actualReferences.Order(StringComparer.Ordinal).ToArray();
        var forbidden = actual.Where(reference => !approved.Contains(reference)).ToArray();

        AssertEx.Empty(forbidden,
            $"'{source}' acquired forbidden internal assembly references: [{string.Join(", ", forbidden)}]."
            + $"{Environment.NewLine}Approved: [{string.Join(", ", approved.Order(StringComparer.Ordinal))}]"
            + $"{Environment.NewLine}Actual: [{string.Join(", ", actual)}]");
    }

    private static IEnumerable<string> EnumerateRepositoryBuildCustomizationFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (IsExcludedBuildCustomizationDirectory(name)
                || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            foreach (var path in EnumerateRepositoryBuildCustomizationFiles(child))
            {
                yield return path;
            }
        }
    }

    // Build output and every dot-directory. The dot rule replaces a list of specific tool directories, which only
    // excluded whatever tooling the author of the list happened to run: a contributor with a different one had that
    // tool's state walked instead. No repository-controlled .props or .targets lives under a dot-directory, so this
    // widening does not shrink what the test asserts over.
    private static bool IsExcludedBuildCustomizationDirectory(string name) =>
        name is "bin" or "obj" or "node_modules" || name.StartsWith('.');
}
