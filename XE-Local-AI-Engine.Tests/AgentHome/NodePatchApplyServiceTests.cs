namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Diagnostics;
using System.Security.Cryptography;
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

    /// <summary>
    ///     A run id in the <c>run-{unixMs}-{counter}</c> shape the node mints. The apply accepts any identifier-shaped
    ///     name, but the delete resolves only a minted one, so a test that exercises both needs this spelling.
    /// </summary>
    private const string MintedRunId = "run-1758400000000-7";

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

        AssertEx.True(result.Applied, $"a clean patch applies. rejections: {Describe(result.Rejections)}");
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

        AssertEx.True(preview.CanApply, $"the patch checks clean. rejections: {Describe(preview.Rejections)}");
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

        AssertEx.True(result.Applied, $"rejections: {Describe(result.Rejections)}");
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
        AssertEx.Contains(result.Rejections, rejection => rejection.Reason.Contains("repo-99", StringComparison.Ordinal) && rejection.Reason.Contains("not a registered", StringComparison.Ordinal));
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
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("across selected folders", StringComparison.Ordinal));
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

        AssertEx.True(result.Applied, $"rejections: {Describe(result.Rejections)}");
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
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("binary", StringComparison.Ordinal));

        // With the option on, the same patch is no longer rejected for the binary reason and applies.
        var allowed = NewHarness(true);
        var allowedRoot = allowed.AddFolder("repo-01");
        await SeedHostBinaryAsync(allowedRoot, "blob.bin", [0x00, 0x01, 0x02, 0x03]);
        await WritePatchAsync(allowed, "run-binary", patch);

        var allowedResult = await allowed.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-binary"
        });
        AssertEx.True(allowedResult.Applied, $"a binary patch applies when allowed. rejections: {Describe(allowedResult.Rejections)}");
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
        AssertEx.True(result.Applied, $"rejections: {Describe(result.Rejections)}");

        var eventsPath = Path.Combine(harness.AgentHomeRoot, "runs", "run-log", "logs", "events.jsonl");
        AssertEx.True(File.Exists(eventsPath), "the run events log exists");
        var events = await File.ReadAllTextAsync(eventsPath);
        AssertEx.Contains(events, "patch_applied");
        AssertEx.Contains(events, "repo-01/src/App.cs");
        AssertEx.False(events.Contains(hostRoot, StringComparison.Ordinal), "the log must not leak a host path");
    }

    /// <summary>
    ///     The delete-versus-apply race. A mid-flight apply has read its patch bytes already, so removing the run
    ///     cannot corrupt the host — but the run-log append writes NOTHING once the directory is gone.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WhileItRuns_RefusesADeleteOfTheSameRunAndStillRecordsTheOutcome()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        var runDirectory = SeedRunDirectory(harness, MintedRunId);

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, MintedRunId, patch);

        // The apply parks inside the section it guards: the resolver is reached from BuildPlanAsync, after the guard
        // is taken and well before the run-log append a delete would swallow. A gate the test releases, never a sleep.
        harness.Resolver.BlockUntilReleased();
        var apply = harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = MintedRunId
        });
        await AssertEx.EventuallyAsync(() => harness.Resolver.EnteredCount > 0,
            TestBudgets.Contended,
            "the apply never reached the resolver, so there was no in-flight apply for the delete to race.");

        var refused = await DeleteService(harness).DeleteAsync(MintedRunId);

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Conflict, refused,
            "a run whose patch is being applied right now must refuse the delete, not race it.");
        AssertEx.True(Directory.Exists(runDirectory), "the refused delete must leave the run standing.");

        harness.Resolver.Release();
        var result = await apply;

        AssertEx.True(result.Applied, $"rejections: {Describe(result.Rejections)}");
        var events = await File.ReadAllTextAsync(Path.Combine(runDirectory, "logs", "events.jsonl"));
        AssertEx.Contains(events, "patch_applied");

        // And the guard is released on the way out: the same delete that was refused now succeeds.
        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, await DeleteService(harness).DeleteAsync(MintedRunId),
            "a finished apply must leave the run deletable, or the guard has become a leak.");
    }

    /// <summary>
    ///     The other direction: a delete holds the guard across its removal, so an apply arriving mid-delete waits
    ///     and finds an empty run. It must answer the refusal it already has for a run with nothing exported.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_OnARunThatWasJustDeleted_AnswersTheMissingPatchRefusal()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        SeedRunDirectory(harness, MintedRunId);

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, MintedRunId, patch);
        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, await DeleteService(harness).DeleteAsync(MintedRunId));

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = MintedRunId
        });

        AssertEx.False(result.Applied);
        AssertEx.True(result.PatchMissing,
            "a run whose directory is gone has no exported patch, which is an answer the contract already carries.");
        AssertEx.Contains(result.Rejections,
            rejection => rejection.Reason.Contains("no exported patch is available", StringComparison.Ordinal));
        AssertEx.Equal("alpha\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "src", "App.cs")),
            "a refused apply writes nothing to the host.");
    }

    /// <summary>
    ///     An apply that refuses still held the guard, so it still has to hand it back. The exit paths are asserted
    ///     one by one because each returns through a different statement.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_AfterARefusedApply_LeavesTheRunDeletable()
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");
        SeedRunDirectory(harness, MintedRunId);

        await WritePatchAsync(harness,
            MintedRunId,
            "diff --git a/repo-01/../escape.txt b/repo-01/../escape.txt\n"
            + "new file mode 100644\n"
            + "--- /dev/null\n"
            + "+++ b/repo-01/../escape.txt\n"
            + "@@ -0,0 +1 @@\n"
            + "+pwned\n");

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = MintedRunId
        });

        AssertEx.False(result.Applied, "a traversing patch is refused, which is the exit path under test.");
        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, await DeleteService(harness).DeleteAsync(MintedRunId));
    }

    [Test]
    public async Task ApplyApprovedAsync_AfterItThrows_LeavesTheRunDeletable()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        SeedRunDirectory(harness, MintedRunId);

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, MintedRunId, patch);
        harness.Resolver.ThrowOnList = new InvalidOperationException("the folder resolver fell over mid-apply.");

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = MintedRunId
        }));

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, await DeleteService(harness).DeleteAsync(MintedRunId),
            "an apply that failed in a way it never planned for must not lock the run forever.");
    }

    [Test]
    public async Task ApplyApprovedAsync_AfterCancellation_LeavesTheRunDeletable()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        SeedRunDirectory(harness, MintedRunId);

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, MintedRunId, patch);

        harness.Resolver.BlockUntilReleased();
        using var cancellation = new CancellationTokenSource();
        var apply = harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
            {
                RunId = MintedRunId
            },
            cancellation.Token);
        await AssertEx.EventuallyAsync(() => harness.Resolver.EnteredCount > 0,
            TestBudgets.Contended,
            "the apply never reached the point where it can be cancelled in flight.");

        await cancellation.CancelAsync();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => apply);

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, await DeleteService(harness).DeleteAsync(MintedRunId),
            "a cancelled apply must hand the guard back like any other exit.");
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
        AssertEx.True(preview.Rejections.All(rejection => !rejection.Reason.Contains(hostRoot, StringComparison.Ordinal)),
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
        AssertEx.Contains(result.Rejections, rejection => rejection.Reason.Contains("symlink", StringComparison.Ordinal));
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
            new AgentHomeRunApplyGuard(),
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
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("maximum allowed size", StringComparison.Ordinal));
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

        AssertEx.True(result.Applied, $"a file under 'dir b/' must not be falsely rejected. rejections: {Describe(result.Rejections)}");
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

        AssertEx.False(preview.Rejections.Any(rejection =>
                rejection.Reason.Contains("traversal", StringComparison.OrdinalIgnoreCase)
                || rejection.Reason.Contains("outside its folder", StringComparison.OrdinalIgnoreCase)
                || rejection.Reason.Contains("no alias", StringComparison.OrdinalIgnoreCase)
                || rejection.Reason.Contains("unparseable", StringComparison.OrdinalIgnoreCase)),
            "a clean mode-only block must not be rejected for path-guard reasons");
    }

    /// <summary>
    ///     A header holding a literal <c>" b/"</c> was cut at the first one, guarding <c>x</c> while git wrote
    ///     <c>x b/sneaky/evil.txt</c>. REAL git writes both patches, so the header shape is git's own.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WithAHeaderOnlyBlockUnderASpaceBSlashDirectory_ChangesExactlyThatPath()
    {
        var harness = NewHarness();
        var addRoot = harness.AddFolder("repo-01");

        var addPatch = await GenerateAddPatchAsync("repo-01", NewTempDir(), ("x b/sneaky/evil.txt", string.Empty));
        AssertEx.Contains(addPatch, "diff --git a/repo-01/x b/sneaky/evil.txt b/repo-01/x b/sneaky/evil.txt\n",
            message: "the fixture must carry git's own mis-splittable header");
        AssertEx.False(addPatch.Contains("+++ ", StringComparison.Ordinal),
            $"an empty added file is the header-only shape this test is about: {addPatch}");
        await WritePatchAsync(harness, "run-header-add", addPatch);

        var added = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-header-add"
        });

        AssertEx.True(added.Applied, $"a legitimate empty file under 'x b/' applies. rejections: {Describe(added.Rejections)}");
        AssertEx.True(File.Exists(Path.Combine(addRoot, "x b", "sneaky", "evil.txt")), "git writes the whole header path");
        AssertEx.False(File.Exists(Path.Combine(addRoot, "x")), "nothing lands at the prefix the first-\" b/\" split produced");

        // The same shape the other way round: a delete has no body lines either once the file it removes is empty.
        var deleteHarness = NewHarness();
        var deleteRoot = deleteHarness.AddFolder("repo-01");
        var deletePatch = await GenerateGPatchAsync("repo-01", deleteRoot, ("x b/sneaky/gone.txt", string.Empty, null));
        AssertEx.False(deletePatch.Contains("--- ", StringComparison.Ordinal),
            $"an empty deleted file is the header-only shape this test is about: {deletePatch}");
        await WritePatchAsync(deleteHarness, "run-header-delete", deletePatch);

        var deleted = await deleteHarness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-header-delete"
        });

        AssertEx.True(deleted.Applied, $"a legitimate empty delete under 'x b/' applies. rejections: {Describe(deleted.Rejections)}");
        AssertEx.False(File.Exists(Path.Combine(deleteRoot, "x b", "sneaky", "gone.txt")), "git removes the whole header path");
    }

    /// <summary>
    ///     git answers a header that does not spell the same name twice with "lacks filename information", so a
    ///     crafted one that merely LOOKS splittable must name nothing here either, rather than a guess.
    /// </summary>
    [Test]
    [Arguments("an added empty file",
        "diff --git a/repo-01/x b/sneaky/evil.txt b/repo-01/other b/sneaky/evil.txt\nnew file mode 100644\nindex 0000000..e69de29\n")]
    [Arguments("a mode change",
        "diff --git a/repo-01/x b/sneaky/run.sh b/repo-01/x b/other/run.sh\nold mode 100644\nnew mode 100755\n")]
    public async Task PreviewAndApply_WithAnAsymmetricHeaderOnlyBlock_RefuseAndWriteNothing(string shape, string patch)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await WritePatchAsync(harness, "run-header-asymmetric", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-header-asymmetric"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-header-asymmetric"
        });

        AssertEx.False(preview.CanApply, shape);
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("unparseable", StringComparison.Ordinal),
            $"{shape}: {Describe(preview.Rejections)}");
        AssertEx.False(result.Applied);
        AssertEx.Empty(Directory.GetFileSystemEntries(hostRoot, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    ///     The escape the truncated path hid: reading only <c>x</c>, the reparse-point walk never saw the <c>x b</c>
    ///     the patch traverses — the one guard git's own <c>..</c> and <c>.git</c> refusals do not duplicate.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WithASymlinkedIntermediateInsideASpaceBSlashHeaderPath_Rejects()
    {
        SymlinkSupport.EnsureSupported();

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        var outside = NewTempDir();
        Directory.CreateSymbolicLink(Path.Combine(hostRoot, "x b"), outside);

        const string Patch = "diff --git a/repo-01/x b/evil.txt b/repo-01/x b/evil.txt\n"
                             + "new file mode 100644\nindex 0000000..e69de29\n";
        await WritePatchAsync(harness, "run-header-symlink", Patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-header-symlink"
        });

        AssertEx.False(result.Applied, "a symlinked intermediate reached only through the header path tail is rejected");
        AssertEx.Contains(result.Rejections, rejection => rejection.Reason.Contains("symlink", StringComparison.Ordinal),
            Describe(result.Rejections));
        AssertEx.False(File.Exists(Path.Combine(outside, "evil.txt")), "nothing is written outside through the symlink");
    }

    /// <summary>
    ///     A character this host cannot write arrives UNQUOTED, because git quotes for none of them, so gating the
    ///     refusal on the quoting let exactly those through.
    /// </summary>
    /// <remarks>
    ///     The character is read from the host's own rule rather than hard-coded: <c>:</c> and <c>*</c> are invalid
    ///     on Windows and ordinary Linux file names, which must stay applyable, and the NUL byte is refused by both.
    /// </remarks>
    [Test]
    public async Task PreviewAndApply_WithAnUnquotedNameTheHostCannotWrite_RefuseAndNameIt()
    {
        var forbidden = Array.FindAll(Path.GetInvalidFileNameChars(), character => character is not ('/' or '\n' or '\r'));
        AssertEx.True(forbidden.Length > 0, "every host forbids at least one character in a file name");

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        var name = $"ev{forbidden[0]}il.txt";
        var patch = $"diff --git a/repo-01/{name} b/repo-01/{name}\nindex 0000001..0000002 100644\n"
                    + $"--- a/repo-01/{name}\n+++ b/repo-01/{name}\n@@ -1 +1 @@\n-old\n+new\n";
        await WritePatchAsync(harness, "run-unwritable-unquoted", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-unwritable-unquoted"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-unwritable-unquoted"
        });

        AssertEx.False(preview.CanApply, "an unquoted name this host cannot write is refused");
        AssertEx.Contains(preview.Rejections,
            rejection => rejection.Reason.Contains("will not write", StringComparison.Ordinal)
                         && rejection.Path?.StartsWith("repo-01/", StringComparison.Ordinal) == true,
            $"the refusal names the entry: {Describe(preview.Rejections)}");
        AssertEx.False(result.Applied);
        AssertEx.Empty(Directory.GetFileSystemEntries(hostRoot, "*", SearchOption.AllDirectories));
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
            AssertEx.True(result.Applied, $"rejections: {Describe(result.Rejections)}");
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
        AssertEx.True(preview.CanApply, $"rejections: {Describe(preview.Rejections)}");
        AssertEx.NotNull(preview.PatchSha256, "a preview that read a patch reports its hash");
        AssertEx.Equal(expected: 64, preview.PatchSha256!.Length, "SHA-256 renders as 64 lowercase hex characters");

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-bound",
            ExpectedPatchSha256 = preview.PatchSha256
        });

        AssertEx.True(result.Applied, $"the hash the preview reported is the hash the apply accepts. rejections: {Describe(result.Rejections)}");
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
        AssertEx.True(preview.CanApply, $"rejections: {Describe(preview.Rejections)}");

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
        AssertEx.Contains(result.Rejections, rejection => rejection.Reason.Contains("changed since it was previewed", StringComparison.Ordinal));
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
    [Arguments("source",
        "diff --git a/repo-01/.git/hooks/pre-commit b/repo-01/.git/hooks/pre-commit\ndeleted file mode 100755\n--- a/repo-01/.git/hooks/pre-commit\n+++ /dev/null\n@@ -1 +0,0 @@\n-#!/bin/sh\n")]
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
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("git directory", StringComparison.Ordinal));
        AssertEx.False(result.Applied);
        AssertEx.False(Directory.Exists(gitDirectory), "no .git directory is created on the host");
        AssertEx.Equal("safe\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "safe.txt")), "the block's other side is untouched too");
    }

    /// <summary>
    ///     Git C-quotes a path whenever the name holds a quote, a backslash or a control byte. The parser decodes
    ///     that literal into the name git itself will write, so such a run is reviewable AND applyable.
    /// </summary>
    /// <remarks>
    ///     Generated by REAL git, because the whole point is that our decoder and git's agree: the file the apply
    ///     leaves on disk is the one the preview named, beside an ordinary file in the same patch.
    /// </remarks>
    [Test]
    public async Task ApplyApprovedAsync_WithQuoteAndBackslashNames_AppliesThemBesideACleanFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("A quote and a backslash are not legal characters in a Windows file name, so the host files cannot be created.");
        }

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01",
            hostRoot,
            ("we\"ird.txt", "alpha\n", "alpha\nbravo\n"),
            ("back\\slash.txt", "charlie\n", "charlie\ndelta\n"),
            ("clean.txt", "echo\n", "echo\nfoxtrot\n"));
        AssertEx.Contains(patch, "\"b/repo-01/we\\\"ird.txt\"", message: "the fixture must be a patch git actually C-quoted");
        await WritePatchAsync(harness, "run-quoted-apply", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-apply"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-apply"
        });

        AssertEx.True(preview.CanApply, $"a decoded quoted path applies. rejections: {Describe(preview.Rejections)}");
        AssertEx.Contains(preview.Files, file => file is { RelativePath: "we\"ird.txt", Added: 1 });
        AssertEx.Contains(preview.Files, file => file is { RelativePath: "back\\slash.txt", Added: 1 });
        AssertEx.True(result.Applied, $"the apply lands. rejections: {Describe(result.Rejections)}");
        AssertEx.Equal("alpha\nbravo\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "we\"ird.txt")));
        AssertEx.Equal("charlie\ndelta\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "back\\slash.txt")));
        AssertEx.Equal("echo\nfoxtrot\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "clean.txt")));
    }

    /// <summary>
    ///     The decode runs BEFORE every guard, so a name that hides a climb, a git directory or a missing alias
    ///     behind octal escapes faces exactly the checks its plain spelling would.
    /// </summary>
    [Test]
    [Arguments("a quoted traversal", "repo-01/../escape.txt", "outside its folder")]
    [Arguments("an octal-escaped traversal", "repo-01/\\056\\056/escape.txt", "outside its folder")]
    [Arguments("an octal-escaped git directory", "repo-01/\\056git/hooks/pre-commit", "git directory")]
    [Arguments("a quoted path with no alias", "escape\\\"d.txt", "no alias segment")]
    public async Task PreviewAndApply_WithADecodedPathThatBreaksAGuard_RefuseAndWriteNothing(string shape, string inner, string reason)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostAsync(hostRoot, ("keep.txt", "keep\n"));

        var patch = $"diff --git \"a/{inner}\" \"b/{inner}\"\nnew file mode 100644\nindex 0000000..e69de29\n"
                    + $"--- /dev/null\n+++ \"b/{inner}\"\n@@ -0,0 +1 @@\n+text\n";
        await WritePatchAsync(harness, "run-decoded-guard", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-decoded-guard"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-decoded-guard"
        });

        AssertEx.False(preview.CanApply, $"{shape} is refused. rejections: {Describe(preview.Rejections)}");
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains(reason, StringComparison.Ordinal), shape);
        AssertEx.False(result.Applied, shape);
        AssertEx.Equal(expected: 1, Directory.GetFileSystemEntries(hostRoot, "*", SearchOption.AllDirectories).Length,
            $"{shape} left the folder holding only its seeded file");
        AssertEx.False(File.Exists(Path.GetFullPath(Path.Combine(hostRoot, "..", "escape.txt"))), $"{shape} wrote nothing beside the folder");
    }

    /// <summary>
    ///     A quoted symlink or submodule block is now named: the same decoder the guards use feeds the refusal,
    ///     so the operator learns WHICH file blocks the patch without any raw literal being echoed.
    /// </summary>
    [Test]
    [Arguments("a symbolic link", "120000", "symbolic link")]
    [Arguments("a submodule reference", "160000", "submodule")]
    public async Task PreviewAndApply_WithAQuotedLinkEntry_RefuseAndNameIt(string shape, string mode, string reason)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = $"diff --git \"a/repo-01/ev\\\"il\" \"b/repo-01/ev\\\"il\"\nnew file mode {mode}\nindex 0000000..1111111\n"
                    + "--- /dev/null\n+++ \"b/repo-01/ev\\\"il\"\n@@ -0,0 +1 @@\n+/etc/passwd\n";
        await WritePatchAsync(harness, "run-quoted-link", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-link"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-link"
        });

        AssertEx.False(preview.CanApply, shape);
        AssertEx.Contains(preview.Rejections,
            rejection => rejection.Path == "repo-01/ev\"il" && rejection.Reason.Contains(reason, StringComparison.Ordinal),
            $"{shape} is named through the decoder: {Describe(preview.Rejections)}");
        AssertEx.False(result.Applied);
        AssertEx.False(Path.Exists(Path.Combine(hostRoot, "ev\"il")), $"{shape} created nothing on the host");
    }

    /// <summary>
    ///     A decoded name holding more than the quote or backslash it was quoted for is still refused — and named,
    ///     with the character itself escaped, so the refusal cannot spoof the file it is about.
    /// </summary>
    [Test]
    [Arguments("a bell", "ev\\007il.txt", "repo-01/ev\\u{0007}il.txt", "\u0007")]
    [Arguments("a right-to-left override", "ev\\342\\200\\256il.txt", "repo-01/ev\\u{202E}il.txt", "‮")]
    [Arguments("a zero-width joiner", "ev\\342\\200\\215il.txt", "repo-01/ev\\u{200D}il.txt", "‍")]
    public async Task PreviewAndApply_WithAControlCharacterInADecodedName_RefuseAndNameItEscaped(string shape,
        string inner,
        string expectedPath,
        string raw)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = $"diff --git \"a/repo-01/{inner}\" \"b/repo-01/{inner}\"\nnew file mode 100644\nindex 0000000..e69de29\n"
                    + $"--- /dev/null\n+++ \"b/repo-01/{inner}\"\n@@ -0,0 +1 @@\n+text\n";
        await WritePatchAsync(harness, "run-decoded-control", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-decoded-control"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-decoded-control"
        });

        AssertEx.False(preview.CanApply, shape);
        AssertEx.Contains(preview.Rejections, rejection => rejection.Path == expectedPath,
            $"{shape} is named escaped: {Describe(preview.Rejections)}");
        AssertEx.True(preview.Rejections.All(rejection => rejection.Path?.Contains(raw, StringComparison.Ordinal) != true
                                                          && !rejection.Reason.Contains(raw, StringComparison.Ordinal)),
            $"the raw {shape} must not survive into the response");
        AssertEx.False(result.Applied);
        AssertEx.Empty(Directory.GetFileSystemEntries(hostRoot, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    ///     A mode-only block carries no body lines, so its quoted HEADER path is the only one there is. Reaching
    ///     these reasons at all proves the header decoded; failing to decode would read as an unparseable header.
    /// </summary>
    [Test]
    [Arguments("an octal-escaped traversal", "repo-01/\\056\\056/escape.txt", "outside its folder")]
    [Arguments("an octal-escaped git directory", "repo-01/\\056git/config", "git directory")]
    [Arguments("a control character", "repo-01/ev\\007il.txt", "will not write")]
    public async Task PreviewAsync_WithAQuotedModeOnlyBlock_RefusesOnTheHeaderPath(string shape, string inner, string reason)
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");

        var patch = $"diff --git \"a/{inner}\" \"b/{inner}\"\nold mode 100644\nnew mode 100755\n";
        await WritePatchAsync(harness, "run-quoted-mode-only", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-mode-only"
        });

        AssertEx.False(preview.CanApply, shape);
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains(reason, StringComparison.Ordinal),
            $"{shape}: {Describe(preview.Rejections)}");
    }

    /// <summary>
    ///     The header's own path is C-quoted on the same rules, so the cross-check that catches a crafted header
    ///     keeps working through the decoder rather than going quiet whenever a name needed quoting.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WithAQuotedHeaderNamingAnotherAlias_Rejects()
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");
        harness.AddFolder("repo-02");

        const string Patch = "diff --git \"a/repo-02/we\\\"ird.txt\" \"b/repo-02/we\\\"ird.txt\"\nindex 0000001..0000002 100644\n"
                             + "--- \"a/repo-01/we\\\"ird.txt\"\n+++ \"b/repo-01/we\\\"ird.txt\"\n@@ -1 +1 @@\n-old\n+new\n";
        await WritePatchAsync(harness, "run-quoted-header", Patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-header"
        });

        AssertEx.False(preview.CanApply, "a header naming another alias than the body is refused");
        AssertEx.Contains(preview.Rejections,
            rejection => rejection.Reason.Contains("header b-path does not match", StringComparison.Ordinal),
            Describe(preview.Rejections));
    }

    /// <summary>
    ///     A gitlink (mode <c>160000</c>) is a submodule pointer, not a file: <c>git apply</c> without
    ///     <c>--index</c> answers one with an EMPTY DIRECTORY the preview's file list could not have described,
    ///     so it is refused by name.
    /// </summary>
    [Test]
    [Arguments("added",
        "diff --git a/repo-01/vendor b/repo-01/vendor\nnew file mode 160000\nindex 0000000..1111111\n--- /dev/null\n+++ b/repo-01/vendor\n@@ -0,0 +1 @@\n+Subproject commit 1111111111111111111111111111111111111111\n")]
    [Arguments("removed",
        "diff --git a/repo-01/vendor b/repo-01/vendor\ndeleted file mode 160000\nindex 1111111..0000000\n--- a/repo-01/vendor\n+++ /dev/null\n@@ -1 +0,0 @@\n-Subproject commit 1111111111111111111111111111111111111111\n")]
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
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("submodule", StringComparison.Ordinal));
        AssertEx.False(result.Applied);
        AssertEx.False(Directory.Exists(Path.Combine(hostRoot, "vendor")), "no empty submodule directory is created on the host");
    }

    /// <summary>
    ///     A symlink block (mode <c>120000</c>) carries the link TARGET as its content, so applying one makes git
    ///     create a real link pointing wherever that names — a <c>../</c> climb the within-root guard never sees,
    ///     because it validates the link's own path.
    /// </summary>
    [Test]
    [Arguments("created",
        "diff --git a/repo-01/evil b/repo-01/evil\nnew file mode 120000\nindex 0000000..1111111\n--- /dev/null\n+++ b/repo-01/evil\n@@ -0,0 +1 @@\n+/etc/passwd\n\\ No newline at end of file\n")]
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
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("symbolic link", StringComparison.Ordinal));
        AssertEx.True(preview.Rejections.All(rejection => !rejection.Reason.Contains(hostRoot, StringComparison.Ordinal)),
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

        AssertEx.True(result.Applied, $"a file that merely describes a mode line applies. rejections: {Describe(result.Rejections)}");
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

        AssertEx.True(result.Applied, $"a .gitattributes / .gitignore change is ordinary. rejections: {Describe(result.Rejections)}");
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

        AssertEx.True(result.Applied, $"a non-repository folder is a valid apply target. rejections: {Describe(result.Rejections)}");
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
        AssertEx.True(results.All(result => result.Applied), $"both applies land. rejections: {Describe([.. results.SelectMany(result => result.Rejections)])}");
        AssertEx.Equal("a\nchanged\n", await File.ReadAllTextAsync(Path.Combine(hostRootA, "a.txt")));
        AssertEx.Equal("b\nchanged\n", await File.ReadAllTextAsync(Path.Combine(hostRootB, "b.txt")));
    }

    /// <summary>
    ///     The warning an operator is owed before approving: the patch applies, but these targets already carry work
    ///     of their own. It must never gate the apply — <c>git apply --check</c> stays the only thing that does.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WhenTargetsAlreadyCarryLocalChanges_WarnsWithoutBlocking()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        // The patch edits the TOP of a long file; the host's own edit is at the bottom, far outside the hunk's
        // context, so the drift is real and the patch still applies — exactly the case a blocking check would ruin.
        var before = string.Concat(Enumerable.Range(start: 1, count: 20).Select(line => $"l{line}\n"));
        var after = before.Replace("l2\n", "l2-changed\n", StringComparison.Ordinal);
        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", before, after), ("staged.txt", "s\n", "s-changed\n"));
        await SeedHostAsync(hostRoot, ("unrelated.txt", "untouched\n"));
        await InitHostRepositoryAsync(hostRoot);

        await SeedHostAsync(hostRoot, ("src/App.cs", before.Replace("l19\n", "l19-local\n", StringComparison.Ordinal)));
        await SeedHostAsync(hostRoot, ("staged.txt", "staged locally\n"));
        await GitOkAsync(hostRoot, "add", "staged.txt");

        // Back to the committed bytes in the WORK TREE, so the index alone differs and the patch still applies.
        await SeedHostAsync(hostRoot, ("staged.txt", "s\n"), ("unrelated.txt", "locally edited\n"));
        await WritePatchAsync(harness, "run-dirty", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-dirty"
        });

        AssertEx.True(preview.CanApply, $"a dirty target is a warning, never a refusal. rejections: {Describe(preview.Rejections)}");
        AssertEx.False(preview.DirtyCheckUnavailable, "the host state was readable, so nothing is unknown");
        AssertEx.Contains(preview.DirtyTargets, entry => entry is { Path: "repo-01/src/App.cs", State: "modified" });
        AssertEx.Contains(preview.DirtyTargets, entry => entry is { Path: "repo-01/staged.txt", State: "staged" });
        AssertEx.False(preview.DirtyTargets.Any(entry => entry.Path.Contains("unrelated", StringComparison.Ordinal)),
            "a dirty file the patch does not touch is none of this warning's business");
        AssertEx.True(preview.DirtyTargets.All(entry => !entry.Path.Contains(hostRoot, StringComparison.Ordinal)),
            "the warning carries no host path");
    }

    /// <summary>
    ///     An untracked file already sitting where the patch would create one. <c>git apply --check</c> refuses that
    ///     on its own; the warning is what tells the operator the refusal is about a file THEY left there.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WhenThePatchAddsAPathTheHostAlreadyHolds_WarnsUntracked()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostAsync(hostRoot, ("keep.txt", "keep\n"));
        await InitHostRepositoryAsync(hostRoot);

        var throwaway = NewTempDir();
        var patch = await GenerateAddPatchAsync("repo-01", throwaway, ("notes/new.txt", "from the run\n"));
        await SeedHostAsync(hostRoot, ("notes/new.txt", "mine, written by hand\n"));
        await WritePatchAsync(harness, "run-untracked", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-untracked"
        });

        AssertEx.Contains(preview.DirtyTargets, entry => entry is { Path: "repo-01/notes/new.txt", State: "untracked" });
        AssertEx.False(preview.DirtyCheckUnavailable);
    }

    /// <summary>
    ///     A selected folder need not be a repository, and most are not. "No history to compare against" is silence,
    ///     not a warning and not a failure — a banner on every non-repository folder would be a false alarm.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WhenTheHostFolderIsNoWorkTree_WarnsAboutNothingAndReportsNothingUnknown()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        AssertEx.False(Directory.Exists(Path.Combine(hostRoot, ".git")), "the host folder must not be a repository for this test to prove anything");
        await WritePatchAsync(harness, "run-norepo-dirty", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-norepo-dirty"
        });

        AssertEx.True(preview.CanApply, $"rejections: {Describe(preview.Rejections)}");
        AssertEx.Empty(preview.DirtyTargets);
        AssertEx.False(preview.DirtyCheckUnavailable, "no repository is not the same as could-not-read");
    }

    /// <summary>
    ///     A selected folder that is a SUBDIRECTORY of a repository: git reports status paths from the repository
    ///     root, so the folder-relative name only comes out right if the repository prefix is stripped.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WhenTheFolderSitsInsideARepository_NamesTargetsFolderRelative()
    {
        var harness = NewHarness();
        var repositoryRoot = NewTempDir();
        var hostRoot = Path.Combine(repositoryRoot, "packages", "app");
        Directory.CreateDirectory(hostRoot);
        harness.Resolver.Add(Guid.NewGuid(), "repo-01", hostRoot);

        var before = string.Concat(Enumerable.Range(start: 1, count: 20).Select(line => $"l{line}\n"));
        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", before, before.Replace("l2\n", "l2-changed\n", StringComparison.Ordinal)));
        await InitHostRepositoryAsync(repositoryRoot);
        await SeedHostAsync(hostRoot, ("src/App.cs", before.Replace("l19\n", "l19-local\n", StringComparison.Ordinal)));
        await WritePatchAsync(harness, "run-nested", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-nested"
        });

        AssertEx.True(preview.CanApply, $"rejections: {Describe(preview.Rejections)}");
        AssertEx.Contains(preview.DirtyTargets, entry => entry is { Path: "repo-01/src/App.cs", State: "modified" });
    }

    /// <summary>
    ///     The warning is advisory, so a git that cannot answer must not cost the operator their preview. It says the
    ///     state is unknown instead of implying "clean".
    /// </summary>
    [Test]
    public async Task PreviewAsync_WhenTheHostStateCannotBeRead_StillPreviewsAndSaysTheStateIsUnknown()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await InitHostRepositoryAsync(hostRoot);

        // An index git cannot map: `rev-parse` still reports a work tree, so this is a FAILED read rather than the
        // no-repository case, which is the distinction the two fields exist to make.
        var indexPath = Path.Combine(hostRoot, ".git", "index");
        File.Delete(indexPath);
        Directory.CreateDirectory(indexPath);
        await WritePatchAsync(harness, "run-status-broken", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-status-broken"
        });

        AssertEx.True(preview.CanApply, $"a failed status read must never refuse a patch. rejections: {Describe(preview.Rejections)}");
        AssertEx.True(preview.DirtyCheckUnavailable, "the state could not be read, and the operator is told so");
        AssertEx.Empty(preview.DirtyTargets);
    }

    /// <summary>
    ///     A preview is a read of the operator's repository, including of its <c>.git</c>: <c>status</c> refreshes
    ///     and rewrites the index unless it is told not to.
    /// </summary>
    [Test]
    public async Task PreviewAsync_LeavesTheHostGitDirectoryByteIdentical()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var before = string.Concat(Enumerable.Range(start: 1, count: 20).Select(line => $"l{line}\n"));
        var patch = await GenerateGPatchAsync("repo-01",
            hostRoot,
            ("src/App.cs", before, before.Replace("l2\n", "l2-changed\n", StringComparison.Ordinal)),
            ("docs/Notes.md", "notes\n", "notes\nmore\n"));
        await InitHostRepositoryAsync(hostRoot);
        await SeedHostAsync(hostRoot, ("src/App.cs", before.Replace("l19\n", "l19-local\n", StringComparison.Ordinal)));

        // A PATCH TARGET whose content still matches its commit but whose cached stat does not: the entry `status`
        // refreshes by rewriting the index. Git refreshes only what the pathspec names, so it has to be a target.
        var notes = Path.Combine(hostRoot, "docs", "Notes.md");
        File.SetLastWriteTimeUtc(notes, File.GetLastWriteTimeUtc(notes).AddSeconds(value: 50));
        await WritePatchAsync(harness, "run-nowrite", patch);

        var gitDirectory = Path.Combine(hostRoot, ".git");
        var fingerprintBefore = FingerprintDirectory(gitDirectory);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-nowrite"
        });

        AssertEx.NotEmpty(preview.DirtyTargets, "the status read must actually have happened, or this proves nothing.");
        AssertEx.Equal(fingerprintBefore, FingerprintDirectory(gitDirectory), "a preview must not write into the operator's own .git");
    }

    /// <summary>
    ///     A refusal names the entry it is about. The reason stays the same sentence; what is new is that the
    ///     operator can tell WHICH of several changed files it refers to.
    /// </summary>
    [Test]
    [Arguments("symlink", "repo-01/evil", "diff --git a/repo-01/evil b/repo-01/evil\nnew file mode 120000\nindex 0000000..1111111\n--- /dev/null\n+++ b/repo-01/evil\n@@ -0,0 +1 @@\n+/etc/passwd\n")]
    [Arguments("gitlink", "repo-01/vendor",
        "diff --git a/repo-01/vendor b/repo-01/vendor\nnew file mode 160000\nindex 0000000..1111111\n--- /dev/null\n+++ b/repo-01/vendor\n@@ -0,0 +1 @@\n+Subproject commit 1111111111111111111111111111111111111111\n")]
    [Arguments("git directory", "repo-01/.git/config",
        "diff --git a/repo-01/.git/config b/repo-01/.git/config\nnew file mode 100644\n--- /dev/null\n+++ b/repo-01/.git/config\n@@ -0,0 +1 @@\n+[core]\n")]
    [Arguments("traversal", "repo-01/../escape.txt",
        "diff --git a/repo-01/../escape.txt b/repo-01/../escape.txt\nnew file mode 100644\n--- /dev/null\n+++ b/repo-01/../escape.txt\n@@ -0,0 +1 @@\n+pwned\n")]
    [Arguments("mode-only git directory", "repo-01/.git/hooks/pre-commit", "diff --git a/repo-01/.git/hooks/pre-commit b/repo-01/.git/hooks/pre-commit\nold mode 100644\nnew mode 100755\n")]
    public async Task PreviewAsync_WhenAnEntryIsRefused_NamesItFolderRelative(string shape, string expectedPath, string patch)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await WritePatchAsync(harness, "run-named", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-named"
        });

        AssertEx.False(preview.CanApply, $"a {shape} entry is refused");
        AssertEx.Contains(preview.Rejections, rejection => rejection.Path == expectedPath);
        AssertEx.True(preview.Rejections.All(rejection => rejection.Path?.Contains(hostRoot, StringComparison.Ordinal) != true),
            "a named entry is folder-relative, never a host path");
    }

    /// <summary>
    ///     The binary refusal names the file too, one refusal per binary entry, so a mixed patch says which of its
    ///     files is the one that cannot be reviewed here.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WhenABinaryEntryIsRefused_NamesIt()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostBinaryAsync(hostRoot, "blob.bin", [0x00, 0x01, 0x02, 0x03]);

        var patch = await GenerateBinaryPatchAsync("repo-01", "blob.bin", [0x00, 0x01, 0x02, 0x03], [0x00, 0x01, 0x02, 0x03, 0xFF, 0x10]);
        await WritePatchAsync(harness, "run-binary-named", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-binary-named"
        });

        AssertEx.Contains(preview.Rejections,
            rejection => rejection.Path == "repo-01/blob.bin" && rejection.Reason.Contains("binary", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A literal git's own reader would give up on stays UNNAMED. git answers one by taking the raw text as the
    ///     name, so there is nothing here that could be echoed and still describe the file git would write.
    /// </summary>
    [Test]
    [Arguments("a two-digit octal escape", "od\\56d.txt")]
    [Arguments("an escape git does not define", "od\\zd.txt")]
    [Arguments("an octal escape over one byte", "od\\400d.txt")]
    [Arguments("text after the closing quote", "od.txt\" trailing")]
    public async Task PreviewAsync_WithMalformedQuoting_RefusesWithoutNamingIt(string shape, string inner)
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");
        var patch = $"diff --git \"a/repo-01/{inner}\" \"b/repo-01/{inner}\"\nnew file mode 100644\n"
                    + $"index 0000000..e69de29\n--- /dev/null\n+++ \"b/repo-01/{inner}\"\n@@ -0,0 +1 @@\n+text\n";
        await WritePatchAsync(harness, "run-unnamed", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-unnamed"
        });

        AssertEx.False(preview.CanApply, $"{shape} is refused");
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("quoted path", StringComparison.Ordinal), shape);
        AssertEx.True(preview.Rejections.All(rejection => rejection.Path is null),
            $"an unreadable literal must not be echoed back: {Describe(preview.Rejections)}");
    }

    /// <summary>
    ///     All-or-nothing survives the decoder: one refused block still voids the whole plan, leaving no file list
    ///     for the clean blocks beside it — and the refusal names the file that caused it.
    /// </summary>
    [Test]
    public async Task PreviewAndApply_WithOneRefusedQuotedBlockAmongCleanOnes_RefuseTheWholePlan()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        await SeedHostAsync(hostRoot, ("clean.txt", "old\n"));

        const string Patch = "diff --git a/repo-01/clean.txt b/repo-01/clean.txt\nindex 0000001..0000002 100644\n"
                             + "--- a/repo-01/clean.txt\n+++ b/repo-01/clean.txt\n@@ -1 +1 @@\n-old\n+new\n"
                             + "diff --git \"a/repo-01/ev\\007il.txt\" \"b/repo-01/ev\\007il.txt\"\nnew file mode 100644\n"
                             + "index 0000000..e69de29\n--- /dev/null\n+++ \"b/repo-01/ev\\007il.txt\"\n@@ -0,0 +1 @@\n+text\n";
        await WritePatchAsync(harness, "run-quoted-mixed", Patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-mixed"
        });
        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-quoted-mixed"
        });

        AssertEx.False(preview.CanApply, "one refused block refuses the whole patch");
        AssertEx.Empty(preview.Files, "no per-file breakdown survives a rejected plan");
        AssertEx.Contains(preview.Rejections, rejection => rejection.Path == "repo-01/ev\\u{0007}il.txt", Describe(preview.Rejections));
        AssertEx.False(result.Applied);
        AssertEx.Equal("old\n", await File.ReadAllTextAsync(Path.Combine(hostRoot, "clean.txt")), "the clean block's file is untouched");
    }

    /// <summary>
    ///     A name that can move, hide or reorder the text around it spoofs another name at the moment the operator
    ///     decides to write to their own disk. Every such code point is shown as a visible escape instead.
    /// </summary>
    [Test]
    [Arguments("right-to-left override", "\u202E", "repo-01/ev\\u{202E}il.txt")]
    [Arguments("zero-width joiner", "\u200D", "repo-01/ev\\u{200D}il.txt")]
    [Arguments("directional isolate", "\u2066", "repo-01/ev\\u{2066}il.txt")]
    [Arguments("line separator", "\u2028", "repo-01/ev\\u{2028}il.txt")]
    [Arguments("byte-order mark", "\uFEFF", "repo-01/ev\\u{FEFF}il.txt")]
    [Arguments("bell", "\u0007", "repo-01/ev\\u{0007}il.txt")]
    public async Task PreviewAsync_WithASpoofingEntryName_NamesItEscaped(string shape, string injected, string expected)
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        var name = "ev" + injected + "il.txt";
        var patch = $"diff --git a/repo-01/{name} b/repo-01/{name}\nnew file mode 120000\nindex 0000000..1111111\n"
                    + $"--- /dev/null\n+++ b/repo-01/{name}\n@@ -0,0 +1 @@\n+/etc/passwd\n";
        await WritePatchAsync(harness, "run-spoof", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-spoof"
        });

        AssertEx.False(preview.CanApply, $"a {shape} name is still a symbolic link, and still refused");
        AssertEx.Contains(preview.Rejections, rejection => rejection.Path == expected);
        AssertEx.True(preview.Rejections.All(rejection => rejection.Path?.Contains(injected, StringComparison.Ordinal) != true),
            $"the raw {shape} must not survive into the response");
        AssertEx.True(preview.Rejections.All(rejection => rejection.Path?.Contains(hostRoot, StringComparison.Ordinal) != true));
    }

    /// <summary>
    ///     The same rule on the per-file table, which is the list the operator actually reads before approving. The
    ///     patch here parses cleanly and only fails its <c>--check</c>, which is what leaves the file list populated.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WithASpoofingFileName_EscapesItInTheFileList()
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");
        const string Name = "no\u202Etes.txt";
        const string Patch = "diff --git a/repo-01/no\u202Etes.txt b/repo-01/no\u202Etes.txt\n"
                             + "index 0000001..0000002 100644\n--- a/repo-01/no\u202Etes.txt\n+++ b/repo-01/no\u202Etes.txt\n"
                             + "@@ -1 +1 @@\n-old\n+new\n";
        await WritePatchAsync(harness, "run-spoof-files", Patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-spoof-files"
        });

        AssertEx.Contains(preview.Files, file => file.RelativePath == "no\\u{202E}tes.txt");
        AssertEx.True(preview.Files.All(file => !file.RelativePath.Contains(Name, StringComparison.Ordinal)),
            "the raw name must not survive into the file list");
    }

    /// <summary>
    ///     The control: this is a DISPLAY rule, not a character set. A name that is merely not ASCII is a perfectly
    ///     ordinary name and must arrive unchanged, astral-plane code points included.
    /// </summary>
    [Test]
    [Arguments("umlaut", "Größe.cs")]
    [Arguments("CJK", "文档.md")]
    [Arguments("astral plane", "notes-\U0001F600.md")]
    public async Task PreviewAsync_WithALegitimateNonAsciiName_LeavesItUnchanged(string shape, string name)
    {
        var harness = NewHarness();
        harness.AddFolder("repo-01");
        var patch = $"diff --git a/repo-01/{name} b/repo-01/{name}\nindex 0000001..0000002 100644\n"
                    + $"--- a/repo-01/{name}\n+++ b/repo-01/{name}\n@@ -1 +1 @@\n-old\n+new\n";
        await WritePatchAsync(harness, "run-nonascii", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-nonascii"
        });

        AssertEx.Contains(preview.Files, file => file.RelativePath == name, $"a {shape} name must not be mangled");
    }

    /// <summary>
    ///     The warning's own names go through the same rule: a spoofing name reaches it from the host's work tree
    ///     rather than from the patch text, and must be just as unable to imitate another file.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WithASpoofingNameOnTheHost_EscapesItInTheWarning()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("A bidi override is not a legal character in a Windows file name, so the host file cannot be created.");
        }

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var before = string.Concat(Enumerable.Range(start: 1, count: 20).Select(line => $"l{line}\n"));
        var patch = await GenerateGPatchAsync("repo-01",
            hostRoot,
            ("no\u202Etes.txt", before, before.Replace("l2\n", "l2-changed\n", StringComparison.Ordinal)));
        await InitHostRepositoryAsync(hostRoot);
        await SeedHostAsync(hostRoot, ("no\u202Etes.txt", before.Replace("l19\n", "l19-local\n", StringComparison.Ordinal)));
        await WritePatchAsync(harness, "run-spoof-dirty", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-spoof-dirty"
        });

        AssertEx.Contains(preview.DirtyTargets, entry => entry.Path == "repo-01/no\\u{202E}tes.txt");
        AssertEx.True(preview.DirtyTargets.All(entry => !entry.Path.Contains('\u202E', StringComparison.Ordinal)),
            "the raw override must not survive into the warning");
    }

    /// <summary>
    ///     These paths are model-authored, so they are pathspecs only in the sense that git would read magic in
    ///     them. A leading colon is the sharp case: unquoted it matches nothing, and the warning goes silent.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WithPathspecMagicInATargetName_StillReadsThatFilesOwnState()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("A colon is not a legal character in a Windows file name, so the host file cannot be created.");
        }

        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var before = string.Concat(Enumerable.Range(start: 1, count: 20).Select(line => $"l{line}\n"));
        var patch = await GenerateGPatchAsync("repo-01",
            hostRoot,
            (":magic.txt", before, before.Replace("l2\n", "l2-changed\n", StringComparison.Ordinal)));
        await InitHostRepositoryAsync(hostRoot);
        await SeedHostAsync(hostRoot, (":magic.txt", before.Replace("l19\n", "l19-local\n", StringComparison.Ordinal)));
        await WritePatchAsync(harness, "run-magic", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-magic"
        });

        AssertEx.False(preview.DirtyCheckUnavailable, "a colon in a file name is a file name, not a failure");
        AssertEx.Contains(preview.DirtyTargets, entry => entry is { Path: "repo-01/:magic.txt", State: "modified" });
    }

    /// <summary>
    ///     A repository git refuses to read is not the same answer as no repository. Silence would read as "clean"
    ///     to the operator, so a folder that LOOKS like a checkout and cannot be read reports unknown instead.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WhenTheRepositoryItselfCannotBeRead_ReportsTheStateAsUnknown()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await InitHostRepositoryAsync(hostRoot);

        // A .git that is still there but that git will not open: discovery fails, and git answers exactly as it
        // does for a folder that never was a repository, so the directory on disk is what tells the two apart.
        File.Delete(Path.Combine(hostRoot, ".git", "HEAD"));
        await WritePatchAsync(harness, "run-broken-repo", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-broken-repo"
        });

        AssertEx.True(preview.CanApply, $"an unreadable repository must not refuse a patch. rejections: {Describe(preview.Rejections)}");
        AssertEx.True(preview.DirtyCheckUnavailable, "a repository git cannot read is unknown, never clean");
        AssertEx.Empty(preview.DirtyTargets);
    }

    /// <summary>
    ///     A submodule the operator already had is not this preview's business: the warning is about files the patch
    ///     will rewrite, and a submodule's own contents are not among them.
    /// </summary>
    [Test]
    public async Task PreviewAsync_WithADirtySubmodule_ReportsNoEntryForIt()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var before = string.Concat(Enumerable.Range(start: 1, count: 20).Select(line => $"l{line}\n"));
        var patch = await GenerateGPatchAsync("repo-01",
            hostRoot,
            ("src/App.cs", before, before.Replace("l2\n", "l2-changed\n", StringComparison.Ordinal)),
            ("sub", "placeholder\n", "placeholder\nchanged\n"));

        var inner = NewTempDir();
        await WriteTextAsync(Path.Combine(inner, "i.txt"), "inner\n");
        await InitHostRepositoryAsync(inner);

        File.Delete(Path.Combine(hostRoot, "sub"));
        await InitHostRepositoryAsync(hostRoot);
        await GitOkAsync(hostRoot,
            "-c", "protocol.file.allow=always",
            "-c", "user.email=agent-home@localhost",
            "-c", "user.name=AgentHome",
            "submodule", "add", "--quiet", inner, "sub");
        await GitOkAsync(hostRoot, "-c", "user.email=agent-home@localhost", "-c", "user.name=AgentHome", "commit", "-m", "add submodule");

        // Dirty the submodule's own work tree, which is what makes the parent report `sub` as modified.
        await WriteTextAsync(Path.Combine(hostRoot, "sub", "i.txt"), "inner changed\n");
        await SeedHostAsync(hostRoot, ("src/App.cs", before.Replace("l19\n", "l19-local\n", StringComparison.Ordinal)));
        await WritePatchAsync(harness, "run-submodule", patch);

        var preview = await harness.Service.PreviewAsync(new NodePatchApplyRequest
        {
            RunId = "run-submodule"
        });

        AssertEx.False(preview.DirtyCheckUnavailable);
        AssertEx.Contains(preview.DirtyTargets, entry => entry.Path == "repo-01/src/App.cs",
            "the ordinary target must still be reported, or this test proves only that nothing was read at all");
        AssertEx.False(preview.DirtyTargets.Any(entry => entry.Path == "repo-01/sub"),
            "a dirty submodule must not surface as a dirty patch target");
    }

    /// <summary>Renders rejections for an assertion message, entry name included where the service supplied one.</summary>
    /// <summary>
    ///     A whole-patch refusal has no entry to name, and must not borrow one. The hash binding is the clearest
    ///     example: nothing about a single file failed.
    /// </summary>
    [Test]
    public async Task ApplyApprovedAsync_WhenTheRefusalIsAboutTheWholePatch_NamesNoEntry()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");

        var patch = await GenerateGPatchAsync("repo-01", hostRoot, ("src/App.cs", "alpha\n", "alpha\nbravo\n"));
        await WritePatchAsync(harness, "run-whole", patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-whole",
            ExpectedPatchSha256 = new string(c: 'a', count: 64)
        });

        AssertEx.False(result.Applied);
        AssertEx.True(result.Rejections.All(rejection => rejection.Path is null), Describe(result.Rejections));
    }

    /// <summary>The refused entry reaches the run log folder-relative, beside its reason and with no host path.</summary>
    [Test]
    public async Task ApplyApprovedAsync_WhenAnEntryIsRefused_LogsItsNameFolderRelative()
    {
        var harness = NewHarness();
        var hostRoot = harness.AddFolder("repo-01");
        Directory.CreateDirectory(Path.Combine(harness.AgentHomeRoot, "runs", "run-reject-log", "logs"));

        const string Patch = "diff --git a/repo-01/evil b/repo-01/evil\nnew file mode 120000\nindex 0000000..1111111\n"
                             + "--- /dev/null\n+++ b/repo-01/evil\n@@ -0,0 +1 @@\n+/etc/passwd\n";
        await WritePatchAsync(harness, "run-reject-log", Patch);

        var result = await harness.Service.ApplyApprovedAsync(new NodePatchApplyRequest
        {
            RunId = "run-reject-log"
        });
        AssertEx.False(result.Applied);

        var events = await File.ReadAllTextAsync(Path.Combine(harness.AgentHomeRoot, "runs", "run-reject-log", "logs", "events.jsonl"));
        AssertEx.Contains(events, "patch_apply_rejected");
        AssertEx.Contains(events, "repo-01/evil");
        AssertEx.False(events.Contains(hostRoot, StringComparison.Ordinal), "the log must not leak a host path");
    }

    /// <summary>A stable fingerprint of every file under a directory: relative path, length and content hash.</summary>
    private static string FingerprintDirectory(string root)
    {
        var builder = new StringBuilder();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            builder.Append(Path.GetRelativePath(root, path).Replace('\\', '/'))
                   .Append(':')
                   .Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
                   .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Turns a seeded host folder into a committed git work tree, so its later drift is measurable.</summary>
    private static async Task InitHostRepositoryAsync(string hostRoot)
    {
        await GitOkAsync(hostRoot, "init");
        await GitOkAsync(hostRoot, "config", "core.autocrlf", "false");
        await GitOkAsync(hostRoot, "config", "core.filemode", "false");
        await GitOkAsync(hostRoot, "add", "-A");
        await GitOkAsync(hostRoot, "-c", "user.email=agent-home@localhost", "-c", "user.name=AgentHome", "commit", "-m", "host baseline", "--allow-empty");
    }

    /// <summary>
    ///     A patch that ADDS files, generated against a baseline that never held them, so the host folder is not
    ///     seeded with a pre-image the way <see cref="GenerateGPatchAsync" /> seeds one.
    /// </summary>
    private static async Task<string> GenerateAddPatchAsync(string alias, string baselineRoot, params (string Relative, string Content)[] files)
    {
        var selected = Path.Combine(baselineRoot, "selected");
        Directory.CreateDirectory(selected);
        await InitBaselineAsync(selected);

        foreach (var (relative, content) in files)
        {
            await WriteTextAsync(Path.Combine(selected, alias, relative), content);
        }

        // Staged before the diff, the way the export stages before it diffs: `git diff HEAD` cannot see a file
        // git has never been told about, so an unstaged add produces an EMPTY patch.
        await GitOkAsync(selected, "add", "-A");
        return await DiffAsync(selected);
    }

    /// <summary>Renders rejections for an assertion message, entry name included where the service supplied one.</summary>
    private static string Describe(IReadOnlyList<PatchApplyRejection> rejections) =>
        string.Join(separator: ';', rejections.Select(rejection => rejection.Path is null ? rejection.Reason : $"{rejection.Path}: {rejection.Reason}"));

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
        // One guard for the whole harness, as the singleton registration gives the node: a fresh one per service
        // would be a guard per request, which guards nothing.
        var applyGuard = new AgentHomeRunApplyGuard();

        NodePatchApplyService NewService() =>
            new(resolver,
                options,
                runtimeSettings,
                new FakeNodeDataDirectory(agentHomeStateRoot),
                new StubIdentityProvider(),
                applyGuard,
                scopeFactory.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<NodePatchApplyService>.Instance);

        return new TestHarness(NewService, resolver, agentHomeStateRoot, agentHomeRoot, applyGuard, () => NewTempDir());
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

        // The export runs every git call with core.quotePath=false, so a non-ASCII name reaches the patch as its
        // own bytes. A fixture that let git C-quote it would generate a patch shape the export never produces.
        await GitOkAsync(repoRoot, "config", "core.quotePath", "false");
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

    /// <summary>
    ///     A run directory with the <c>logs/</c> the run logger writes into, mirroring the real layout. Returns the
    ///     run directory itself, which is what a delete removes.
    /// </summary>
    private static string SeedRunDirectory(TestHarness harness, string runId)
    {
        var runDirectory = Path.Combine(harness.AgentHomeRoot, "runs", runId);
        Directory.CreateDirectory(Path.Combine(runDirectory, "logs"));
        return runDirectory;
    }

    /// <summary>The operator delete over the same tree and apply guard the harness's apply service holds.</summary>
    /// <remarks>
    ///     The pairing the node's singleton registrations produce. The executing registry is empty and no run is in
    ///     flight, so the only thing that can refuse a delete in these tests is the apply guard.
    /// </remarks>
    private static AgentHomeRunDeleteService DeleteService(TestHarness harness) =>
        new(Options.Create(new AgentHomeOptions
            {
                RootPath = harness.StateRoot
            }),
            new FakeNodeDataDirectory(harness.StateRoot),
            new AgentHomeRunExecutionRegistry(),
            harness.ApplyGuard,
            NullLogger<AgentHomeRunDeleteService>.Instance);

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

        public TestHarness(Func<NodePatchApplyService> newService,
            FakeResolver resolver,
            string stateRoot,
            string agentHomeRoot,
            AgentHomeRunApplyGuard applyGuard,
            Func<string> newFolder)
        {
            _newService = newService;
            Service = newService();
            _resolver = resolver;
            StateRoot = stateRoot;
            AgentHomeRoot = agentHomeRoot;
            ApplyGuard = applyGuard;
            _newFolder = newFolder;
        }

        /// <summary>One shared instance, for the tests that need only one.</summary>
        public NodePatchApplyService Service { get; }

        /// <summary>A FRESH instance over the same state — what a second scoped request resolves.</summary>
        public NodePatchApplyService NewService() =>
            _newService();

        /// <summary>The <c>RootPath</c> the options carry — what a service sharing this tree is configured with.</summary>
        public string StateRoot { get; }

        public string AgentHomeRoot { get; }

        /// <summary>The same instance the apply service holds, so a delete can be gated against a live apply.</summary>
        public AgentHomeRunApplyGuard ApplyGuard { get; }

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

        /// <summary>
        ///     Thrown out of the next resolve instead of answering. An unexpected exception type on purpose: the
        ///     apply catches <see cref="SelectedFolderValidationException" />, so only something else proves what
        ///     happens to state the apply holds when it fails in a way it never planned for.
        /// </summary>
        public Exception? ThrowOnList { get; set; }

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

                if (ThrowOnList is { } failure)
                {
                    throw failure;
                }

                IReadOnlyList<SelectedFolderReference> references =
                    _folders.Values.Select(folder => new SelectedFolderReference
                    {
                        Id = folder.Id.ToString(),
                        Alias = folder.Alias
                    }).ToList();
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
            _folders[id] = new ResolvedSelectedFolder
            {
                Id = id,
                Alias = alias,
                HostPath = hostPath,
                Mode = SelectedFolderMode.Copy
            };
        }
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity
            {
                OwnerUserId = "owner-a",
                NodeId = "node-1"
            });
        }
    }
}
