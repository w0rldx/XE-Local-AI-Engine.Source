namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins that a concrete persistence store registered beside a decorator is named only where it is registered.
/// </summary>
/// <remarks>
///     <c>DevWorkflowStore</c> and <c>GraphWorkflowStore</c> are resolved through publishing decorators, so a caller
///     that constructs or resolves the concrete type commits a change without announcing it — and both went public
///     when the friend-assembly grant to the application layer was revoked, which put them in reach of the host for
///     the first time. Visibility is not the boundary here; this is, exactly as
///     <see cref="ProviderMapCoordinationArchitectureTests" /> is for the provider map.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ConcreteStoreConstructionArchitectureTests
{
    // Non-vacuity floor for the source scan, set under the count measured across the two production projects on
    // 2026-09-21 so ordinary file churn is not brittle. It only has to catch a scan that walked nothing.
    private const int ScannedProductionFileFloor = 1500;

    /// <summary>The concrete store types this rule fences, and the one file each may be named in.</summary>
    /// <remarks>
    ///     <c>AgentWorkSessionStore</c> has no decorator today, so it is listed pre-emptively rather than to close a
    ///     live bypass: it is registered straight onto its interface, and the moment anyone wraps it the same hazard
    ///     applies. Keeping all three under one rule means the invariant is simply "the concrete store is named where
    ///     it is registered, and nowhere else".
    /// </remarks>
    private static readonly (string TypeName, string RegistrationFile)[] FencedStores =
    [
        ("DevWorkflowStore", "XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeDevWorkflowsExtensions.cs"),
        ("GraphWorkflowStore", "XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeGraphWorkflowsExtensions.cs"),
        ("AgentWorkSessionStore", "XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeWorkSessionsExtensions.cs")
    ];

    [Test]
    public void ProductionCallers_NameAConcreteStoreOnlyWhereItIsRegistered()
    {
        // The matcher is bare-identifier, so PublishingDevWorkflowStore and DevWorkflowStoreExceptions do not match
        // DevWorkflowStore. Proved by the two assertions below rather than assumed.
        AssertEx.True(ContainsIdentifier("GetRequiredService<DevWorkflowStore>()", "DevWorkflowStore"));
        AssertEx.False(ContainsIdentifier("new PublishingDevWorkflowStore(inner)", "DevWorkflowStore"));

        var allowed = FencedStores.Select(static store => store.RegistrationFile).ToHashSet(StringComparer.Ordinal);
        foreach (var registrationFile in allowed)
        {
            AssertEx.True(File.Exists(RepositoryPaths.Combine(registrationFile.Split('/'))),
                $"The allowed registration file '{registrationFile}' does not exist, so this rule would fence a name "
                + "that nothing may legitimately use and pass for the wrong reason.");
        }

        var violations = new List<string>();
        var scannedFiles = 0;
        foreach (var project in new[]
                 {
                     "XE-Local-AI-Engine.Client.Application",
                     "XE-Local-AI-Engine.Client"
                 })
        {
            foreach (var path in Directory.EnumerateFiles(RepositoryPaths.Combine(project), "*.cs", SearchOption.AllDirectories)
                                          .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                scannedFiles++;
                var relative = Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');
                var source = File.ReadAllText(path);
                violations.AddRange(FencedStores
                                    .Where(store => !string.Equals(relative, store.RegistrationFile, StringComparison.Ordinal)
                                                    && ContainsIdentifier(source, store.TypeName))
                                    .Select(store => $"{relative} names {store.TypeName}"));
            }
        }

        AssertEx.True(scannedFiles >= ScannedProductionFileFloor,
            $"Scanned {scannedFiles} .cs files across XE-Local-AI-Engine.Client.Application and XE-Local-AI-Engine.Client, "
            + $"below the non-vacuity floor of {ScannedProductionFileFloor}. A scan that walked nothing would report no "
            + "bypass for the wrong reason.");

        AssertEx.Empty(violations,
            "A concrete persistence store that is registered beside a decorator may be named only in the module that "
            + "registers it: resolving or constructing it anywhere else takes the undecorated store and skips the "
            + "event publishing every other caller gets. Inject the interface instead. Violations:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>True when <paramref name="identifier" /> appears as a whole identifier, not as part of a longer one.</summary>
    private static bool ContainsIdentifier(string source, string identifier)
    {
        var startIndex = 0;
        while ((startIndex = source.IndexOf(identifier, startIndex, StringComparison.Ordinal)) >= 0)
        {
            var endIndex = startIndex + identifier.Length;
            if ((startIndex == 0 || !IsIdentifierCharacter(source[startIndex - 1]))
                && (endIndex == source.Length || !IsIdentifierCharacter(source[endIndex])))
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
