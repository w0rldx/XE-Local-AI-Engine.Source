namespace XE_Local_AI_Engine.Tests.Workspace;

using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The parts of the two workspace surveys that only a real Windows filesystem can express.
///     <para>
///         <see cref="WorkspaceFileScannerTests" /> is deliberately OS-agnostic — the scanner is one implementation
///         everywhere, so a green Linux run IS the Windows evidence for its behaviour. The exception is its two
///         security guards, which have to plant a link to prove anything, and on a stock Windows 11 box those tests
///         cannot run at all: creating a symbolic link needs Developer Mode or elevation. Measured on the Windows 11
///         machine this file was written on (Developer Mode off, unelevated), the full backend suite reported
///         <b>seven</b> symbolic-link-privilege skips — six reading <i>"This host does not permit creating symbolic
///         links (on Windows this needs Developer Mode or an elevated process)"</i> and one worded <i>"Creating
///         symbolic links is privilege-dependent on Windows"</i>. So the no-follow guard, which is what stops a
///         listing confined to the workspace from becoming an unconfined one, had no Windows coverage of any kind.
///     </para>
///     <para>
///         <b>What these tests do NOT close.</b> Only two of those seven skips are this scanner's
///         (<c>ListFiles_NeitherFollowsNorEmitsASymbolicLink</c> and
///         <c>ListFiles_WhenTheScanRootIsItselfALink_Refuses</c>). The other five cover different link guards
///         entirely — AgentHome's selected-folder preparation, the sandbox <c>CopyInto</c> destination check,
///         <c>DevelopmentWorkspaceGitConfig.RestoreMinimal</c>, and registered-path resolution — and they remain
///         unproven on an unprivileged Windows box. Junctions would close those too, the same way, and that is
///         worth doing; it is simply not what this file does.
///     </para>
///     <para>
///         NTFS <b>junctions</b> close that gap. They are reparse points, exactly like symbolic links, so the same
///         guard has to reject them — but creating one needs no privilege, so these tests actually execute where the
///         symbolic-link ones skip. They are also the more realistic threat on Windows: a junction is what
///         <c>mklink /J</c> and most build tooling produce, so a workspace can contain one without anyone having
///         arranged it.
///     </para>
/// </summary>
public sealed class WorkspaceFileScannerWindowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-scanner-win-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // Junctions must be removed before the recursive delete, or the delete follows one out of the tree.
                foreach (var directory in new DirectoryInfo(_root).GetDirectories("*", SearchOption.AllDirectories))
                {
                    if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        directory.Delete();
                    }
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best-effort test cleanup.
        }
    }

    /// <summary>
    ///     The confinement property, on the link kind an unprivileged Windows process can actually create: a junction
    ///     inside the workspace naming a directory outside it must be neither descended into nor emitted.
    /// </summary>
    [Test]
    public void ListFiles_NeitherFollowsNorEmitsAnNtfsJunction()
    {
        JunctionSupport.EnsureSupported();

        var workspace = CreateWorkspace();
        var outside = Path.Combine(_root, "outside");
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "leak");
        Write(workspace, "real.txt", "x");

        AssertEx.True(JunctionSupport.TryCreate(Path.Combine(workspace, "escape"), outside),
            "the fixture could not plant the junction, so nothing below would have been proven");

        var listing = WorkspaceFileScanner.ListFiles(workspace, maxEntries: 100, NeverSuppressed, nameGlob: null, CancellationToken.None);

        AssertEx.Equal(1, listing.Count);
        AssertEx.Equal("./real.txt", listing[0]);
    }

    /// <summary>
    ///     The search side of the same guard. Listing and searching walk the same tree but emit different things, and a
    ///     leak here would put the target's CONTENT in front of the model rather than just its path.
    /// </summary>
    [Test]
    public void SearchText_NeverReadsThroughAnNtfsJunction()
    {
        JunctionSupport.EnsureSupported();

        var workspace = CreateWorkspace();
        var outside = Path.Combine(_root, "outside-search");
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "needle-outside\n");
        Write(workspace, "real.txt", "needle-inside\n");

        AssertEx.True(JunctionSupport.TryCreate(Path.Combine(workspace, "escape"), outside),
            "the fixture could not plant the junction, so nothing below would have been proven");

        var matches = WorkspaceFileScanner.SearchText(workspace,
            "needle",
            isRegex: false,
            maxMatches: int.MaxValue,
            maxOutputBytes: 65536,
            NeverSuppressed,
            CancellationToken.None);

        AssertEx.Equal(1, matches.Count);
        AssertEx.False(matches.Any(match => match.Contains("outside", StringComparison.Ordinal)),
            "content reached through a junction must never appear in a survey result");
    }

    /// <summary>
    ///     The scan root itself being a junction is the case the agent can most easily arrange: it creates the directory
    ///     it then asks to list. <c>EnsureNotReachedThroughLink</c> must refuse it, exactly as it refuses a symbolic link.
    /// </summary>
    [Test]
    public void ListFiles_WhenTheScanRootIsItselfAJunction_Refuses()
    {
        JunctionSupport.EnsureSupported();

        var workspace = CreateWorkspace();
        var outside = Path.Combine(_root, "outside-root");
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "leak");

        var linkedRoot = Path.Combine(workspace, "linked");
        AssertEx.True(JunctionSupport.TryCreate(linkedRoot, outside),
            "the fixture could not plant the junction, so nothing below would have been proven");

        _ = Assert.Throws<WorkspaceScanRejectedException>(() =>
            WorkspaceFileScanner.ListFiles(linkedRoot, maxEntries: 100, NeverSuppressed, nameGlob: null, CancellationToken.None));
    }

    /// <summary>
    ///     A tree whose paths run past Windows' historical <c>MAX_PATH</c> of 260 characters, which a real repository
    ///     inside <c>%LOCALAPPDATA%\XE-Local-AI-Engine\development\workspaces\&lt;project&gt;\&lt;task&gt;\</c> reaches
    ///     without trying — that prefix alone is over 70 characters before the repository's own paths start.
    ///     <para>
    ///         <b>What this does and does not prove.</b> It proves the scanner itself introduces no path-length ceiling
    ///         of its own. It cannot prove behaviour on a host with <c>HKLM\SYSTEM\CurrentControlSet\Control\FileSystem
    ///         \LongPathsEnabled = 0</c>, because that is a machine-wide setting: the box this was written on has it set
    ///         to <b>1</b>, where a 399-character path was created, written, read and enumerated without error. A tester
    ///         on a box with it disabled is still the only way to answer the other half.
    ///     </para>
    /// </summary>
    [Test]
    // MAX_PATH is a Windows concept; the OS-agnostic suite covers deep trees generally.
    [RunOn(OS.Windows)]
    public void ListFilesAndSearchText_WorkOnATreeDeeperThanMaxPath()
    {
        var workspace = CreateWorkspace();

        // Long segment names rather than many levels, so the DEPTH ceiling is not what is being measured here.
        var relative = string.Join('/', Enumerable.Repeat("segment" + new string('x', 24), 8));
        Write(workspace, relative + "/needle.txt", "alpha\nneedle here\n");

        var absoluteLength = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar), "needle.txt").Length;
        AssertEx.True(absoluteLength > 260,
            $"the fixture path is only {absoluteLength} characters, so it does not exercise MAX_PATH at all");

        var listing = WorkspaceFileScanner.ListFiles(workspace, maxEntries: 100, NeverSuppressed, nameGlob: null, CancellationToken.None);
        AssertEx.Equal(1, listing.Count);
        AssertEx.Equal("./" + relative + "/needle.txt", listing[0]);

        var matches = WorkspaceFileScanner.SearchText(workspace,
            "needle",
            isRegex: false,
            maxMatches: int.MaxValue,
            maxOutputBytes: 65536,
            NeverSuppressed,
            CancellationToken.None);

        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./" + relative + "/needle.txt:2:needle here", matches[0]);
    }

    private static bool NeverSuppressed(string relativePath) =>
        false;

    private string CreateWorkspace()
    {
        var workspace = Path.Combine(_root, "workspace-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static void Write(string workspace, string relativePath, string content)
    {
        var full = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
