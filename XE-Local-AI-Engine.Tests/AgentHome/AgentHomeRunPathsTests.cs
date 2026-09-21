namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one place either agent-home root is derived. The manifest service, the patch-apply service, the run list
///     and the retention sweep all read these two methods, so the shapes they return are a contract between them.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AgentHomeRunPathsTests
{
    [Test]
    public void ResolveAgentHomeRoot_WithNoConfiguredRoot_LandsUnderTheNodeDataDirectory()
    {
        var root = AgentHomeRunPaths.ResolveAgentHomeRoot(new AgentHomeOptions(), Path.Combine("data", "node"));

        AssertEx.Equal(Path.Combine("data", "node", "agent-home-state", "agent-home"), root);
    }

    [Test]
    public void ResolveAgentHomeRoot_WithConfiguredRoot_IgnoresTheDataDirectory()
    {
        var options = new AgentHomeOptions { RootPath = Path.Combine("elsewhere", "state") };

        var root = AgentHomeRunPaths.ResolveAgentHomeRoot(options, Path.Combine("data", "node"));

        AssertEx.Equal(Path.Combine("elsewhere", "state", "agent-home"), root);
    }

    /// <summary>
    ///     <c>AgentHomeManifestService.WipeAgentHome</c> refuses to recursively delete a path that does not end in
    ///     the layout directory name, so the root must arrive un-normalized and without a trailing separator.
    /// </summary>
    [Test]
    public void ResolveAgentHomeRoot_EndsWithTheLayoutDirectoryNameTheWipeGuardChecks()
    {
        var root = AgentHomeRunPaths.ResolveAgentHomeRoot(new AgentHomeOptions(), Path.Combine("data", "node"));

        AssertEx.True(root.EndsWith(AgentHomeRunPaths.AgentHomeDirectoryName, StringComparison.Ordinal),
            "the wipe guard compares the root against this exact suffix");
    }

    [Test]
    public void ResolveRunsRoot_IsTheRunsDirectoryInsideTheAgentHomeRoot()
    {
        var options = new AgentHomeOptions { RootPath = Path.Combine("elsewhere", "state") };

        var runsRoot = AgentHomeRunPaths.ResolveRunsRoot(options, Path.Combine("data", "node"));

        AssertEx.Equal(Path.Combine(AgentHomeRunPaths.ResolveAgentHomeRoot(options, Path.Combine("data", "node")),
                AgentHomeRunPaths.RunsDirectoryName),
            runsRoot);
    }
}
