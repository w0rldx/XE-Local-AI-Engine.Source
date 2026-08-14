namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProviderMapCoordinationArchitectureTests
{
    [Test]
    public void ProductionComposition_RegistersSharedCoordinationDomainAndFacades()
    {
        var source = File.ReadAllText(RepositoryPaths.Combine(
            "XE-Local-AI-Engine.Client.Application",
            "DependencyInjection",
            "Modules",
            "AddNodeWorkspaceAndAgentsExtensions.cs"));
        foreach (var registration in new[]
                 {
                     "AddSingleton<KeyedCompositeLockDomain>()",
                     "AddSingleton<IModelProviderMapLeaseCoordinator, ModelProviderMapLeaseCoordinator>()",
                     "AddSingleton<IInstalledModelSnapshotCoordinator>",
                     "AddScoped<ICoordinatedModelProviderMapStore>",
                     "AddScoped<IGgufAcquisitionPreflight, GgufAcquisitionPreflight>()",
                     "AddScoped<IOllamaProviderMapBackfillCoordinator, OllamaProviderMapBackfillCoordinator>()"
                 })
        {
            AssertEx.Contains(source, registration);
        }
    }

    [Test]
    public void ProductionCallers_CannotBypassCoordinatedProviderMapFacade()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeWorkspaceAndAgentsExtensions.cs",
            "XE-Local-AI-Engine.Client.Application/Services/Models/CoordinatedModelProviderMapStore.cs"
        };
        var violations = new List<string>();
        foreach (var project in new[] { "XE-Local-AI-Engine.Client.Application", "XE-Local-AI-Engine.Client" })
        {
            var projectRoot = RepositoryPaths.Combine(project);
            foreach (var path in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                                          .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var relative = Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');
                if (allowed.Contains(relative))
                {
                    continue;
                }

                var source = File.ReadAllText(path);
                if (source.Contains("IModelProviderMapStore", StringComparison.Ordinal)
                    || source.Contains("ModelProviderMapStore", StringComparison.Ordinal))
                {
                    violations.Add(relative);
                }
            }
        }

        AssertEx.Empty(violations,
            $"Provider-map persistence must be accessed only through ICoordinatedModelProviderMapStore: {string.Join(", ", violations)}");
    }
}
