namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The property the tool-relevance offer rests on: exactly ONE production site, the single agent's tool assembly,
///     appends <c>list_tools</c>, and the send-time hop filters only arrays that carry a <c>list_tools</c> instance.
/// </summary>
/// <remarks>
///     Participant and sub-agent arrays are inert because they lack it; a second construction site would silently make
///     them filterable, hiding tools from a turn with no escape hatch. This pins it by type, not by grep alone.
/// </remarks>
[Category(TestCategories.Unit)]
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
