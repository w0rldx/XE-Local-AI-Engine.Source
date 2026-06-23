namespace XE_Local_AI_Engine.Tests.Coder;

using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Confinement coverage for <see cref="WorkspacePathGuard" />: every model path is either confined to a
///     workspace-relative subpath of <c>/agent-home/workspace/selected</c> or rejected fail-closed. Traversal,
///     absolute, drive-qualified, extended/device, and control-char paths are rejected; legitimate relative paths
///     resolve under the root.
/// </summary>
public sealed class WorkspacePathGuardTests
{
    [Test]
    public void Confine_WhenNullOrEmpty_ConfinesToWorkspaceRoot()
    {
        var confined = WorkspacePathGuard.Confine(null);

        AssertEx.True(confined.IsConfined);
        AssertEx.Equal(string.Empty, confined.RelativePath);
        AssertEx.Equal(WorkspacePathGuard.WorkspaceRoot, confined.SandboxPath);
    }

    [Test]
    public void Confine_WhenRelativePath_ResolvesUnderRoot()
    {
        var confined = WorkspacePathGuard.Confine("src/app/Program.cs");

        AssertEx.True(confined.IsConfined);
        AssertEx.Equal("src/app/Program.cs", confined.RelativePath);
        AssertEx.Equal(WorkspacePathGuard.WorkspaceRoot + "/src/app/Program.cs", confined.SandboxPath);
    }

    [Test]
    public void Confine_WhenInnerDotSegments_CollapseWithoutEscaping()
    {
        var confined = WorkspacePathGuard.Confine("src/./a/../b/c");

        AssertEx.True(confined.IsConfined);
        AssertEx.Equal("src/b/c", confined.RelativePath);
    }

    [Test]
    public void Confine_WhenTraversalAboveRoot_Rejects()
    {
        var confined = WorkspacePathGuard.Confine("../../etc/passwd");

        AssertEx.False(confined.IsConfined);
        AssertEx.NotNullOrEmpty(confined.RejectionReason!);
    }

    [Test]
    public void Confine_WhenTraversalNetsAboveRoot_Rejects()
    {
        // a/../.. nets one level above the root.
        var confined = WorkspacePathGuard.Confine("a/../../x");

        AssertEx.False(confined.IsConfined);
    }

    [Test]
    public void Confine_WhenAbsolutePath_Rejects()
    {
        var confined = WorkspacePathGuard.Confine("/etc/passwd");

        AssertEx.False(confined.IsConfined);
    }

    [Test]
    public void Confine_WhenBackslashTraversal_Rejects()
    {
        var confined = WorkspacePathGuard.Confine(@"..\..\windows\system32");

        AssertEx.False(confined.IsConfined);
    }

    [Test]
    public void Confine_WhenWindowsDriveQualified_Rejects()
    {
        var confined = WorkspacePathGuard.Confine("C:/Windows/System32");

        AssertEx.False(confined.IsConfined);
    }

    [Test]
    public void Confine_WhenExtendedDevicePath_Rejects()
    {
        var confined = WorkspacePathGuard.Confine(@"\\?\C:\secret");

        AssertEx.False(confined.IsConfined);
    }

    [Test]
    public void Confine_WhenControlCharacters_Rejects()
    {
        var confined = WorkspacePathGuard.Confine("src/a\u0001b");

        AssertEx.False(confined.IsConfined);
    }
}
