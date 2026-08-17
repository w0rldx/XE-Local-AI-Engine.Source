namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for <see cref="SandboxJailPathGuard" /> — the sandbox path-safety guards on their own, without a
///     provider or a child process. The pair is what the security posture rests on: the lexical jail check rejects a
///     <c>..</c> escape but is blind to symlinks, the component walk rejects a planted link, and the no-follow open
///     closes the swap-after-check race the two managed checks cannot. Real host temp directories and real symlinks are
///     used so the semantics are proven rather than mocked; the symlink cases are Linux-guarded.
///     <see cref="ProcessSandboxRuntimeProviderTests" /> keeps the same matrix at the provider level — these cases pin
///     the guard directly so a future sandbox surface that calls it gets a failing test, not a silent gap.
/// </summary>
public sealed class SandboxJailPathGuardTests : IDisposable
{
    private readonly string _jailRoot = Path.Combine(Path.GetTempPath(), "xe-jail-guard-" + Guid.NewGuid().ToString("N"));

    public SandboxJailPathGuardTests()
    {
        Directory.CreateDirectory(_jailRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_jailRoot))
            {
                Directory.Delete(_jailRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Test]
    public async Task ResolveJailPath_MapsSandboxAbsolutePathsUnderTheJail_AndRejectsEscapes()
    {
        // A sandbox-absolute path is jail-relative, not host-absolute.
        AssertEx.Equal(Path.Combine(_jailRoot, "workspace", "file.txt"),
            SandboxJailPathGuard.ResolveJailPath(_jailRoot, "/workspace/file.txt"));

        // `..` traversal is collapsed by canonicalization and then rejected by the prefix check.
        AssertEx.Throws<UnauthorizedAccessException>(() => SandboxJailPathGuard.ResolveJailPath(_jailRoot, "../../etc/passwd"));
        AssertEx.Throws<UnauthorizedAccessException>(() => SandboxJailPathGuard.ResolveJailPath(_jailRoot, "workspace/../../outside"));

        await Task.CompletedTask;
    }

    [Test]
    public async Task EnsureNoSymlinkComponentsUnderJail_RejectsAnIntermediateOrLeafLink_ButAllowsARealPath()
    {
        var real = Path.Combine(_jailRoot, "workspace", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        await File.WriteAllTextAsync(real, "in-jail");

        // A path made only of real components passes — the guard rejects links, not depth.
        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(_jailRoot, real, "workspace/file.txt");

        if (!OperatingSystem.IsLinux())
        {
            // Real symlink semantics are the Linux guarantee under test.
            return;
        }

        using var outside = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "OUTSIDE-THE-JAIL");

        // An INTERMEDIATE component that is a link out of the jail: ResolveJailPath passes it (lexically it is under
        // the jail), so only the component walk can reject it.
        var linkDirectory = Path.Combine(_jailRoot, "workspace", "link");
        Directory.CreateSymbolicLink(linkDirectory, outside.Path);
        var throughLink = SandboxJailPathGuard.ResolveJailPath(_jailRoot, "workspace/link/secret.txt");
        AssertEx.Throws<UnauthorizedAccessException>(() =>
            SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(_jailRoot, throughLink, "workspace/link/secret.txt"));

        // And a LEAF that is a link to an outside file.
        var linkLeaf = Path.Combine(_jailRoot, "workspace", "leaf");
        File.CreateSymbolicLink(linkLeaf, Path.Combine(outside.Path, "secret.txt"));
        AssertEx.Throws<UnauthorizedAccessException>(() =>
            SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(_jailRoot, linkLeaf, "workspace/leaf"));
    }

    [Test]
    public async Task WriteAndReadJailFileNoFollow_RoundTripsBytes_AndRefusesASymlinkedLeaf()
    {
        var target = Path.Combine(_jailRoot, "workspace", "round-trip.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var content = Encoding.UTF8.GetBytes("jail bytes");
        await SandboxJailPathGuard.WriteJailFileNoFollowAsync(target, content, CancellationToken.None);
        var readBack = await SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(target, int.MaxValue, CancellationToken.None);
        AssertEx.Equal("jail bytes", Encoding.UTF8.GetString(readBack));

        // The requested read bound is enforced on the sized length.
        await AssertEx.ThrowsAsync<InvalidDataException>(() =>
            SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(target, maxBytes: 2, CancellationToken.None));

        if (!OperatingSystem.IsLinux())
        {
            // O_NOFOLLOW is the Linux guarantee; the non-Linux fallback relies on the component walk above.
            return;
        }

        using var outside = new TempDir();
        var outsideFile = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "OUTSIDE-THE-JAIL");

        // The leaf was swapped for a link AFTER the component walk would have run: only the no-follow open catches it,
        // for both the read and the copy-into write.
        var swapped = Path.Combine(_jailRoot, "workspace", "swapped.txt");
        File.CreateSymbolicLink(swapped, outsideFile);
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
            SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(swapped, int.MaxValue, CancellationToken.None));
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
            SandboxJailPathGuard.WriteJailFileNoFollowAsync(swapped, content, CancellationToken.None));
        AssertEx.Equal("OUTSIDE-THE-JAIL", await File.ReadAllTextAsync(outsideFile));
    }

    [Test]
    public async Task ReadHostFileUnderGuard_ReadsWithinTheCap_AndRejectsAnOverCapOrSymlinkedSource()
    {
        var source = Path.Combine(_jailRoot, "host-source.txt");
        await File.WriteAllTextAsync(source, "host bytes");

        AssertEx.Equal("host bytes", Encoding.UTF8.GetString(SandboxJailPathGuard.ReadHostFileUnderGuard(source, maxCopyFileBytes: 1024)));
        AssertEx.Throws<InvalidDataException>(() => SandboxJailPathGuard.ReadHostFileUnderGuard(source, maxCopyFileBytes: 4));

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var link = Path.Combine(_jailRoot, "host-link.txt");
        File.CreateSymbolicLink(link, source);
        AssertEx.Throws<UnauthorizedAccessException>(() => SandboxJailPathGuard.ReadHostFileUnderGuard(link, maxCopyFileBytes: 1024));
    }

    [Test]
    public async Task EnsureNoSymbolicLinkComponents_RejectsALinkedTrustedHostWorkspaceComponent()
    {
        SandboxJailPathGuard.EnsureNoSymbolicLinkComponents(_jailRoot);

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var outside = new TempDir();
        var linked = Path.Combine(_jailRoot, "workspace-link");
        Directory.CreateSymbolicLink(linked, outside.Path);
        AssertEx.Throws<UnauthorizedAccessException>(() => SandboxJailPathGuard.EnsureNoSymbolicLinkComponents(linked));

        await Task.CompletedTask;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
