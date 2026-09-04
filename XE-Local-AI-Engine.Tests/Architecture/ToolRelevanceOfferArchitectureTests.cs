namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The structural property the whole tool-relevance offer rests on: <c>list_tools</c> is appended by exactly ONE
///     production site, the single agent's tool assembly. The send-time hop refuses to filter any array that does not
///     carry a <c>list_tools</c> instance, so that one append is what decides which arrays can be trimmed at all — an
///     orchestration participant's array and a spawned sub-agent's array are inert precisely because they lack it. A
///     second construction site anywhere in product code would silently make those arrays filterable, hiding tools from
///     a turn that has no escape hatch to get them back. This was previously pinned by grep alone.
/// </summary>
public sealed class ToolRelevanceOfferArchitectureTests
{
    private const string ConstructionSite = "new ListToolsFunction(";

    private const string ExpectedOwner = "XE-Local-AI-Engine.AI.Agent/Invocation/Implementation/InvocationAgentFactory.cs";

    [Test]
    public void ExactlyOneProductionSite_ConstructsAListToolsFunction()
    {
        var sites = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(RepositoryPaths.Root, "XE-Local-AI-Engine.*")
                                         .Where(static directory => !Path.GetFileName(directory).Contains(".Tests", StringComparison.Ordinal))
                                         .Where(static directory => Directory.EnumerateFiles(directory, "*.csproj").Any()))
        {
            foreach (var path in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                                          .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                if (File.ReadAllText(path).Contains(ConstructionSite, StringComparison.Ordinal))
                {
                    sites.Add(Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/'));
                }
            }
        }

        AssertEx.True(sites.Count == 1 && sites[0] == ExpectedOwner,
            $"'{ConstructionSite}' must appear in exactly one production file ({ExpectedOwner}), but was found in: {string.Join(", ", sites.Order(StringComparer.Ordinal))}.");
    }
}
