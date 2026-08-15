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
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
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
    private const string ClientNamespace = "XE_Local_AI_Engine.Client";
    private const string PersistenceNamespace = "XE_Local_AI_Engine.Client.Persistence";
    private const string ApplicationNamespace = "XE_Local_AI_Engine.Client.Application";

    private const string OllamaNamespace = "XE_Local_AI_Engine.Providers.Ollama";
    private const string LlamaServerNamespace = "XE_Local_AI_Engine.Providers.LlamaServer";
    private const string HuggingFaceNamespace = "XE_Local_AI_Engine.Providers.HuggingFace";
    private const string CodexOAuthNamespace = "XE_Local_AI_Engine.Providers.CodexOAuth";
    private const string CapabilitiesNamespace = "XE_Local_AI_Engine.Providers.Capabilities";
    private const string StableDiffusionCppNamespace = "XE_Local_AI_Engine.Providers.StableDiffusionCpp";
    private const string AbstractionsNamespace = "XE_Local_AI_Engine.Providers.Abstractions";
    private const string AiAgentNamespace = "XE_Local_AI_Engine.AI.Agent";

    // Marker types anchor each assembly so we test the real compiled IL, not a
    // namespace string. Every marker is a verified public type in its assembly.
    private static readonly Assembly OllamaAssembly = typeof(OllamaLocalModelProvider).Assembly;
    private static readonly Assembly LlamaServerAssembly = typeof(IInstalledRuntimeStore).Assembly;
    private static readonly Assembly HuggingFaceAssembly = typeof(HuggingFaceOptions).Assembly;
    private static readonly Assembly CodexOAuthAssembly = typeof(ICodexOAuthChatClientFactory).Assembly;
    private static readonly Assembly CapabilitiesAssembly = typeof(CapabilitiesServiceCollectionExtensions).Assembly;
    private static readonly Assembly StableDiffusionCppAssembly = typeof(IStableDiffusionBinaryManager).Assembly;
    private static readonly Assembly TrainingAssembly = typeof(ITrainingRuntimeService).Assembly;
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
                "XE-Local-AI-Engine.Providers.StableDiffusionCpp",
                "XE-Local-AI-Engine.Providers.Training",
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
            ["XE-Local-AI-Engine.Providers.LlamaServer"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.Ollama"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.StableDiffusionCpp"] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            ["XE-Local-AI-Engine.Providers.Training"] = ["XE-Local-AI-Engine.Providers.Abstractions"]
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
            [LlamaServerAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [OllamaAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [StableDiffusionCppAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
            [TrainingAssembly] = ["XE-Local-AI-Engine.Providers.Abstractions"],
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
                "XE-Local-AI-Engine.Providers.StableDiffusionCpp",
                "XE-Local-AI-Engine.Providers.Training",
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
        var declarations = EnumerateRepositoryBuildCustomizationFiles(RepositoryPaths.Root)
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
        AssertNoDependency(LlamaServerAssembly,
            LlamaServerNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void HuggingFaceProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertNoDependency(HuggingFaceAssembly,
            HuggingFaceNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void CodexOAuthProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
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
        AssertNoDependency(StableDiffusionCppAssembly,
            StableDiffusionCppNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void CapabilitiesProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
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
        AssertNoDependency(ContractsAssembly,
            "XE_Local_AI_Engine.AI.Contracts",
            ClientNamespace,
            PersistenceNamespace,
            ApplicationNamespace,
            "XE_Local_AI_Engine.Providers");
    }

    [Test]
    public void ProvidersAbstractions_DoesNotDependOnConcreteProvidersApplicationPersistenceOrHost()
    {
        AssertNoDependency(AbstractionsAssembly,
            AbstractionsNamespace,
            ClientNamespace,
            PersistenceNamespace,
            ApplicationNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void AiAgent_DoesNotDependOnApplicationPersistenceHostOrConcreteProviders()
    {
        AssertNoDependency(AiAgentAssembly,
            AiAgentNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace,
            StableDiffusionCppNamespace);
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

    private static bool IsExcludedBuildCustomizationDirectory(string name) =>
        name is "bin" or "obj" or ".tmp" or ".git" or ".codegraph" or ".omx" or "node_modules";
}
