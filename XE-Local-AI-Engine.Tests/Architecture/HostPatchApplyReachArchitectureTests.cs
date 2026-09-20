namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Only the operator may reach the host patch apply: <c>INodePatchApplyService</c> writes to the operator's own
///     folders, so never a model tool, never an <c>IClientLocalToolHandler</c>, never a <c>[McpServerTool]</c>.
/// </summary>
/// <remarks>
///     Enforced as an allow-list of the production files that may NAME the interface, which is stronger than a
///     heuristic over attributes and base types: anything model-influenced — a tool handler, an MCP partial, a
///     workflow node, a future registry — would have to appear in the list. A new legitimate caller fails here and
///     is added deliberately, which is the moment to ask whether it is the operator or something acting for the
///     model.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class HostPatchApplyReachArchitectureTests
{
    private const string ServiceName = "INodePatchApplyService";

    /// <summary>
    ///     Every production file allowed to name the interface: its own declaration and models, the implementation,
    ///     the DI registration, and the operator endpoint pair with its mapper. Not one of these is model-facing.
    /// </summary>
    private static readonly string[] AllowedOwners =
    [
        "XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules/AddNodeAgentHomeExtensions.cs",
        "XE-Local-AI-Engine.Client.Application/Services/AgentHome/INodePatchApplyService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/AgentHome/Implementation/NodePatchApplyService.cs",
        "XE-Local-AI-Engine.Client.Application/Services/AgentHome/NodePatchApplyModels.cs",
        "XE-Local-AI-Engine.Client/Endpoints/AgentHome/V1/ApplyAgentHomePatchEndpoint.cs",
        "XE-Local-AI-Engine.Client/Endpoints/AgentHome/V1/Mappers/AgentHomePatchContractMapper.cs",
        "XE-Local-AI-Engine.Client/Endpoints/AgentHome/V1/PreviewAgentHomePatchEndpoint.cs",
        "XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs"
    ];

    [Test]
    public void OnlyTheOperatorSurface_NamesTheHostPatchApplyService()
    {
        var referencing = ProductionFilesNaming(ServiceName);

        AssertEx.NotEmpty(referencing,
            $"No production file names {ServiceName}. Either it was renamed — in which case this test is now vacuous "
            + "and must be updated — or the scan below stopped finding source files.");

        var unexpected = referencing.Except(AllowedOwners, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        AssertEx.Empty(unexpected,
            $"{string.Join(", ", unexpected)} reference{(unexpected.Count == 1 ? "s" : "")} {ServiceName}. Landing a "
            + "patch on the host is the OPERATOR's decision: it must not become reachable from a tool handler, an MCP "
            + "tool, or anything a model can cause to run. If this is a new operator-only caller, add it to "
            + "AllowedOwners and say so in the review.");

        var absent = AllowedOwners.Except(referencing, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        AssertEx.Empty(absent,
            $"This list expects file(s) that no longer reference {ServiceName}: {string.Join(", ", absent)}. Remove the "
            + "stale entry rather than leaving an unused exemption behind.");
    }

    private static List<string> ProductionFilesNaming(string token)
    {
        var matches = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(RepositoryPaths.Root, "XE-Local-AI-Engine.*")
                                         .Where(static directory => !Path.GetFileName(directory).Contains(".Tests", StringComparison.Ordinal))
                                         .Where(static directory => Directory.EnumerateFiles(directory, "*.csproj").Any()))
        {
            foreach (var path in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                                          .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                if (File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                {
                    matches.Add(Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/'));
                }
            }
        }

        return matches;
    }
}
