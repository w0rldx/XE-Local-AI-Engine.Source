namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Host patch apply coverage. Uses REAL host <c>git</c> (no Docker, no Ollama) to
///     generate patch-export-shaped patches (rooted at a temp <c>selected/&lt;alias&gt;/</c> repo, with the exact diff
///     flags) and applies them through <see cref="NodePatchApplyService" /> to temp host folders mapped by a fake
///     <see cref="ISelectedFolderResolver" />. Proves the traversal/alias guards, binary-reject default, the gate on
///     <c>changes.patch</c> presence (never <c>changed-files.json</c>), folder-relative logging, and host-path redaction.
///     The git baseline and the host folder are seeded with the SAME pre-image so a generated patch applies cleanly.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodePatchApplyServiceTests : IDisposable
{
    private static readonly string[] PatchDiffArgs =
        ["diff", "--binary", "--find-renames=50%", "--find-copies=50%", "--src-prefix=a/", "--dst-prefix=b/", "HEAD", "--", "."];

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    public async Task ApplyApprovedAsync_WithModifiedAndDeletedFiles_AppliesToHostAndReportsFiles()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01",
            hostRoot,
            ("src/App.cs", "line1\nline2\n", "line1\nline2\nline3\n"),
            ("old/Gone.cs", "remove me\n", null));
        await WritePatchAsync(harness, "run-apply", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-apply"
        });

        AssertEx.True(result.Applied, $"a clean patch applies. rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Empty(result.Rejections);
        AssertEx.Equal("line1\nline2\nline3\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")));
        AssertEx.False(File.Exists(Path.Combine(hostRoot, "old", "Gone.cs")), "the deleted file is removed on the host");
        AssertEx.Contains(result.AppliedFiles, file => file is { Alias: "repo-01", RelativePath: "src/App.cs", ChangeType: "modified" });
        AssertEx.Contains(result.AppliedFiles, file => file is { Alias: "repo-01", RelativePath: "old/Gone.cs", ChangeType: "deleted" });
    }

    [Test]
    public async Task PreviewAsync_DoesNotMutateTheHostAndReportsCounts()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        var before = await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs"));
        await WritePatchAsync(harness, "run-preview", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-preview"
        });

        AssertEx.True(preview.CanApply, $"the patch checks clean. rejections: {string.Join(separator: ';', preview.Rejections)}");
        AssertEx.Equal(before, await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")), "preview must not mutate the host");
        var entry = preview.Files.Single(file => file.RelativePath == "src/App.cs");
        AssertEx.Equal(expected: 1, entry.Added);
        AssertEx.Equal(expected: 0, entry.Removed);
    }

    [Test]
    public async Task PreviewAsync_WithTraversalPath_RejectsAndMutatesNothing()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostAsync(hostRoot, ("keep.txt", "keep\n"));

        // Hand-craft a patch whose target traverses out of the alias root.
        var patch =
            "diff --git a/repo-01/../escape.txt b/repo-01/../escape.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..e69de29\n" +
            "--- /dev/null\n" +
            "+++ b/repo-01/../escape.txt\n" +
            "@@ -0,0 +1 @@\n" +
            "+pwned\n";
        await WritePatchAsync(harness, "run-traversal", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-traversal"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-traversal"
        });

        AssertEx.False(preview.CanApply, "a traversal target is rejected");
        AssertEx.NotEmpty(preview.Rejections);
        AssertEx.False(result.Applied);
        AssertEx.False(File.Exists(Path.Combine(Path.GetDirectoryName(hostRoot)!, "escape.txt")), "nothing is written outside the root");
    }

    [Test]
    public async Task ApplyApprovedAsync_NeverWritesOutsideTheResolvedRoot()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        var sibling = NewTempDir();
        var siblingSnapshot = Directory.GetFileSystemEntries(sibling).Length;

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("a.txt", "x\n", "x\ny\n"));
        await WritePatchAsync(harness, "run-root", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-root"
        });

        AssertEx.True(result.Applied, $"rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Equal(siblingSnapshot, Directory.GetFileSystemEntries(sibling).Length, "the unrelated sibling dir is untouched");
    }

    [Test]
    public async Task ApplyApprovedAsync_WithUnknownAlias_Rejects()
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");

        // The patch references repo-99, which is not registered in the resolver. Generate it in a throwaway host dir.
        var throwaway = NewTempDir();
        var patch = await GenerateGPatchAsync("repo-99", throwaway, ("a.txt", "orig\n", "orig\nchanged\n"));
        await WritePatchAsync(harness, "run-unknown", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-unknown"
        });

        AssertEx.False(result.Applied);
        AssertEx.Contains(result.Rejections, reason => reason.Contains("repo-99", StringComparison.Ordinal) && reason.Contains("not a registered", StringComparison.Ordinal));
    }

    [Test]
    public async Task PreviewAsync_WithCrossAliasRename_Rejects()
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");
        harness.AddFolder("repo-02");

        var patch =
            "diff --git a/repo-01/x.txt b/repo-02/y.txt\n" +
            "similarity index 100%\n" +
            "rename from repo-01/x.txt\n" +
            "rename to repo-02/y.txt\n";
        await WritePatchAsync(harness, "run-cross", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-cross"
        });

        AssertEx.False(preview.CanApply);
        AssertEx.Contains(preview.Rejections, reason => reason.Contains("across selected folders", StringComparison.Ordinal));
    }

    [Test]
    public async Task ApplyApprovedAsync_WithMultipleAliases_LandsEachUnderItsOwnRoot()
    {
        var harness = NewHarness();
        var root01 = harness.AddFolder("repo-01");
        var root02 = harness.AddFolder("repo-02");

        var patch = await GenerateMultiAliasPatchAsync(root01,
            root02,
            ("repo-01", "one.txt", "one\n", "one\nupdated\n"),
            ("repo-02", "two.txt", "two\n", "two\nupdated\n"));
        await WritePatchAsync(harness, "run-multi", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-multi"
        });

        AssertEx.True(result.Applied, $"rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Equal("one\nupdated\n", await File.ReadAllTextAsync(Path.Combine(root01, "one.txt")));
        AssertEx.Equal("two\nupdated\n", await File.ReadAllTextAsync(Path.Combine(root02, "two.txt")));
        AssertEx.False(File.Exists(Path.Combine(root01, "two.txt")), "no cross-contamination between alias roots");
    }

    [Test]
    public async Task ApplyApprovedAsync_WithBinaryBlock_RejectsByDefaultButAppliesWhenAllowed()
    {
        var rejectingHarness = NewHarness();
        var rejectingRoot = rejectingHarness.AddFolder("repo-01");
        await SeedHostBinaryAsync(rejectingRoot, "blob.bin", [0x00, 0x01, 0x02, 0x03]);

        var patch = await GenerateBinaryPatchAsync("repo-01", "blob.bin", [0x00, 0x01, 0x02, 0x03], [0x00, 0x01, 0x02, 0x03, 0xFF, 0x10]);
        await WritePatchAsync(rejectingHarness, "run-binary", patch);

        var rejected = await rejectingHarness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-binary"
        });
        AssertEx.False(rejected.Applied, "a binary block is rejected by default");
        var preview = await rejectingHarness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-binary"
        });
        AssertEx.True(preview.ContainsBinary, "the binary block is detected");
        AssertEx.Contains(preview.Rejections, reason => reason.Contains("binary", StringComparison.Ordinal));

        // With the option on, the same patch is no longer rejected for the binary reason and applies.
        var allowed = NewHarness(true);
        var allowedRoot = allowed.AddFolder("repo-01");
        await SeedHostBinaryAsync(allowedRoot, "blob.bin", [0x00, 0x01, 0x02, 0x03]);
        await WritePatchAsync(allowed, "run-binary", patch);

        var allowedResult = await allowed.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-binary"
        });
        AssertEx.True(allowedResult.Applied, $"a binary patch applies when allowed. rejections: {string.Join(separator: ';', allowedResult.Rejections)}");
    }

    [Test]
    public async Task ApplyApprovedAsync_WhenContextNoLongerMatches_ReportsConflictAndMutatesNothing()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // Generate a patch against an "alpha\n" pre-image, then overwrite the host so the context no longer matches.
        var throwaway = NewTempDir();
        var patch = await GenerateGPatchAsync("repo-01", throwaway, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await SeedHostAsync(hostRoot, ("src/App.cs", "totally different content\n"));
        var before = await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs"));
        await WritePatchAsync(harness, "run-conflict", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-conflict"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-conflict"
        });

        AssertEx.False(preview.CanApply, "a conflicting context fails the check");
        AssertEx.NotEmpty(preview.Rejections);
        AssertEx.False(result.Applied);
        AssertEx.Equal(before, await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")), "a failed apply mutates nothing");
    }

    [Test]
    public async Task PreviewAsync_WhenPatchMissingOrEmpty_RejectsAndIgnoresChangedFilesJson()
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");

        // No changes.patch at all.
        var missing = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-missing"
        });
        AssertEx.False(missing.CanApply);

        // changed-files.json present but no changes.patch — must still reject (never gate on changed-files.json).
        var patchesDir = Path.Combine(harness.AgentHomeRoot, "runs", "run-meta-only", "patches");
        Directory.CreateDirectory(patchesDir);
        await File.WriteAllTextAsync(Path.Combine(patchesDir, "changed-files.json"), "[{\"alias\":\"repo-01\"}]");

        var metaOnly = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-meta-only"
        });
        AssertEx.False(metaOnly.CanApply, "a present changed-files.json must not enable apply when changes.patch is absent");

        // Empty changes.patch — rejected.
        var emptyDir = Path.Combine(harness.AgentHomeRoot, "runs", "run-empty", "patches");
        Directory.CreateDirectory(emptyDir);
        await File.WriteAllTextAsync(Path.Combine(emptyDir, "changes.patch"), string.Empty);

        var empty = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-empty"
        });
        AssertEx.False(empty.CanApply, "an empty patch is rejected");
    }

    [Test]
    public async Task PreviewAsync_WithInjectionRunId_RejectsBeforeAnyPathAccess()
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");

        foreach (var badRunId in new[]
                 {
                     "../escape",
                     "run/../../etc",
                     "a/b",
                     "..",
                     "with\\back"
                 })
        {
            var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
            {
                RunId = badRunId
            });
            AssertEx.False(preview.CanApply, $"an injection run id '{badRunId}' is rejected");
        }
    }

    [Test]
    public async Task ApplyApprovedAsync_LogsAppliedFilesFolderRelativeWithoutHostPath()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // The run's logs/ dir must exist for the K logger to write (mirrors the real run layout).
        Directory.CreateDirectory(Path.Combine(harness.AgentHomeRoot, "runs", "run-log", "logs"));

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, "run-log", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-log"
        });
        AssertEx.True(result.Applied, $"rejections: {string.Join(separator: ';', result.Rejections)}");

        var eventsPath = Path.Combine(harness.AgentHomeRoot, "runs", "run-log", "logs", "events.jsonl");
        AssertEx.True(File.Exists(eventsPath), "the run events log exists");
        var events = await File.ReadAllTextAsync(eventsPath);
        AssertEx.Contains(events, "patch_applied");
        AssertEx.Contains(events, "repo-01/src/App.cs");
        AssertEx.False(events.Contains(hostRoot, StringComparison.Ordinal), "the log must not leak a host path");
    }

    [Test]
    public async Task PreviewAsync_RedactsHostPathFromRejections()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // A conflicting context forces git to surface an error that mentions the working directory; assert it is redacted.
        var throwaway = NewTempDir();
        var patch = await GenerateGPatchAsync("repo-01", throwaway, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await SeedHostAsync(hostRoot, ("src/App.cs", "different\n"));
        await WritePatchAsync(harness, "run-redact", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-redact"
        });

        AssertEx.False(preview.CanApply);
        AssertEx.True(preview.Rejections.All(reason => !reason.Contains(hostRoot, StringComparison.Ordinal)),
            "no rejection string may contain the host root path");
    }

    [Test]
    public async Task PreviewAsync_WithMismatchedHeaderAndBodyPath_Rejects()
    {
        // FIX 1 regression: a crafted patch whose diff header looks clean but whose body destination path
        // diverges to a different alias. Guards must be authoritative from body paths, independently of git.
        var harness = NewHarness();
        harness.AddFolder("repo-01");

        // Header claims repo-01 on both sides. The body destination line targets repo-02 — a cross-alias escape.
        var patch =
            "diff --git a/repo-01/safe.txt b/repo-01/safe.txt\n" +
            "index 0000001..0000002 100644\n" +
            "--- a/repo-01/safe.txt\n" +
            "+++ b/repo-02/evil.txt\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n";
        await WritePatchAsync(harness, "run-mismatch", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-mismatch"
        });

        AssertEx.False(preview.CanApply, "a header/body path mismatch is rejected by our guard, not git");
        AssertEx.NotEmpty(preview.Rejections);
    }

    [Test]
    public async Task ApplyApprovedAsync_WithSymlinkEscapingRoot_Rejects()
    {
        // FIX 1 + EscapesViaReparsePoint coverage: a symlinked intermediate directory inside the host root that
        // points outside it must be rejected before git touches any file.

        // This is a SECURITY guard, so a host that cannot plant the link must SKIP and say so — never return
        // early. This test used to swallow the creation failure and return, which reported it as a PASS having
        // asserted nothing; on a stock Windows box, where symlinks need Developer Mode or elevation, that is
        // exactly what happened, so the one escape this test exists to catch went unproven on the shipping
        // platform in silence. The swallow also matched only IOException, missing the UnauthorizedAccessException
        // that Windows raises the rest of the time.
        SymlinkSupport.EnsureSupported();

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // Create a subdirectory that is a symlink pointing outside the root.
        var outside = NewTempDir();
        var symlinkDir = Path.Combine(hostRoot, "subdir");
        Directory.CreateSymbolicLink(symlinkDir, outside);

        // A valid patch that would write into subdir/ — the symlink escape must be caught.
        var throwaway = NewTempDir();
        var patch = await GenerateGPatchAsync("repo-01", throwaway, ("subdir/target.txt", "before\n", "after\n"));
        await WritePatchAsync(harness, "run-symlink", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-symlink"
        });

        AssertEx.False(result.Applied, "a symlink that escapes the root is rejected");
        AssertEx.Contains(result.Rejections, reason => reason.Contains("symlink", StringComparison.Ordinal));
        AssertEx.False(File.Exists(Path.Combine(outside, "target.txt")), "nothing is written outside via symlink");
    }

    /// <summary>
    ///     The junction twin of <see cref="ApplyApprovedAsync_WithSymlinkEscapingRoot_Rejects" />, which skips on a
    ///     stock Windows box because symbolic links need Developer Mode or elevation. A junction is the same reparse
    ///     point and needs no privilege, so this proves the escape guard on the shipping platform.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WithJunctionEscapingRoot_Rejects()
    {
        JunctionSupport.EnsureSupported();

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var outside = NewTempDir();
        AssertEx.True(JunctionSupport.TryCreate(Path.Combine(hostRoot, "subdir"), outside),
            "the fixture must be able to plant a junction once EnsureSupported has passed");

        var throwaway = NewTempDir();
        var patch = await GenerateGPatchAsync("repo-01", throwaway, ("subdir/target.txt", "before\n", "after\n"));
        await WritePatchAsync(harness, "run-junction", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-junction"
        });

        AssertEx.False(result.Applied, "a junction that escapes the root is rejected");
        // Deliberately not asserting the rejection WORDING. The symbolic-link twin matches "symlink", but how the
        // product words a junction rejection is its own choice; the security property is that it refused and wrote
        // nothing outside. Pinning the string here would fail the test for a reason that is not a defect.
        AssertEx.NotEmpty(result.Rejections);
        AssertEx.False(File.Exists(Path.Combine(outside, "target.txt")), "nothing is written outside via a junction");
    }

    [Test]
    public async Task PreviewAsync_WhenPatchExceedsMaxPatchBytes_Rejects()
    {
        // MaxPatchBytes over-budget gate: the file-size check must reject before reading the patch content.
        var agentHomeStateRoot = NewTempDir();
        var agentHomeRoot = Path.Combine(agentHomeStateRoot, "agent-home");
        Directory.CreateDirectory(agentHomeRoot);

        var resolver = new FakeResolver();
        resolver.Add(Guid.NewGuid(), "repo-01", NewTempDir());

        const int tinyBudget = 16;
        var options = Options.Create(new AgentHomeOptions
        {
            RootPath = agentHomeStateRoot,
            PatchApplyTimeoutSeconds = 120
        });
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithAgentHomeMaxPatchBytes(tinyBudget)
                                                     .Build();
        var scopeFactory = new ServiceCollection()
                           .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(TimeProvider.System))
                           .BuildServiceProvider();
        var service = new NodePatchApplyService(resolver,
            options,
            runtimeSettings,
            new FakeNodeDataDirectory(agentHomeStateRoot),
            new StubIdentityProvider(),
            scopeFactory.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodePatchApplyService>.Instance);

        var patchesDir = Path.Combine(agentHomeRoot, "runs", "run-big", "patches");
        Directory.CreateDirectory(patchesDir);
        await File.WriteAllTextAsync(Path.Combine(patchesDir, "changes.patch"), new string(c: 'x', tinyBudget + 1));

        var preview = await service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-big"
        });

        AssertEx.False(preview.CanApply, "a patch exceeding MaxPatchBytes is rejected");
        AssertEx.Contains(preview.Rejections, reason => reason.Contains("maximum allowed size", StringComparison.Ordinal));
    }

    [Test]
    public async Task ApplyApprovedAsync_WithSpaceInFolderName_IsNotFalselyRejected()
    {
        // FIX 2 regression test: a selected-folder alias that contains no space, but whose file path contains
        // a directory named "dir b" (space + single letter), must not be mis-parsed via the header. The patch
        // is generated with real git so the body paths are canonical; our body-first parsing must handle them.
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // Generate a patch for a file nested under a directory whose name could confuse a naive header split.
        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("dir b/file.cs", "old\n", "old\nnew\n"));
        await WritePatchAsync(harness, "run-dirb", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-dirb"
        });

        AssertEx.True(result.Applied, $"a file under 'dir b/' must not be falsely rejected. rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Equal("old\nnew\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "dir b", "file.cs")));
    }

    [Test]
    public async Task PreviewAndApply_WithTraversingModeOnlyBlock_RejectsWithoutTouchingHost()
    {
        // FIX N-1: a synthetic mode-only block (no unified-diff body lines) whose header path contains ".."
        // must be rejected by our own guard before git ever runs. The block is hand-crafted because the patch-export
        // baseline uses core.filemode false so real git never emits mode-change blocks.
        var harness = NewHarness();
        harness.AddFolder("repo-01");

        var traversingPatch =
            "diff --git a/repo-01/../../../etc/evil b/repo-01/../../../etc/evil\n" +
            "old mode 100644\n" +
            "new mode 100755\n";
        await WritePatchAsync(harness, "run-modeonly-traversal", traversingPatch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-modeonly-traversal"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-modeonly-traversal"
        });

        AssertEx.False(preview.CanApply, "a mode-only block with a traversing header path is rejected by our guard");
        AssertEx.NotEmpty(preview.Rejections);
        AssertEx.False(result.Applied);
    }

    [Test]
    public async Task PreviewAsync_WithCleanModeOnlyBlock_IsNotRejectedForPathGuardReasons()
    {
        // FIX N-1 positive case: a synthetic mode-only block whose header path is within the alias root must
        // pass the alias + traversal guard (TargetRelativePaths is populated with the header-derived path).
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostAsync(hostRoot, ("sub/x.sh", "#!/bin/sh\necho hi\n"));

        var cleanPatch =
            "diff --git a/repo-01/sub/x.sh b/repo-01/sub/x.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n";
        await WritePatchAsync(harness, "run-modeonly-clean", cleanPatch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-modeonly-clean"
        });

        AssertEx.False(preview.Rejections.Any(reason =>
                reason.Contains("traversal", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("outside its folder", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("no alias", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("unparseable", StringComparison.OrdinalIgnoreCase)),
            "a clean mode-only block must not be rejected for path-guard reasons");
    }

    /// <summary>
    ///     The per-alias sub-patch copies the operator's source into the shared temp directory, so it is created
    ///     0600 and gone before the call returns. Narrowing after creation leaves a umask-wide window in which
    ///     another local user can read it.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WritesItsTempSubPatchUserOnlyAndRemovesIt()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Unix file modes are what this asserts; the Windows half of the same guard is the ACL SecureFilePermissions applies.");
        }

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, "run-tempmode", patch);

        // The file exists only while git reads it, so a watcher captures the mode as each sub-patch appears. One
        // watch, one directory, one filter: the inotify budget is not a test suite's to spend freely.
        var observed = new List<(string Path, UnixFileMode Mode)>();
        var gate = new Lock();
        using (var watcher = new FileSystemWatcher(Path.GetTempPath(), "agenthome-apply-*.patch"))
        {
            watcher.Created += (_, args) =>
            {
                // The Skip above already left on Windows; this is what tells the platform analyzer so, since it
                // cannot see through the skip into a lambda.
                if (OperatingSystem.IsWindows())
                {
                    return;
                }

                try
                {
                    var mode = File.GetUnixFileMode(args.FullPath);
                    lock (gate)
                    {
                        observed.Add((args.FullPath, mode));
                    }
                }
                catch (FileNotFoundException)
                {
                    // Deleted between the event and the read; the deletion assertion below is what covers that.
                }
                catch (IOException)
                {
                    // Best-effort observation.
                }
            };
            watcher.EnableRaisingEvents = true;

            var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
            {
                RunId = "run-tempmode"
            });
            AssertEx.True(result.Applied, $"rejections: {string.Join(separator: ';', result.Rejections)}");
        }

        (string Path, UnixFileMode Mode)[] captured;
        lock (gate)
        {
            captured = [.. observed];
        }

        AssertEx.NotEmpty(captured,
            "no sub-patch file was observed being created, so this test asserted nothing about its permissions.");
        AssertEx.True(captured.All(entry => entry.Mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite)),
            $"a sub-patch was created readable beyond the owner: {string.Join(separator: ';', captured.Select(entry => entry.Mode))}.");

        // Per observed path rather than "the temp directory is empty of them": the watcher sees every apply on this
        // box, and a sibling test's sub-patch may legitimately be in flight right now.
        await AssertEx.EventuallyAsync(() => captured.All(entry => !File.Exists(entry.Path)),
            TimeSpan.FromSeconds(10),
            "a sub-patch was left behind on disk after its apply finished.");
    }

    [Test]
    public async Task ApplyApprovedAsync_WithTheHashThePreviewReported_Applies()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, "run-bound", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-bound"
        });
        AssertEx.True(preview.CanApply, $"rejections: {string.Join(separator: ';', preview.Rejections)}");
        AssertEx.NotNull(preview.PatchSha256, "a preview that read a patch reports its hash");
        AssertEx.Equal(expected: 64, preview.PatchSha256!.Length, "SHA-256 renders as 64 lowercase hex characters");

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-bound",
            ExpectedPatchSha256 = preview.PatchSha256
        });

        AssertEx.True(result.Applied, $"the hash the preview reported is the hash the apply accepts. rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Equal("alpha\nbravo\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")));
    }

    /// <summary>
    ///     The read-then-apply gap. The patch is REPLACED between preview and apply with one that would itself
    ///     apply cleanly, so only the hash binding catches it: an operator lands the diff they read, not the diff
    ///     that replaced it.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WhenThePatchChangedAfterThePreview_RejectsAndMutatesNothing()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var reviewed = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, "run-swap", reviewed);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-swap"
        });
        AssertEx.True(preview.CanApply, $"rejections: {string.Join(separator: ';', preview.Rejections)}");

        // The swapped-in patch targets the SAME pre-image, so it passes every check the reviewed one passed.
        var swapped = await GenerateGPatchAsync("repo-01", NewTempDir(), ("src/App.cs", "alpha\n", "alpha\nsomething else entirely\n"));
        await WritePatchAsync(harness, "run-swap", swapped);
        var before = await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs"));

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-swap",
            ExpectedPatchSha256 = preview.PatchSha256
        });

        AssertEx.False(result.Applied, "a patch that changed since the preview is refused");
        AssertEx.Contains(result.Rejections, reason => reason.Contains("changed since it was previewed", StringComparison.Ordinal));
        AssertEx.Equal(before, await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")), "the host is untouched by a refused apply");

        // And the swapped patch is not refused on its own terms — the refusal above is the BINDING, not a second
        // validation failure that would have rejected it anyway.
        var unbound = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-swap"
        });
        AssertEx.True(unbound.CanApply, "the swapped patch would itself have applied cleanly, which is what makes the binding the only guard that catches it");
    }

    /// <summary>
    ///     A patch writing under a selected folder's <c>.git</c> writes the configuration git executes: hooks and
    ///     the drivers <c>AgentHomeGitHardening</c> keeps node-owned. Refused in every path position, and through
    ///     <c>.GIT</c> on a case-folding host.
    /// </summary>
    [Test]
    [Arguments("destination", "diff --git a/repo-01/.git/config b/repo-01/.git/config\nnew file mode 100644\n--- /dev/null\n+++ b/repo-01/.git/config\n@@ -0,0 +1 @@\n+[core]\n")]
    [Arguments("source", "diff --git a/repo-01/.git/hooks/pre-commit b/repo-01/.git/hooks/pre-commit\ndeleted file mode 100755\n--- a/repo-01/.git/hooks/pre-commit\n+++ /dev/null\n@@ -1 +0,0 @@\n-#!/bin/sh\n")]
    [Arguments("rename to", "diff --git a/repo-01/safe.txt b/repo-01/.git/config\nsimilarity index 100%\nrename from repo-01/safe.txt\nrename to repo-01/.git/config\n")]
    [Arguments("rename from", "diff --git a/repo-01/.git/config b/repo-01/safe.txt\nsimilarity index 100%\nrename from repo-01/.git/config\nrename to repo-01/safe.txt\n")]
    [Arguments("copy to", "diff --git a/repo-01/safe.txt b/repo-01/.git/config\nsimilarity index 100%\ncopy from repo-01/safe.txt\ncopy to repo-01/.git/config\n")]
    [Arguments("nested", "diff --git a/repo-01/sub/.git/objects/x b/repo-01/sub/.git/objects/x\nnew file mode 100644\n--- /dev/null\n+++ b/repo-01/sub/.git/objects/x\n@@ -0,0 +1 @@\n+x\n")]
    [Arguments("mixed case", "diff --git a/repo-01/.GiT/config b/repo-01/.GiT/config\nnew file mode 100644\n--- /dev/null\n+++ b/repo-01/.GiT/config\n@@ -0,0 +1 @@\n+[core]\n")]
    [Arguments("mode-only header", "diff --git a/repo-01/.git/hooks/pre-commit b/repo-01/.git/hooks/pre-commit\nold mode 100644\nnew mode 100755\n")]
    public async Task PreviewAndApply_WithAGitDirectoryTarget_RejectWithoutTouchingHost(string position, string patch)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostAsync(hostRoot, ("safe.txt", "safe\n"));
        var gitDirectory = Path.Combine(hostRoot, ".git");

        await WritePatchAsync(harness, "run-gitdir", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-gitdir"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-gitdir"
        });

        AssertEx.False(preview.CanApply, $"a {position} path under .git is rejected");
        AssertEx.Contains(preview.Rejections, reason => reason.Contains("git directory", StringComparison.Ordinal));
        AssertEx.False(result.Applied);
        AssertEx.False(Directory.Exists(gitDirectory), "no .git directory is created on the host");
        AssertEx.Equal("safe\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "safe.txt")), "the block's other side is untouched too");
    }

    /// <summary>
    ///     Git C-quotes a path whenever the name holds a quote, a backslash or a control character, and this parser
    ///     does not unescape them. It refuses by name rather than as an unregistered folder, pinning unescaping
    ///     as a deliberate change later.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WithAQuotedPath_RejectsSayingWhy()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // The shape git produces for a name containing a literal quote: the whole path is C-quoted and escaped.
        var patch =
            "diff --git \"a/repo-01/od\\\"d.txt\" \"b/repo-01/od\\\"d.txt\"\n" +
            "new file mode 100644\n" +
            "index 0000000..e69de29\n" +
            "--- /dev/null\n" +
            "+++ \"b/repo-01/od\\\"d.txt\"\n" +
            "@@ -0,0 +1 @@\n" +
            "+text\n";
        await WritePatchAsync(harness, "run-quoted", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted"
        });

        AssertEx.False(preview.CanApply, "a quoted path is refused rather than parsed past");
        AssertEx.Contains(preview.Rejections, reason => reason.Contains("quoted path", StringComparison.Ordinal));
        AssertEx.True(preview.Rejections.All(reason => !reason.Contains(hostRoot, StringComparison.Ordinal)),
            "the rejection carries no host path");
    }

    /// <summary>
    ///     A gitlink (mode <c>160000</c>) is a submodule pointer, not a file: <c>git apply</c> without
    ///     <c>--index</c> answers one with an EMPTY DIRECTORY the preview's file list could not have described,
    ///     so it is refused by name.
    /// </summary>
    [Test]
    [Arguments("added", "diff --git a/repo-01/vendor b/repo-01/vendor\nnew file mode 160000\nindex 0000000..1111111\n--- /dev/null\n+++ b/repo-01/vendor\n@@ -0,0 +1 @@\n+Subproject commit 1111111111111111111111111111111111111111\n")]
    [Arguments("removed", "diff --git a/repo-01/vendor b/repo-01/vendor\ndeleted file mode 160000\nindex 1111111..0000000\n--- a/repo-01/vendor\n+++ /dev/null\n@@ -1 +0,0 @@\n-Subproject commit 1111111111111111111111111111111111111111\n")]
    [Arguments("mode-only", "diff --git a/repo-01/vendor b/repo-01/vendor\nold mode 100644\nnew mode 160000\n")]
    // The everyday one: an existing submodule bumped to a new commit. No mode line at all — the mode is stated on
    // the index header because it did not change — so this used to read as an ordinary modified file in the preview.
    [Arguments("pointer bump",
        "diff --git a/repo-01/vendor b/repo-01/vendor\nindex 1111111..2222222 160000\n--- a/repo-01/vendor\n+++ b/repo-01/vendor\n@@ -1 +1 @@\n-Subproject commit 1111111111111111111111111111111111111111\n+Subproject commit 2222222222222222222222222222222222222222\n")]
    public async Task PreviewAndApply_WithAGitlinkBlock_RejectWithoutTouchingHost(string shape, string patch)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        await WritePatchAsync(harness, "run-gitlink", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-gitlink"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-gitlink"
        });

        AssertEx.False(preview.CanApply, $"a {shape} submodule reference is refused");
        AssertEx.Contains(preview.Rejections, reason => reason.Contains("submodule", StringComparison.Ordinal));
        AssertEx.False(result.Applied);
        AssertEx.False(Directory.Exists(Path.Combine(hostRoot, "vendor")), "no empty submodule directory is created on the host");
    }

    /// <summary>
    ///     A symlink block (mode <c>120000</c>) carries the link TARGET as its content, so applying one makes git
    ///     create a real link pointing wherever that names — a <c>../</c> climb the within-root guard never sees,
    ///     because it validates the link's own path.
    /// </summary>
    [Test]
    [Arguments("created", "diff --git a/repo-01/evil b/repo-01/evil\nnew file mode 120000\nindex 0000000..1111111\n--- /dev/null\n+++ b/repo-01/evil\n@@ -0,0 +1 @@\n+/etc/passwd\n\\ No newline at end of file\n")]
    [Arguments("deleted", "diff --git a/repo-01/evil b/repo-01/evil\ndeleted file mode 120000\nindex 1111111..0000000\n--- a/repo-01/evil\n+++ /dev/null\n@@ -1 +0,0 @@\n-/etc/passwd\n")]
    // A regular file turned into a link, and the reverse: git states both as a mode pair, with no `new file` line.
    [Arguments("file-to-link", "diff --git a/repo-01/notes.txt b/repo-01/notes.txt\nold mode 100644\nnew mode 120000\n")]
    [Arguments("link-to-file", "diff --git a/repo-01/notes.txt b/repo-01/notes.txt\nold mode 120000\nnew mode 100644\n")]
    // Retargeting an existing link: the mode did not change, so it is stated on the index header and nowhere else.
    [Arguments("retargeted",
        "diff --git a/repo-01/evil b/repo-01/evil\nindex 1111111..2222222 120000\n--- a/repo-01/evil\n+++ b/repo-01/evil\n@@ -1 +1 @@\n-../inside\n+/etc/shadow\n")]
    public async Task PreviewAndApply_WithASymlinkBlock_RejectWithoutTouchingHost(string shape, string patch)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostAsync(hostRoot, ("notes.txt", "notes\n"));

        await WritePatchAsync(harness, "run-symlink-mode", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-symlink-mode"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-symlink-mode"
        });

        AssertEx.False(preview.CanApply, $"a {shape} symbolic link is refused");
        AssertEx.Contains(preview.Rejections, reason => reason.Contains("symbolic link", StringComparison.Ordinal));
        AssertEx.True(preview.Rejections.All(reason => !reason.Contains(hostRoot, StringComparison.Ordinal)),
            "the rejection carries no host path");
        AssertEx.False(result.Applied);
        AssertEx.False(Path.Exists(Path.Combine(hostRoot, "evil")), "no link is created on the host");
        AssertEx.Equal("notes\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "notes.txt")), "the mode-pair shapes leave the real file alone");
    }

    /// <summary>
    ///     The false-positive control for the mode guards: an ordinary <c>100644</c> file whose CONTENT is the text
    ///     of a mode line still applies. Every hunk line carries a sigil, which is what makes whole-line matching
    ///     safe.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WithAFileWhoseContentLooksLikeAModeLine_IsNotRefused()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        const string Body = "new file mode 120000\nindex 1111111..2222222 160000\n";
        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("docs/patch-format.md", "placeholder\n", Body));

        await WritePatchAsync(harness, "run-lookalike", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-lookalike"
        });

        AssertEx.True(result.Applied, $"a file that merely describes a mode line applies. rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Equal(Body, await File.ReadAllTextAsync(Path.Combine(hostRoot, "docs", "patch-format.md")));
    }

    /// <summary>
    ///     Names that merely START with <c>.git</c> are ordinary tracked files and must keep applying. The driver
    ///     definitions an in-tree <c>.gitattributes</c> names are closed by the hardened environment instead.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WithGitattributesOrGitignore_IsNotRefused()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01",
            hostRoot,
            (".gitattributes", "* text=auto\n", "* text=auto\n*.bin binary\n"),
            (".gitignore", "bin/\n", "bin/\nobj/\n"));
        await WritePatchAsync(harness, "run-dotgit-files", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-dotgit-files"
        });

        AssertEx.True(result.Applied, $"a .gitattributes / .gitignore change is ordinary. rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Contains(await File.ReadAllTextAsync(Path.Combine(hostRoot, ".gitattributes")), "*.bin binary");
    }

    /// <summary>
    ///     A selected folder need not be a git repository: <c>git apply</c> works against a bare working tree, and
    ///     the operator surface makes a folder that is not a checkout the ordinary case rather than a corner.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WhenTheHostFolderIsNotAGitRepository_StillApplies()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // GenerateGPatchAsync builds its baseline in a SEPARATE temp repo and only seeds the pre-image here, so this
        // host folder has no .git of its own. Asserted rather than assumed, because the whole point is the absence.
        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        AssertEx.False(Directory.Exists(Path.Combine(hostRoot, ".git")), "the host folder must not be a repository for this test to prove anything");
        await WritePatchAsync(harness, "run-norepo", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-norepo"
        });

        AssertEx.True(result.Applied, $"a non-repository folder is a valid apply target. rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Equal("alpha\nbravo\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")));
    }

    /// <summary>
    ///     Two applies never overlap: <c>git apply</c> is not transactional, so a second could write into a tree
    ///     the first's <c>--check</c> cleared. The gate must outlive the SCOPED service; the resolver stub records
    ///     the peak concurrency.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WhenTwoApplesRunAtOnce_AreSerialized()
    {
        var harness = NewHarness();
        var hostRootA = harness.AddFolder("repo-01");
        var hostRootB = harness.AddFolder("repo-02");

        await WritePatchAsync(harness, "run-par-a", await GenerateGPatchAsync("repo-01", hostRootA, ("a.txt", "a\n", "a\nchanged\n")));
        await WritePatchAsync(harness, "run-par-b", await GenerateGPatchAsync("repo-02", hostRootB, ("b.txt", "b\n", "b\nchanged\n")));

        harness.Resolver.BlockUntilReleased();

        // Two SEPARATE service instances over one state, which is what two concurrent scoped requests are: a gate
        // held in an instance field would pass with one instance and fail here.
        var first = harness.NewService().ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-par-a"
        });
        var second = harness.NewService().ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-par-b"
        });

        await AssertEx.EventuallyAsync(() => harness.Resolver.EnteredCount >= 1,
            TimeSpan.FromSeconds(10),
            "one apply must reach the resolver; neither did, so nothing below proves anything.");

        // real-timer: the property is the ABSENCE of an overlap, so the second apply needs a real window in which
        // it WOULD have entered; a gate the test controls cannot grant one, since never entering is the claim.
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        harness.Resolver.Release();
        var results = await Task.WhenAll(first, second);

        AssertEx.Equal(expected: 1, harness.Resolver.MaxConcurrent,
            "two applies were inside the resolver at once, so the node-wide apply gate did not hold them apart.");
        AssertEx.Equal(expected: 2, harness.Resolver.EnteredCount, "both applies must have run; a serialized pair is not a dropped one.");
        AssertEx.True(results.All(result => result.Applied), $"both applies land. rejections: {string.Join(separator: ';', results.SelectMany(result => result.Rejections))}");
        AssertEx.Equal("a\nchanged\n", await File.ReadAllTextAsync(Path.Combine(hostRootA, "a.txt")));
        AssertEx.Equal("b\nchanged\n", await File.ReadAllTextAsync(Path.Combine(hostRootB, "b.txt")));
    }

    private TestHarness NewHarness(bool allowBinary = false)
    {
        var agentHomeStateRoot = NewTempDir();
        var agentHomeRoot = Path.Combine(agentHomeStateRoot, "agent-home");
        Directory.CreateDirectory(agentHomeRoot);

        var resolver = new FakeResolver();
        var options = Options.Create(new AgentHomeOptions
        {
            RootPath = agentHomeStateRoot,
            AllowBinaryPatchApply = allowBinary,
            PatchApplyTimeoutSeconds = 120
        });
        var runtimeSettings = StubNodeRuntimeSettings.Create().Build();
        var scopeFactory = new ServiceCollection()
                           .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(TimeProvider.System))
                           .BuildServiceProvider();

        NodePatchApplyService NewService() =>
            new(resolver,
                options,
                runtimeSettings,
                new FakeNodeDataDirectory(agentHomeStateRoot),
                new StubIdentityProvider(),
                scopeFactory.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<NodePatchApplyService>.Instance);

        return new TestHarness(NewService, resolver, agentHomeRoot, () => NewTempDir());
    }


    /// <summary>
    ///     Generates a patch-export-shaped patch for a single alias by building a temp <c>selected/&lt;alias&gt;/</c> repo
    ///     seeded with each file's BEFORE content, committing the baseline, applying each AFTER edit (null = delete),
    ///     then running the patch-export exact diff. The same BEFORE content is also seeded into <paramref name="hostRoot" /> so the
    ///     patch applies cleanly on the host.
    /// </summary>
    private async Task<string> GenerateGPatchAsync(string alias, string hostRoot, params (string Relative, string Before, string? After)[] files)
    {
        var selected = NewTempDir();
        var aliasDir = Path.Combine(selected, alias);
        foreach (var (relative, before, _) in files)
        {
            await WriteTextAsync(Path.Combine(aliasDir, relative), before);
            await WriteTextAsync(Path.Combine(hostRoot, relative), before);
        }

        await InitBaselineAsync(selected);

        foreach (var (relative, _, after) in files)
        {
            var fullPath = Path.Combine(aliasDir, relative);
            if (after is null)
            {
                File.Delete(fullPath);
            }
            else
            {
                await WriteTextAsync(fullPath, after);
            }
        }

        return await DiffAsync(selected);
    }

    private async Task<string> GenerateMultiAliasPatchAsync(string hostRoot01,
        string hostRoot02,
        (string Alias, string Relative, string Before, string After) file01,
        (string Alias, string Relative, string Before, string After) file02)
    {
        var selected = NewTempDir();
        await WriteTextAsync(Path.Combine(selected, file01.Alias, file01.Relative), file01.Before);
        await WriteTextAsync(Path.Combine(selected, file02.Alias, file02.Relative), file02.Before);
        await WriteTextAsync(Path.Combine(hostRoot01, file01.Relative), file01.Before);
        await WriteTextAsync(Path.Combine(hostRoot02, file02.Relative), file02.Before);

        await InitBaselineAsync(selected);

        await WriteTextAsync(Path.Combine(selected, file01.Alias, file01.Relative), file01.After);
        await WriteTextAsync(Path.Combine(selected, file02.Alias, file02.Relative), file02.After);

        return await DiffAsync(selected);
    }

    private async Task<string> GenerateBinaryPatchAsync(string alias, string relative, byte[] before, byte[] after)
    {
        var selected = NewTempDir();
        var fullPath = Path.Combine(selected, alias, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, before);

        await InitBaselineAsync(selected);

        await File.WriteAllBytesAsync(fullPath, after);

        return await DiffAsync(selected);
    }

    private static async Task<string> DiffAsync(string repoRoot)
    {
        var (exitCode, standardOutput, standardError) = await GitAsync(repoRoot, PatchDiffArgs);
        AssertEx.Equal(expected: 0, exitCode, $"git diff failed: {standardError}");
        return standardOutput;
    }

    private static async Task InitBaselineAsync(string repoRoot)
    {
        await GitOkAsync(repoRoot, "init");
        await GitOkAsync(repoRoot, "config", "core.autocrlf", "false");
        await GitOkAsync(repoRoot, "config", "core.filemode", "false");
        await GitOkAsync(repoRoot, "add", "-A");
        await GitOkAsync(repoRoot,
            "-c", "user.email=agent-home@localhost",
            "-c", "user.name=AgentHome",
            "commit", "-m", "baseline", "--allow-empty");
    }

    private static async Task SeedHostAsync(string hostRoot, params (string Relative, string Content)[] files)
    {
        foreach (var (relative, content) in files)
        {
            await WriteTextAsync(Path.Combine(hostRoot, relative), content);
        }
    }

    private static async Task SeedHostBinaryAsync(string hostRoot, string relative, byte[] content)
    {
        var fullPath = Path.Combine(hostRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content);
    }

    private static async Task WriteTextAsync(string fullPath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    private static async Task WritePatchAsync(TestHarness harness, string runId, string patchText)
    {
        var patchesDir = Path.Combine(harness.AgentHomeRoot, "runs", runId, "patches");
        Directory.CreateDirectory(patchesDir);
        await File.WriteAllTextAsync(Path.Combine(patchesDir, "changes.patch"), patchText);
    }

    private static async Task GitOkAsync(string repoRoot, params string[] args)
    {
        var (exitCode, _, standardError) = await GitAsync(repoRoot, args);
        AssertEx.Equal(expected: 0, exitCode, $"git {string.Join(separator: ' ', args)} failed: {standardError}");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> GitAsync(string repoRoot, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agenthome-l-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class TestHarness
    {
        private readonly Func<string> _newFolder;
        private readonly Func<NodePatchApplyService> _newService;
        private readonly FakeResolver _resolver;

        public TestHarness(Func<NodePatchApplyService> newService, FakeResolver resolver, string agentHomeRoot, Func<string> newFolder)
        {
            _newService = newService;
            Service = newService();
            _resolver = resolver;
            AgentHomeRoot = agentHomeRoot;
            _newFolder = newFolder;
        }

        /// <summary>One shared instance, for the tests that need only one.</summary>
        public NodePatchApplyService Service { get; }

        /// <summary>A FRESH instance over the same state — what a second scoped request resolves.</summary>
        public NodePatchApplyService NewService() =>
            _newService();

        public string AgentHomeRoot { get; }

        public FakeResolver Resolver => _resolver;

        public string AddFolder(string alias)
        {
            var hostRoot = _newFolder();
            _resolver.Add(Guid.NewGuid(), alias, hostRoot);
            return hostRoot;
        }
    }

    /// <summary>
    ///     The alias → host-root map the service resolves through, plus the instrumentation the serialization test
    ///     needs. Every apply passes through <see cref="ListReferencesAsync" /> while holding the node-wide apply
    ///     gate, which makes it the place to watch two of them from.
    /// </summary>
    private sealed class FakeResolver : ISelectedFolderResolver
    {
        private readonly Dictionary<Guid, ResolvedSelectedFolder> _folders = [];
        private readonly Lock _counters = new();
        private TaskCompletionSource? _release;
        private int _concurrent;
        private int _entered;
        private int _maxConcurrent;

        public int EnteredCount
        {
            get
            {
                lock (_counters)
                {
                    return _entered;
                }
            }
        }

        public int MaxConcurrent
        {
            get
            {
                lock (_counters)
                {
                    return _maxConcurrent;
                }
            }
        }

        /// <summary>Makes every subsequent resolve wait inside the gate until <see cref="Release" />.</summary>
        public void BlockUntilReleased()
        {
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release()
        {
            _ = _release?.TrySetResult();
        }

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
        {
            lock (_counters)
            {
                _entered++;
                _concurrent++;
                _maxConcurrent = Math.Max(_maxConcurrent, _concurrent);
            }

            try
            {
                if (_release is { } release)
                {
                    await release.Task.WaitAsync(cancellationToken);
                }

                IReadOnlyList<SelectedFolderReference> references =
                    _folders.Values.Select(folder => new SelectedFolderReference { Id = folder.Id.ToString(), Alias = folder.Alias }).ToList();
                return references;
            }
            finally
            {
                lock (_counters)
                {
                    _concurrent--;
                }
            }
        }

        public Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default)
        {
            if (Guid.TryParse(id, out var parsed) && _folders.TryGetValue(parsed, out var folder))
            {
                return Task.FromResult(folder);
            }

            throw new SelectedFolderValidationException($"Unknown selected folder id '{id}'.");
        }

        public void Add(Guid id, string alias, string hostPath)
        {
            _folders[id] = new ResolvedSelectedFolder { Id = id, Alias = alias, HostPath = hostPath, Mode = SelectedFolderMode.Copy };
        }
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity { OwnerUserId = "owner-a", NodeId = "node-1" });
        }
    }
}
