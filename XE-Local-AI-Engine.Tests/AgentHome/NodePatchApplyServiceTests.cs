namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Host patch apply coverage. Uses REAL host <c>git</c> (no Docker, no Ollama) to
///     generate patch-export-shaped patches (rooted at a temp <c>selected/&lt;alias&gt;/</c> repo, with the exact diff
///     flags) and applies them through <see cref="NodePatchApplyService" /> to temp host folders mapped by a fake
///     <see cref="ISelectedFolderResolver" />. Proves the traversal/alias guards, binary-reject default, the gate on
///     <c>changes.patch</c> presence (never <c>changed-files.json</c>), folder-relative logging, and host-path redaction.
///     The git baseline and the host folder are seeded with the SAME pre-image so a generated patch applies cleanly.
/// </summary>
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
                    Directory.Delete(dir, true);
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

        AssertEx.True(result.Applied, $"a clean patch applies. rejections: {string.Join(';', result.Rejections)}");
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

        AssertEx.True(preview.CanApply, $"the patch checks clean. rejections: {string.Join(';', preview.Rejections)}");
        AssertEx.Equal(before, await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")), "preview must not mutate the host");
        var entry = preview.Files.Single(file => file.RelativePath == "src/App.cs");
        AssertEx.Equal(1, entry.Added);
        AssertEx.Equal(0, entry.Removed);
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

        AssertEx.True(result.Applied, $"rejections: {string.Join(';', result.Rejections)}");
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

        AssertEx.True(result.Applied, $"rejections: {string.Join(';', result.Rejections)}");
        AssertEx.Equal("one\nupdated\n", await File.ReadAllTextAsync(Path.Combine(root01, "one.txt")));
        AssertEx.Equal("two\nupdated\n", await File.ReadAllTextAsync(Path.Combine(root02, "two.txt")));
        AssertEx.False(File.Exists(Path.Combine(root01, "two.txt")), "no cross-contamination between alias roots");
    }

    [Test]
    public async Task ApplyApprovedAsync_WithBinaryBlock_RejectsByDefaultButAppliesWhenAllowed()
    {
        var rejectingHarness = NewHarness(false);
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
        AssertEx.True(allowedResult.Applied, $"a binary patch applies when allowed. rejections: {string.Join(';', allowedResult.Rejections)}");
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
        AssertEx.True(result.Applied, $"rejections: {string.Join(';', result.Rejections)}");

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
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // Create a subdirectory that is a symlink pointing outside the root.
        var outside = NewTempDir();
        var symlinkDir = Path.Combine(hostRoot, "subdir");
        try
        {
            Directory.CreateSymbolicLink(symlinkDir, outside);
        }
        catch (IOException)
        {
            // Symlink creation not supported in this environment; skip gracefully.
            return;
        }

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
            MaxPatchBytes = tinyBudget,
            PatchApplyTimeoutSeconds = 120
        });
        var scopeFactory = new ServiceCollection()
                           .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(TimeProvider.System))
                           .BuildServiceProvider();
        var service = new NodePatchApplyService(resolver,
            options,
            new FakeNodeDataDirectory(agentHomeStateRoot),
            new StubIdentityProvider(),
            scopeFactory.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodePatchApplyService>.Instance);

        var patchesDir = Path.Combine(agentHomeRoot, "runs", "run-big", "patches");
        Directory.CreateDirectory(patchesDir);
        await File.WriteAllTextAsync(Path.Combine(patchesDir, "changes.patch"), new string('x', tinyBudget + 1));

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

        AssertEx.True(result.Applied, $"a file under 'dir b/' must not be falsely rejected. rejections: {string.Join(';', result.Rejections)}");
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
        var scopeFactory = new ServiceCollection()
                           .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(TimeProvider.System))
                           .BuildServiceProvider();

        var service = new NodePatchApplyService(resolver,
            options,
            new FakeNodeDataDirectory(agentHomeStateRoot),
            new StubIdentityProvider(),
            scopeFactory.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodePatchApplyService>.Instance);

        return new TestHarness(service, resolver, agentHomeRoot, () => NewTempDir());
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
        AssertEx.Equal(0, exitCode, $"git diff failed: {standardError}");
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
        AssertEx.Equal(0, exitCode, $"git {string.Join(' ', args)} failed: {standardError}");
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
        private readonly FakeResolver _resolver;

        public TestHarness(NodePatchApplyService service, FakeResolver resolver, string agentHomeRoot, Func<string> newFolder)
        {
            Service = service;
            _resolver = resolver;
            AgentHomeRoot = agentHomeRoot;
            _newFolder = newFolder;
        }

        public NodePatchApplyService Service { get; }

        public string AgentHomeRoot { get; }

        public string AddFolder(string alias)
        {
            var hostRoot = _newFolder();
            _resolver.Add(Guid.NewGuid(), alias, hostRoot);
            return hostRoot;
        }
    }

    private sealed class FakeResolver : ISelectedFolderResolver
    {
        private readonly Dictionary<Guid, ResolvedSelectedFolder> _folders = [];

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SelectedFolderReference> references =
                _folders.Values.Select(folder => new SelectedFolderReference(folder.Id.ToString(), folder.Alias)).ToList();
            return Task.FromResult(references);
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
            _folders[id] = new ResolvedSelectedFolder(id, alias, hostPath, SelectedFolderMode.Copy);
        }
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity("owner-a", "node-1"));
        }
    }

}
