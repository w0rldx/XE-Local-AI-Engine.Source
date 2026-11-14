namespace XE_Local_AI_Engine.Tests.Coder;

using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="WorkspacePathGuard" /> confines every model path under <c>/agent-home/workspace/selected</c> or
///     rejects it fail-closed: traversal, absolute, drive-qualified, extended/device and control-char paths are
///     rejected; legitimate relative paths resolve under the root.
/// </summary>
[Category(TestCategories.Unit)]
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

    // Windows trims a trailing dot or space and reads ':' as a stream separator, so each of these would open an excluded name
    // (or, for ".. ", the parent) while the name-based exclusion saw something else. Rejected on every OS.
    [Test]
    [Arguments(".git.")]
    [Arguments(".git./config")]
    [Arguments("sub/.git./config")]
    [Arguments(".env ")]
    [Arguments(".. ")]
    [Arguments("src/a:b")]
    [Arguments(".env::$DATA")]
    public void Confine_WhenSegmentWindowsWouldRewrite_Rejects(string path)
    {
        var confined = WorkspacePathGuard.Confine(path);

        AssertEx.False(confined.IsConfined);
        AssertEx.Contains(confined.RejectionReason!, "ends with a dot or a space");
    }

    [Test]
    [Arguments("GIT~1/config")]
    [Arguments("AWS~1/credentials")]
    [Arguments("src/PROGRA~1")]
    public void Confine_WhenSegmentIsAShortNameAlias_Rejects(string path)
    {
        var confined = WorkspacePathGuard.Confine(path);

        AssertEx.False(confined.IsConfined);
        AssertEx.Contains(confined.RejectionReason!, "short-name alias");
    }

    [Test]
    [Arguments("file.txt", "file.txt")]
    [Arguments("a.b/c", "a.b/c")]
    [Arguments(".env.local/../src/.editorconfig", "src/.editorconfig")]
    [Arguments("a~b", "a~b")]
    [Arguments("file~", "file~")]
    [Arguments("~/notes", "~/notes")]
    public void Confine_WhenDotOrTildeIsOrdinaryPartOfAName_Confines(string path, string expectedRelative)
    {
        var confined = WorkspacePathGuard.Confine(path);

        AssertEx.True(confined.IsConfined);
        AssertEx.Equal(expectedRelative, confined.RelativePath);
    }

    [Test]
    public void Confine_WhenControlCharacters_Rejects()
    {
        var confined = WorkspacePathGuard.Confine("src/a\u0001b");

        AssertEx.False(confined.IsConfined);
    }
}
