namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The sandbox-path mapping, which is the same mapping <c>ProcessSandboxRuntimeProvider.ResolveJailPath</c>
///     applies and must stay that way.
///     <para>
///         Callers address files identically whichever provider is in force — Development Mode's
///         <c>DevelopmentWorkspaceSecurity.Confine</c> hands out <c>"/"</c> for the workspace root and
///         <c>"/relative/path"</c> for a file — so the sandbox root is the workspace, not the container's <c>/</c>.
///         The first case below is the one that was actually broken: an unmapped <c>"/"</c> reaches the wire as the
///         container root, and every command runs somewhere with no repository in it.
///     </para>
/// </summary>
public sealed class DockerSandboxPathsTests
{
    [Test]
    public void ResolveContainerPath_TheSandboxRootIsTheWorkspaceMountRatherThanTheContainerRoot()
    {
        AssertEx.Equal("/workspace", DockerSandboxPaths.ResolveContainerPath("/workspace", "/"));
    }

    [Test]
    public void ResolveContainerPath_ASandboxAbsolutePathLandsUnderTheMount()
    {
        AssertEx.Equal("/workspace/src/app.cs", DockerSandboxPaths.ResolveContainerPath("/workspace", "/src/app.cs"));
    }

    [Test]
    public void ResolveContainerPath_ARelativePathIsTreatedTheSameWay()
    {
        AssertEx.Equal("/workspace/src/app.cs", DockerSandboxPaths.ResolveContainerPath("/workspace", "src/app.cs"));
    }

    [Test]
    public void ResolveContainerPath_CollapsesTraversalThatStaysInside()
    {
        AssertEx.Equal("/workspace/b.cs", DockerSandboxPaths.ResolveContainerPath("/workspace", "/src/../b.cs"));
    }

    [Test]
    public async Task ResolveContainerPath_WhenTraversalEscapesTheMount_ThrowsTheSameWayTheProcessProviderDoes()
    {
        // UnauthorizedAccessException specifically: the process provider throws that for a jail escape, and a caller
        // that catches one provider's escape signal must catch the other's.
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => Task.FromResult(DockerSandboxPaths.ResolveContainerPath("/workspace", "/../etc/shadow")));
    }

    [Test]
    public async Task ResolveContainerPath_ANestedEscapeIsRejectedToo()
    {
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => Task.FromResult(DockerSandboxPaths.ResolveContainerPath("/workspace", "/src/../../../root/.ssh/id_rsa")));
    }

    [Test]
    public void ResolveContainerPath_ASiblingOfTheMountWhoseNameSharesItsPrefixIsNotInsideIt()
    {
        // "/workspace-other" starts with "/workspace" as a STRING but is a different directory. A prefix check without
        // the separator would accept it.
        AssertEx.Equal("/workspace/workspace-other/x", DockerSandboxPaths.ResolveContainerPath("/workspace", "/workspace-other/x"));
    }

    [Test]
    public void ResolveHostPath_MapsOntoTheMountSourceRatherThanTheContainerPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-paths", Guid.NewGuid().ToString("N"));

        AssertEx.Equal(Path.Combine(root, "src", "app.cs"), DockerSandboxPaths.ResolveHostPath(root, "/workspace", "/src/app.cs"));
    }

    [Test]
    public void ResolveHostPath_TheSandboxRootMapsToTheWorkspaceRootItself()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-paths", Guid.NewGuid().ToString("N"));

        AssertEx.Equal(root, DockerSandboxPaths.ResolveHostPath(root, "/workspace", "/"));
    }

    [Test]
    public async Task ResolveHostPath_WhenTheSandboxPathEscapes_Rejects()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-paths", Guid.NewGuid().ToString("N"));

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => Task.FromResult(DockerSandboxPaths.ResolveHostPath(root, "/workspace", "/../../etc/passwd")));
    }
}
