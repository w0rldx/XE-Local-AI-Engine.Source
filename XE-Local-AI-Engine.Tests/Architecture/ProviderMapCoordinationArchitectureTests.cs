namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ProviderMapCoordinationArchitectureTests
{
    // Non-vacuity floor for the source scan below, set under today's measured 2045 .cs files across the two
    // production projects so ordinary file churn does not make it brittle.
    private const int ScannedProductionFileFloor = 1500;

    [Test]
    public void ProductionComposition_RegistersSharedCoordinationDomainAndFacades()
    {
        var source = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Client.Application",
            "DependencyInjection",
            "Modules",
            "AddNodeWorkspaceAndAgentsExtensions.cs"));
        foreach (var registration in new[]
                 {
                     "AddSingleton<KeyedCompositeLockDomain>()",
                     "AddSingleton<IModelProviderMapLeaseCoordinator, ModelProviderMapLeaseCoordinator>()",
                     "AddScoped<IInstalledModelSnapshotCoordinator>",
                     "GetRequiredService<IInstalledGgufSnapshotStore>()",
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
        AssertEx.True(ContainsIdentifier("IModelProviderMapStore store", "IModelProviderMapStore"));
        AssertEx.True(ContainsIdentifier("new ModelProviderMapStore()", "ModelProviderMapStore"));
        AssertEx.False(ContainsIdentifier("ICoordinatedModelProviderMapStore store", "IModelProviderMapStore"));
        AssertEx.False(ContainsIdentifier("ICoordinatedModelProviderMapStore store", "ModelProviderMapStore"));

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeWorkspaceAndAgentsExtensions.cs",
            "XE-Local-AI-Engine.Client.Application/Services/Models/CoordinatedModelProviderMapStore.cs"
        };
        var violations = new List<string>();
        var scannedFiles = 0;
        foreach (var project in new[]
                 {
                     "XE-Local-AI-Engine.Client.Application",
                     "XE-Local-AI-Engine.Client"
                 })
        {
            var projectRoot = RepositoryPaths.Combine(project);
            foreach (var path in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                                          .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                scannedFiles++;
                var relative = Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');
                if (allowed.Contains(relative))
                {
                    continue;
                }

                var source = File.ReadAllText(path);
                if (ContainsIdentifier(source, "IModelProviderMapStore")
                    || ContainsIdentifier(source, "ModelProviderMapStore"))
                {
                    violations.Add(relative);
                }
            }
        }

        AssertEx.True(scannedFiles >= ScannedProductionFileFloor,
            $"Scanned {scannedFiles} .cs files across XE-Local-AI-Engine.Client.Application and XE-Local-AI-Engine.Client, "
            + $"below the non-vacuity floor of {ScannedProductionFileFloor}. A scan that walked nothing would report no "
            + "facade bypass for the wrong reason.");

        AssertEx.Empty(violations,
            $"Provider-map persistence must be accessed only through ICoordinatedModelProviderMapStore: {string.Join(", ", violations)}");
    }

    private static bool ContainsIdentifier(string source, string identifier)
    {
        var startIndex = 0;
        while ((startIndex = source.IndexOf(identifier, startIndex, StringComparison.Ordinal)) >= 0)
        {
            var endIndex = startIndex + identifier.Length;
            var hasIdentifierStart = startIndex == 0 || !IsIdentifierCharacter(source[startIndex - 1]);
            var hasIdentifierEnd = endIndex == source.Length || !IsIdentifierCharacter(source[endIndex]);
            if (hasIdentifierStart && hasIdentifierEnd)
            {
                return true;
            }

            startIndex = endIndex;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value) =>
        value == '_' || char.IsLetterOrDigit(value);
}
