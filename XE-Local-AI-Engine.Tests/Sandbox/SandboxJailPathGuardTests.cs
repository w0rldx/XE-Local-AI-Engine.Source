namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Unit coverage for <see cref="SandboxJailPathGuard" /> — the sandbox path-safety guards on their own, without a
///     provider or a child process. The pair is what the security posture rests on: the lexical jail check rejects a
///     <c>..</c> escape but is blind to symlinks, the component walk rejects a planted link, and the no-follow open
///     closes the swap-after-check race the two managed checks cannot. Real host temp directories and real symlinks are
///     used so the semantics are proven rather than mocked; the symlink cases are Linux-guarded.
///     <see cref="ProcessSandboxRuntimeProviderTests" /> keeps the same matrix at the provider level — these cases pin
///     the guard directly so a future sandbox surface that calls it gets a failing test, not a silent gap.
/// </summary>
[Category(TestCategories.Unit)]
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
    public async Task EnsureNoSymlinkComponentsUnderJail_AllowsAPathOfRealComponents()
    {
        var real = Path.Combine(_jailRoot, "workspace", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        await File.WriteAllTextAsync(real, "in-jail");

        // A path made only of real components passes — the guard rejects links, not depth.
        AssertEx.DoesNotThrow(() => SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(_jailRoot, real, "workspace/file.txt"),
            "a nested path of real components is not an escape.");
    }

    [Test]
    // Real symlink semantics are the Linux guarantee under test.
    [RunOn(OS.Linux)]
    public async Task EnsureNoSymlinkComponentsUnderJail_RejectsAnIntermediateOrLeafLinkOutOfTheJail()
    {
        Directory.CreateDirectory(Path.Combine(_jailRoot, "workspace"));

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
    public async Task WriteAndReadJailFileNoFollow_RoundTripsBytes_AndEnforcesTheReadBound()
    {
        var target = Path.Combine(_jailRoot, "workspace", "round-trip.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var content = Encoding.UTF8.GetBytes("jail bytes");
        await SandboxJailPathGuard.WriteJailFileNoFollowAsync(target, content, CancellationToken.None);
        var readBack = await SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(_jailRoot, target, int.MaxValue, CancellationToken.None);
        AssertEx.Equal("jail bytes", Encoding.UTF8.GetString(readBack));

        // The requested read bound is enforced on the sized length.
        await AssertEx.ThrowsAsync<InvalidDataException>(() =>
            SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(_jailRoot, target, maxBytes: 2, CancellationToken.None));
    }

    [Test]
    // O_NOFOLLOW is the Linux guarantee; the non-Linux fallback relies on the component walk instead.
    [RunOn(OS.Linux)]
    public async Task ReadAndWriteJailFileNoFollow_RefuseALeafSwappedForASymlinkAfterTheComponentWalk()
    {
        Directory.CreateDirectory(Path.Combine(_jailRoot, "workspace"));
        var content = Encoding.UTF8.GetBytes("jail bytes");

        using var outside = new TempDir();
        var outsideFile = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "OUTSIDE-THE-JAIL");

        // The leaf was swapped for a link AFTER the component walk would have run: only the no-follow open catches it,
        // for both the read and the copy-into write.
        var swapped = Path.Combine(_jailRoot, "workspace", "swapped.txt");
        File.CreateSymbolicLink(swapped, outsideFile);
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
            SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(_jailRoot, swapped, int.MaxValue, CancellationToken.None));
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
            SandboxJailPathGuard.WriteJailFileNoFollowAsync(swapped, content, CancellationToken.None));
        AssertEx.Equal("OUTSIDE-THE-JAIL", await File.ReadAllTextAsync(outsideFile));
    }

    [Test]
    // /proc/self/fd is the Linux handle check under test; the Windows branch is pinned by PathContainmentTests.
    [RunOn(OS.Linux)]
    public async Task ReadJailFileBytesNoFollow_RefusesADirectorySwappedForALinkAfterTheComponentWalk()
    {
        var workspace = Path.Combine(_jailRoot, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "secret.txt"), "in-jail");
        var target = Path.Combine(workspace, "secret.txt");

        // In-root control: the same path reads while its directory is real.
        AssertEx.Equal("in-jail", Encoding.UTF8.GetString(await SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(_jailRoot, target, int.MaxValue, CancellationToken.None)));

        using var outside = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "OUTSIDE-THE-JAIL");

        // The directory is swapped for a link AFTER the component walk would have passed it: calling the post-walk read directly is that
        // interleaving. O_NOFOLLOW guards only the leaf, a regular file here, so the open succeeds through the link.
        Directory.Delete(workspace, recursive: true);
        Directory.CreateSymbolicLink(workspace, outside.Path);

        var refused = await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
            SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(_jailRoot, target, int.MaxValue, CancellationToken.None));
        AssertEx.Contains(refused.Message, "outside its trusted root", message: "The refusal must come from the opened-handle check, not the open.");
    }

    [Test]
    // A jail under a linked temp directory is a Linux symlink case; Windows root spelling is pinned by PathContainmentTests.
    [RunOn(OS.Linux)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReadFileAndCopyOut_ThroughASymlinkedJailRoot_StillRead(bool rootItselfIsTheLink)
    {
        using var scratch = new TempDir();
        var realJail = Path.Combine(scratch.Path, "real", "jail");
        Directory.CreateDirectory(Path.Combine(realJail, "workspace"));
        await File.WriteAllTextAsync(Path.Combine(realJail, "workspace", "file.txt"), "in-jail");
        string jailRoot;
        if (rootItselfIsTheLink)
        {
            jailRoot = Path.Combine(scratch.Path, "jail-link");
            Directory.CreateSymbolicLink(jailRoot, realJail);
        }
        else
        {
            Directory.CreateSymbolicLink(Path.Combine(scratch.Path, "alias"), Path.Combine(scratch.Path, "real"));
            jailRoot = Path.Combine(scratch.Path, "alias", "jail");
        }

        AssertEx.Equal("in-jail", await SandboxFileSurveyOperations.ReadFileAsync(jailRoot, "/workspace/file.txt", maxBytes: 1024, CancellationToken.None));

        var destination = Path.Combine(scratch.Path, "copied.txt");
        await SandboxFileSurveyOperations.CopyOutAsync(jailRoot,
            new SandboxCopyRequest
            {
                SourcePath = "/workspace/file.txt",
                DestinationPath = destination
            },
            CancellationToken.None);
        AssertEx.Equal("in-jail", await File.ReadAllTextAsync(destination));
    }

    [Test]
    public async Task ReadHostFileUnderGuard_ReadsWithinTheCap_AndRejectsAnOverCapSource()
    {
        var source = Path.Combine(_jailRoot, "host-source.txt");
        await File.WriteAllTextAsync(source, "host bytes");

        AssertEx.Equal("host bytes", Encoding.UTF8.GetString(SandboxJailPathGuard.ReadHostFileUnderGuard(source, maxCopyFileBytes: 1024)));
        AssertEx.Throws<InvalidDataException>(() => SandboxJailPathGuard.ReadHostFileUnderGuard(source, maxCopyFileBytes: 4));
    }

    [Test]
    // The cap leg above is asserted everywhere; only the symlink leg needs real Linux link semantics.
    [RunOn(OS.Linux)]
    public async Task ReadHostFileUnderGuard_RejectsASymlinkedSource()
    {
        var source = Path.Combine(_jailRoot, "host-source.txt");
        await File.WriteAllTextAsync(source, "host bytes");

        var link = Path.Combine(_jailRoot, "host-link.txt");
        File.CreateSymbolicLink(link, source);
        AssertEx.Throws<UnauthorizedAccessException>(() => SandboxJailPathGuard.ReadHostFileUnderGuard(link, maxCopyFileBytes: 1024));
    }

    [Test]
    // The reads go through the libc no-follow open on Linux only, and a second writable handle on the file
    // being read is a Linux-shareable open — the Windows fallback denies it.
    [RunOn(OS.Linux)]
    public async Task BothReadLegs_RefuseAFileThatGrewAfterItWasSized_RatherThanReturningAStaleCopy()
    {
        await AssertGrowthAfterSizingIsRefusedAsync("jail-growing.bin",
            path => SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(_jailRoot, path, int.MaxValue, CancellationToken.None));

        await AssertGrowthAfterSizingIsRefusedAsync("host-growing.bin",
            path => Task.Run(() => SandboxJailPathGuard.ReadHostFileUnderGuard(path, long.MaxValue)));
    }

    [Test]
    public async Task EnsureNoSymbolicLinkComponents_AcceptsARealTrustedHostWorkspacePath()
    {
        AssertEx.DoesNotThrow(() => SandboxJailPathGuard.EnsureNoSymbolicLinkComponents(_jailRoot),
            "a real host workspace directory is not a linked component.");

        await Task.CompletedTask;
    }

    [Test]
    // The clean-path leg above is asserted everywhere; only the link leg needs real Linux link semantics.
    [RunOn(OS.Linux)]
    public async Task EnsureNoSymbolicLinkComponents_RejectsALinkedTrustedHostWorkspaceComponent()
    {
        using var outside = new TempDir();
        var linked = Path.Combine(_jailRoot, "workspace-link");
        Directory.CreateSymbolicLink(linked, outside.Path);
        AssertEx.Throws<UnauthorizedAccessException>(() => SandboxJailPathGuard.EnsureNoSymbolicLinkComponents(linked));

        await Task.CompletedTask;
    }

    // The growth check is a TOCTOU detector, so pinning it means racing it: there is no seam to synchronize on,
    // because the whole point is to catch a writer the reader cannot see. The read is therefore given a
    // multi-megabyte file — the guard sizes it, copies that many bytes, then probes one byte past the sized length,
    // so the window spans milliseconds — while an appender spins for the whole read. Each attempt is retried a few
    // times so one scheduling hiccup cannot turn the pin into a flake.
    private async Task AssertGrowthAfterSizingIsRefusedAsync(string fileName, Func<string, Task> readAsync)
    {
        var target = Path.Combine(_jailRoot, fileName);
        await File.WriteAllBytesAsync(target, new byte[8 * 1024 * 1024]);

        using var appending = new ManualResetEventSlim(false);
        using var stop = new ManualResetEventSlim(false);
        var appender = Task.Run(() =>
        {
            using var handle = File.OpenHandle(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var offset = RandomAccess.GetLength(handle);
            var oneByte = new byte[]
            {
                0x41
            };
            while (!stop.IsSet)
            {
                RandomAccess.Write(handle, oneByte, offset);
                offset++;
                appending.Set();
            }
        });

        try
        {
            AssertEx.True(appending.Wait(TimeSpan.FromSeconds(10)), "the appender never started");

            InvalidDataException? refusal = null;
            for (var attempt = 0; attempt < 10 && refusal is null; attempt++)
            {
                try
                {
                    await readAsync(target);
                }
                catch (InvalidDataException exception)
                {
                    refusal = exception;
                }
            }

            AssertEx.Contains(AssertEx.NotNull(refusal, "a file growing under the read was copied instead of refused").Message,
                "grew while");
        }
        finally
        {
            stop.Set();
            await appender;
        }
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
