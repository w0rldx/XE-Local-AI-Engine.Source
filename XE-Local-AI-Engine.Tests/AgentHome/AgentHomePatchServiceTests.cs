namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Patch-export coverage: the real <see cref="AgentHomePatchService" /> runs the two diff commands against
///     the <see cref="FakeSandboxRuntimeProvider" /> (whose git is scripted — no Docker, no real git), then parses the
///     scripted output into <c>changed-files.json</c>, enforces the byte budget, and writes the artifacts host-side.
///     Real-git byte-equality under <c>.gitattributes</c> perturbation, the <c>--binary</c> not-silently-dropped
///     behavior, and binary-patch apply rejection are proven by the env-gated real-git smoke — not here.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AgentHomePatchServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 5, day: 29, hour: 12, minute: 0, second: 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The written paths the classification test leaves out of its diff, in the order the export asks about them.</summary>
    private static readonly string[] MissingPaths = ["repo-01/hidden.txt", "repo-01/gone.tmp", "repo-01/same.cs", "repo-01/unstaged.txt", "repo-01/assumed.cs"];

    /// <summary>
    ///     The path shapes git C-quotes in the tab-delimited <c>--name-status</c> format and emits verbatim under
    ///     <c>-z</c>, each paired with the folder-relative name the entry must carry once the alias is stripped.
    /// </summary>
    private static readonly (string WorkspacePath, string RelativePath)[] QuotablePaths =
    [
        ("repo-01/say \"hi\".txt", "say \"hi\".txt"),
        ("repo-01/back\\slash.txt", "back\\slash.txt"),
        ("repo-01/tab\there.txt", "tab\there.txt"),
        ("repo-01/new\nline.txt", "new\nline.txt"),
        ("repo-01/grüße-日本.txt", "grüße-日本.txt"),
        ("repo-01/ padded .txt", " padded .txt")
    ];

    /// <summary>One patch carrying every block shape the line walk has to tell apart.</summary>
    private const string MixedShapePatch = """
        diff --git a/repo-01/src/App.cs b/repo-01/src/App.cs
        index 1111111..2222222 100644
        --- a/repo-01/src/App.cs
        +++ b/repo-01/src/App.cs
        @@ -1,3 +1,4 @@
         context stays
        -old line
        +new line
        +extra added
        \ No newline at end of file
        diff --git a/repo-01/old.txt b/repo-01/new.txt
        similarity index 100%
        rename from repo-01/old.txt
        rename to repo-01/new.txt
        diff --git a/repo-01/data.bin b/repo-01/data.bin
        new file mode 100644
        index 0000000..3333333
        GIT binary patch
        literal 4
        Lc$_OqJOKb%00

        diff --git a/repo-01/empty.txt b/repo-01/empty.txt
        new file mode 100644
        index 0000000..e69de29
        diff --git a/repo-01/gone.txt b/repo-01/gone.txt
        deleted file mode 100644
        index 4444444..0000000
        --- a/repo-01/gone.txt
        +++ /dev/null
        @@ -1,2 +0,0 @@
        -first
        -second
        """;

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
    public async Task ExportPatchAsync_IssuesHardenedDiffCommandsInWorkspace()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, NameStatusZ("M", "repo-01/src/App.cs"));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "patch-body\n");
        var service = CreateService(provider);

        await service.ExportPatchAsync(handle, Request("run-x", NewTempDir(), Folder("repo-01")));

        var gitCommands = provider.ExecutedCommands.Where(command => command.Executable == "git").ToArray();
        AssertEx.True(gitCommands.Any(command => command.Arguments.Contains("--binary")
                                                 && HasHardenedFlags(command.Arguments)
                                                 && command.WorkingDirectory == "/agent-home/workspace/selected"),
            "the patch diff runs with --binary, the hardened -c flags, and the workspace working directory");
        AssertEx.True(gitCommands.Any(command => command.Arguments.Contains("--name-status")
                                                 && command.Arguments.Contains("-z")
                                                 && HasHardenedFlags(command.Arguments)
                                                 && command.WorkingDirectory == "/agent-home/workspace/selected"),
            "the name-status diff runs with -z, the hardened -c flags and the workspace working directory");
    }

    /// <summary>
    ///     A working-tree <c>diff HEAD</c> cannot see an untracked path, so the export stages first and diffs the
    ///     index. Order is the whole point: a stage that ran after the diff would leave a created file out, exactly as
    ///     before.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_StagesTheWorkspaceBeforeItDiffs()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, NameStatusZ("A", "repo-01/docs/notes.md"));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "patch-body\n");
        var service = CreateService(provider);

        await service.ExportPatchAsync(handle, Request("run-stage", NewTempDir(), Folder("repo-01")));

        var gitCommands = provider.ExecutedCommands.Where(command => command.Executable == "git").ToArray();
        var stageIndex = Array.FindIndex(gitCommands, command => command.Arguments.Contains("add"));
        var diffIndex = Array.FindIndex(gitCommands, command => command.Arguments.Contains("--binary"));

        AssertEx.True(stageIndex >= 0, "the export stages the workspace");
        AssertEx.True(stageIndex < diffIndex, "staging runs BEFORE the diff, or a created file is still invisible to it");
        AssertEx.True(HasHardenedFlags(gitCommands[stageIndex].Arguments)
                      && gitCommands[stageIndex].WorkingDirectory == "/agent-home/workspace/selected",
            "the staging add carries the same hardened -c flags and workspace working directory as the diffs");
        AssertEx.False(gitCommands[stageIndex].Arguments.Contains("--force"),
            "staging honours .gitignore exactly as the baseline's own add -A does");
        AssertEx.True(gitCommands.All(command => !command.Arguments.Contains("--binary") || command.Arguments.Contains("--cached")),
            "the diffs read the index, not the working tree");
    }

    [Test]
    public async Task ExportPatchAsync_WhenStagingFails_ReportsFailureAndWritesNothing()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.StageAll("repo-01"), exitCode: 128, string.Empty, "fatal: unable to index file");
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, string.Empty);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, string.Empty);
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-stage-fail", runDir, Folder("repo-01")));

        AssertEx.True(export.Failed,
            "a failed stage leaves created files out of the index, so the export must refuse rather than report a clean zero-change run");
        AssertEx.False(Directory.Exists(Path.Combine(runDir, "patches")), "no artifacts are written when staging failed");
    }

    [Test]
    public async Task ExportPatchAsync_BuildsChangedFilesJsonMappedToFolderIdsWithRelativePaths()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());

        var nameStatus = NameStatusZ("M", "repo-01/src/Program.cs",
            "A", "repo-01/src/New.cs",
            "D", "repo-01/old/Gone.cs",
            "R100", "repo-01/a.txt", "repo-01/b.txt",
            "M", "repo-02/lib/X.cs");
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01", "repo-02"), exitCode: 0, nameStatus);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01", "repo-02"), exitCode: 0, "diff --git a/repo-01/src/Program.cs b/repo-01/src/Program.cs\n");
        var service = CreateService(provider);

        var repo01 = Folder("repo-01");
        var repo02 = Folder("repo-02");
        var runDir = NewTempDir();

        var export = await service.ExportPatchAsync(handle, Request("run-1", runDir, repo01, repo02));

        AssertEx.Equal(expected: 5, export.ChangedFileCount);
        AssertEx.False(export.Blocked);
        AssertEx.Equal("runs/run-1/patches/changes.patch", export.PatchRelativePath);
        AssertEx.Equal("runs/run-1/patches/changed-files.json", export.ChangedFilesRelativePath);

        var json = await File.ReadAllTextAsync(Path.Combine(runDir, "patches", "changed-files.json"));
        var entries = JsonSerializer.Deserialize<ChangedFileEntry[]>(json, JsonOptions)!;

        AssertEx.Equal(expected: 5, entries.Length);
        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "src/Program.cs", "modified");
        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "src/New.cs", "added");
        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "old/Gone.cs", "deleted");
        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "b.txt", "renamed");
        AssertEntry(entries, repo02.Id.ToString(), "repo-02", "lib/X.cs", "modified");

        AssertEx.True(entries.All(entry => !entry.RelativePath.StartsWith("repo-0", StringComparison.Ordinal)),
            "relativePath is folder-relative — the alias prefix is stripped");
        AssertEx.False(json.Contains(runDir, StringComparison.Ordinal), "changed-files.json must not leak a host path");

        var patch = await File.ReadAllTextAsync(Path.Combine(runDir, "patches", "changes.patch"));
        AssertEx.Contains(patch, "diff --git");
    }

    [Test]
    public async Task ExportPatchAsync_WhenPatchOverBudget_BlocksPatchButKeepsMetadata()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, NameStatusZ("M", "repo-01/src/App.cs"));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, new string(c: 'x', count: 4096));
        var service = CreateService(provider, maxPatchBytes: 16);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-2", runDir, Folder("repo-01")));

        AssertEx.True(export.Blocked, "a patch over MaxPatchBytes is blocked");
        AssertEx.Equal(expected: 1, export.ChangedFileCount);
        AssertEx.True(export.PatchRelativePath is null, "a blocked patch is not written, so its path is null");
        AssertEx.Equal("runs/run-2/patches/changed-files.json", export.ChangedFilesRelativePath);

        AssertEx.False(File.Exists(Path.Combine(runDir, "patches", "changes.patch")), "the oversized patch must not be written");
        AssertEx.True(File.Exists(Path.Combine(runDir, "patches", "changed-files.json")), "the metadata is kept when blocked");
    }

    [Test]
    public async Task ExportPatchAsync_WhenNoChanges_WritesNeitherArtifact()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, string.Empty);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, string.Empty);
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-3", runDir, Folder("repo-01")));

        AssertEx.Equal(expected: 0, export.ChangedFileCount);
        AssertEx.True(export.PatchRelativePath is null, "no changes means no patch path");
        AssertEx.True(export.ChangedFilesRelativePath is null, "no changes means no metadata path");
        AssertEx.False(Directory.Exists(Path.Combine(runDir, "patches")), "no patches directory is created when nothing changed");
    }

    [Test]
    public async Task ExportPatchAsync_WhenDiffCommandFails_ReportsFailureAndWritesNothing()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 128, string.Empty, "fatal: bad revision 'HEAD'");
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 128, string.Empty, "fatal: bad revision 'HEAD'");
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-5", runDir, Folder("repo-01")));

        AssertEx.True(export.Failed, "a non-zero git diff exit must be reported as a failure, not a clean zero-change run");
        AssertEx.Equal(expected: 0, export.ChangedFileCount);
        AssertEx.True(export.PatchRelativePath is null, "a failed export writes no patch");
        AssertEx.True(export.ChangedFilesRelativePath is null, "a failed export writes no metadata");
        AssertEx.False(Directory.Exists(Path.Combine(runDir, "patches")), "no artifacts are written on failure");
    }

    /// <summary>
    ///     Every export git is limited to the copied folders' own alias pathspecs.
    /// </summary>
    /// <remarks>
    ///     A whole-repository <c>.</c> is what let a file written at the workspace root into <c>changes.patch</c> and
    ///     the line totals while the alias map dropped it from <c>changed-files.json</c>: the counts disagreed and the
    ///     host apply then refused the whole patch over the one alias-less block.
    /// </remarks>
    [Test]
    public async Task ExportPatchAsync_ScopesStagingAndBothDiffsToTheCopiedFolders()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01", "repo-02"), exitCode: 0, NameStatusZ("M", "repo-01/src/App.cs"));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01", "repo-02"), exitCode: 0, "patch-body\n");
        var service = CreateService(provider);

        var export = await service.ExportPatchAsync(handle, Request("run-scope", NewTempDir(), Folder("repo-01"), Folder("repo-02")));

        AssertEx.Equal(expected: 1, export.ChangedFileCount, "the scripted output is only reached when the scoped command line matches");

        var gitCommands = provider.ExecutedCommands.Where(command => command.Executable == "git").ToArray();
        var scoped = gitCommands.Where(command => command.Arguments.Contains("add")
                                                  || command.Arguments.Contains("--binary")
                                                  || command.Arguments.Contains("--name-status"))
                                .ToArray();

        AssertEx.Equal(expected: 3, scoped.Length, "staging and both diffs are the three commands that must be scoped");
        foreach (var command in scoped)
        {
            var tail = command.Arguments.SkipWhile(static argument => argument != "--").Skip(count: 1).ToArray();
            AssertEx.Equal(":(literal)repo-01,:(literal)repo-02", string.Join(",", tail),
                "the pathspec tail is the copied aliases, in literal form so an alias can carry pathspec magic harmlessly");
            AssertEx.False(command.Arguments.Contains("."),
                "a whole-repository '.' pathspec would put a root-level write back into the patch the counts do not describe");
        }
    }

    /// <summary>
    ///     No copied folder means no alias directory to diff and no baseline commit to diff against, so the export
    ///     reports an empty run rather than falling back to the whole repository.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_WithNoCopiedFolder_ReportsNothingAndRunsNoGit()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider);

        ResolvedSelectedFolder[] noFolders = [];
        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-nofolders", runDir, [], noFolders));

        AssertEx.Equal(expected: 0, export.ChangedFileCount);
        AssertEx.False(export.Failed, "an empty selection is a clean zero-change run, not an export failure");
        AssertEx.True(export.PatchRelativePath is null, "no folders means no patch path");
        AssertEx.False(Directory.Exists(Path.Combine(runDir, "patches")), "no artifacts are written");
        AssertEx.False(provider.ExecutedCommands.Any(command => command.Executable == "git"),
            "with nothing to scope to, no git runs at all");
    }

    /// <summary>
    ///     Reachable now only for a stream the node did not get from its own scoped git — the pathspecs keep a
    ///     root-level or foreign-alias path out of the diff — so the mapping keeps its defensive skip.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_SkipsEntriesWithUnknownAliasOrNoAliasSegment()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());

        var nameStatus = NameStatusZ("M", "repo-01/keep.cs", // mapped
            "M", "unknown-alias/skip.cs", // alias not in the prepared workspace → skipped
            "M", "rootfile.txt"); // no alias segment → skipped
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, nameStatus);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "diff --git a/repo-01/keep.cs b/repo-01/keep.cs\n");
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-4", runDir, Folder("repo-01")));

        AssertEx.Equal(expected: 1, export.ChangedFileCount);
        var entries = JsonSerializer.Deserialize<ChangedFileEntry[]>(await File.ReadAllTextAsync(Path.Combine(runDir, "patches", "changed-files.json")),
            JsonOptions)!;
        AssertEx.Equal(expected: 1, entries.Length);
        AssertEx.Equal("keep.cs", entries[0].RelativePath);
    }

    /// <summary>
    ///     The totals come from the patch text itself, so one walk must handle every block shape git emits: hunks, a
    ///     pure rename, a binary block, an empty creation, a deletion and the <c>\ No newline</c> marker.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_TotalsOnlyTheLinesInsideHunks()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());

        var nameStatus = NameStatusZ("M", "repo-01/src/App.cs",
            "R100", "repo-01/old.txt", "repo-01/new.txt",
            "A", "repo-01/data.bin",
            "A", "repo-01/empty.txt",
            "D", "repo-01/gone.txt");
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, nameStatus);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, MixedShapePatch);
        var service = CreateService(provider);

        var export = await service.ExportPatchAsync(handle, Request("run-lines", NewTempDir(), Folder("repo-01")));

        AssertEx.Equal(expected: 2, export.LinesAdded, "only the two '+' lines inside the one hunk count as additions");
        AssertEx.Equal(expected: 3, export.LinesRemoved, "one removal in the modified file and both lines of the deleted one");
    }

    [Test]
    public async Task ExportPatchAsync_WhenEveryWrittenPathIsInTheDiff_ReportsNoGapAndRunsNoExtraGit()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"),
            exitCode: 0,
            NameStatusZ("M", "repo-01/src/App.cs", "R100", "repo-01/old.txt", "repo-01/new.txt"));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "patch-body\n");
        var service = CreateService(provider);

        var export = await service.ExportPatchAsync(handle,
            Request("run-nogap", NewTempDir(), ["repo-01/src/App.cs", "repo-01/new.txt", "repo-01/old.txt"], Folder("repo-01")));

        AssertEx.Equal(expected: 0, export.WrittenGap.Total, "every written path is named by the diff, the rename's source included");
        AssertEx.False(provider.ExecutedCommands.Any(command => command.Arguments.Contains("check-ignore")),
            "a run whose writes all reached the patch pays for no classification commands");
    }

    /// <summary>
    ///     The four reasons, in one export: an ignored path, one deleted after the write, one whose content matches
    ///     the baseline, and two the node cannot account for — an unstaged file and one hidden behind
    ///     <c>assume-unchanged</c>.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_ClassifiesEveryWrittenPathTheDiffDoesNotCarry()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, NameStatusZ("M", "repo-01/kept.cs"));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "patch-body\n");
        provider.RegisterCommand(GitDiffCommandKeys.CheckIgnore, exitCode: 0, "repo-01/hidden.txt\0");
        provider.RegisterCommand(GitDiffCommandKeys.LsFiles(MissingPaths),
            exitCode: 0,
            "H repo-01/same.cs\0? repo-01/unstaged.txt\0h repo-01/assumed.cs\0");
        var service = CreateService(provider);

        var logger = new NoOpAgentHomeRunLogger();
        var export = await service.ExportPatchAsync(handle,
            Request("run-gap", NewTempDir(), ["repo-01/kept.cs", .. MissingPaths], Folder("repo-01"), logger));

        AssertEx.Equal(expected: 1, export.WrittenGap.IgnoredCount);
        AssertEx.Equal(expected: 1, export.WrittenGap.DeletedCount, "a path git names neither in the index nor on disk is gone");
        AssertEx.Equal(expected: 1, export.WrittenGap.UnchangedCount);
        AssertEx.Equal(expected: 2, export.WrittenGap.UnexplainedCount,
            "an unstaged path and an assume-unchanged one are both absences the node cannot vouch for");

        var gapEvents = logger.Events.Where(entry => entry.EventName == "written_not_exported").ToList();
        AssertEx.Equal(expected: 1, gapEvents.Count, "the run's own log records the gap exactly once");

        var (_, gapDetail, gapData) = gapEvents[0];
        AssertEx.Equal("total=5;ignored=1;deleted=1;unchanged=1;unexplained=2", gapDetail);

        var buckets = AssertEx.NotNull(gapData as IReadOnlyDictionary<string, List<string>>, "the event carries the paths per reason");
        AssertEx.Contains(buckets["ignored"], "repo-01/hidden.txt");
        AssertEx.Contains(buckets["deleted"], "repo-01/gone.tmp");
        AssertEx.Contains(buckets["unexplained"], "repo-01/assumed.cs");
    }

    /// <summary>
    ///     The classification is best-effort but never optimistic: when git cannot say which paths are ignored, every
    ///     missing path is unexplained and the export still succeeds.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_WhenTheIgnoreQueryFails_CallsEveryMissingPathUnexplained()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, NameStatusZ("M", "repo-01/kept.cs"));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "patch-body\n");
        provider.RegisterCommand(GitDiffCommandKeys.CheckIgnore, exitCode: 128, string.Empty, "fatal: pathspec is in submodule");
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle,
            Request("run-degrade", runDir, ["repo-01/kept.cs", "repo-01/a.txt", "repo-01/b.txt"], Folder("repo-01")));

        AssertEx.Equal(expected: 2, export.WrittenGap.UnexplainedCount);
        AssertEx.Equal(expected: 0, export.WrittenGap.IgnoredCount);
        AssertEx.Equal(expected: 1, export.ChangedFileCount, "a classification that failed must not fail the export");
        AssertEx.True(File.Exists(Path.Combine(runDir, "patches", "changes.patch")), "the patch is still written");
    }

    /// <summary>
    ///     Such a path has to reach <c>TryMapEntry</c> whole and unquoted: C-quoted or cut in half by a tab split
    ///     it fails its alias parse, and a genuinely changed file goes missing from the run's review surface.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_KeepsChangedPathsGitWouldCQuote()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());

        string[] modifications = [.. QuotablePaths.SelectMany(static entry => new[] { "M", entry.WorkspacePath })];
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"),
            exitCode: 0,
            NameStatusZ([.. modifications, "R100", "repo-01/plain.txt", "repo-01/re\"named\".txt"]));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "patch-body\n");
        var service = CreateService(provider);

        var repo01 = Folder("repo-01");
        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-quoted", runDir, repo01));

        AssertEx.Equal(QuotablePaths.Length + 1, export.ChangedFileCount, "no quotable path is dropped from the export");

        var entries = JsonSerializer.Deserialize<ChangedFileEntry[]>(await File.ReadAllTextAsync(Path.Combine(runDir, "patches", "changed-files.json")),
            JsonOptions)!;

        foreach (var (_, relativePath) in QuotablePaths)
        {
            AssertEntry(entries, repo01.Id.ToString(), "repo-01", relativePath, "modified");
        }

        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "re\"named\".txt", "renamed");
    }

    /// <summary>
    ///     <c>T</c> and <c>U</c> share the two-token shape of <c>M</c>/<c>A</c>/<c>D</c>, so the change type each
    ///     maps to is pinned rather than inferred from the branch they share.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_MapsTypechangeAndUnmergedRecords()
    {
        var repo01 = Folder("repo-01");

        var entries = await ExportChangedFilesAsync("run-tu",
            NameStatusZ("T", "repo-01/became-a-link", "U", "repo-01/conflicted.cs", "M", "repo-01/plain.cs"),
            repo01);

        AssertEx.Equal(expected: 3, entries.Length);
        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "became-a-link", "typechanged");
        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "conflicted.cs", "unmerged");
        AssertEntry(entries, repo01.Id.ToString(), "repo-01", "plain.cs", "modified");
    }

    /// <summary>
    ///     A cut-off stream must not throw and must not consume the tokens of the records that did arrive. Both
    ///     shapes are pinned at the END of the stream, the only place a cut lands.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_WithATruncatedNameStatusTail_KeepsTheWholeRecordsAndDropsTheRest()
    {
        var repo01 = Folder("repo-01");

        var danglingStatus = await ExportChangedFilesAsync("run-trunc-status",
            NameStatusZ("M", "repo-01/good.cs") + "D",
            repo01);

        AssertEx.Equal(expected: 1, danglingStatus.Length, "a status with no path names no file, and takes no other record's path");
        AssertEntry(danglingStatus, repo01.Id.ToString(), "repo-01", "good.cs", "modified");

        var renameWithoutDestination = await ExportChangedFilesAsync("run-trunc-rename",
            NameStatusZ("M", "repo-01/good.cs", "R100", "repo-01/only.txt"),
            repo01);

        AssertEx.Equal(expected: 1, renameWithoutDestination.Length, "a rename missing its destination is dropped, not paired with the record before it");
        AssertEntry(renameWithoutDestination, repo01.Id.ToString(), "repo-01", "good.cs", "modified");
    }

    /// <summary>
    ///     The reconciliation reads the same stream: a quotable written path the diff DOES name has to be found in
    ///     the exported set, or <c>ls-files</c> labels it "unchanged" — a false gap over a file the patch carries.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_WhenAQuotableWrittenPathIsInTheDiff_ReportsNoGap()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());

        var written = QuotablePaths[0].WorkspacePath;
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, NameStatusZ("M", written));
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, "patch-body\n");
        var service = CreateService(provider);

        var export = await service.ExportPatchAsync(handle, Request("run-quoted-gap", NewTempDir(), [written], Folder("repo-01")));

        AssertEx.Equal(expected: 0, export.WrittenGap.Total, "the diff names this path, so it is exported and not a gap");
        AssertEx.Equal(expected: 0, export.WrittenGap.UnchangedCount, "a real export must never be reported as an unchanged file");
        AssertEx.False(provider.ExecutedCommands.Any(command => command.Arguments.Contains("ls-files")),
            "a run with no gap pays for no classification commands");
    }

    /// <summary>
    ///     The loudest gap: the run wrote files and the diff reports nothing at all. The export returns early there,
    ///     so the reconciliation has to run before it or the whole defect class stays silent again.
    /// </summary>
    [Test]
    public async Task ExportPatchAsync_WhenTheRunWroteFilesAndTheDiffIsEmpty_StillReportsTheGap()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus("repo-01"), exitCode: 0, string.Empty);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff("repo-01"), exitCode: 0, string.Empty);
        provider.RegisterCommand(GitDiffCommandKeys.CheckIgnore, exitCode: 1, string.Empty);
        provider.RegisterCommand(GitDiffCommandKeys.LsFiles("repo-01/written.cs"), exitCode: 0, "? repo-01/written.cs\0");
        var service = CreateService(provider);

        var export = await service.ExportPatchAsync(handle,
            Request("run-empty-diff", NewTempDir(), ["repo-01/written.cs"], Folder("repo-01")));

        AssertEx.Equal(expected: 0, export.ChangedFileCount);
        AssertEx.Equal(expected: 1, export.WrittenGap.UnexplainedCount,
            "a write that reached neither the index nor the diff is exactly the silence this reconciliation ends");
    }

    /// <summary>
    ///     The NUL-terminated stream <c>git diff --name-status -z</c> emits: every field its own token, each one
    ///     closed by a NUL. Status and path are separate tokens — under <c>-z</c> there is no tab between them.
    /// </summary>
    private static string NameStatusZ(params string[] fields)
    {
        return string.Concat(fields.Select(static field => field + '\0'));
    }

    /// <summary>Runs one export over a scripted name-status stream and reads back the entries it wrote.</summary>
    private async Task<ChangedFileEntry[]> ExportChangedFilesAsync(string runId, string nameStatusOutput, ResolvedSelectedFolder folder)
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus(folder.Alias), exitCode: 0, nameStatusOutput);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff(folder.Alias), exitCode: 0, "patch-body\n");

        var runDir = NewTempDir();
        await CreateService(provider).ExportPatchAsync(handle, Request(runId, runDir, folder));

        return JsonSerializer.Deserialize<ChangedFileEntry[]>(await File.ReadAllTextAsync(Path.Combine(runDir, "patches", "changed-files.json")),
            JsonOptions)!;
    }

    private static void AssertEntry(ChangedFileEntry[] entries, string folderId, string alias, string relativePath, string changeType)
    {
        var entry = entries.Single(candidate => candidate.RelativePath == relativePath);
        AssertEx.Equal(folderId, entry.SelectedFolderId);
        AssertEx.Equal(alias, entry.Alias);
        AssertEx.Equal(changeType, entry.ChangeType);
    }

    /// <summary>
    ///     <c>core.fsmonitor=</c> is pinned for security rather than byte stability — git executes a configured
    ///     <c>core.fsmonitor</c> value as a shell command on index refresh — but it belongs in the same assertion
    ///     because the flag set must stay identical between baseline creation and diff.
    /// </summary>
    private static bool HasHardenedFlags(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("core.hooksPath=/dev/null")
               && arguments.Contains("core.attributesfile=/dev/null")
               && arguments.Contains("core.fsmonitor=");
    }

    private static AgentHomePatchService CreateService(FakeSandboxRuntimeProvider provider, long maxPatchBytes = 52428800)
    {
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithAgentHomeMaxPatchBytes(maxPatchBytes)
                                                     .Build();
        return new AgentHomePatchService(provider, runtimeSettings, TimeProvider.System, NullLogger<AgentHomePatchService>.Instance);
    }

    private static AgentHomePatchExportRequest Request(string runId, string hostRunDirectory, params ResolvedSelectedFolder[] folders)
    {
        return Request(runId, hostRunDirectory, [], folders);
    }

    private static AgentHomePatchExportRequest Request(string runId,
        string hostRunDirectory,
        IReadOnlyList<string> writtenFiles,
        ResolvedSelectedFolder folder,
        NoOpAgentHomeRunLogger? runLogger = null)
    {
        return Request(runId, hostRunDirectory, writtenFiles, [folder], runLogger);
    }

    private static AgentHomePatchExportRequest Request(string runId,
        string hostRunDirectory,
        IReadOnlyList<string> writtenFiles,
        ResolvedSelectedFolder[] folders,
        NoOpAgentHomeRunLogger? runLogger = null)
    {
        return new AgentHomePatchExportRequest
        {
            RunLogger = runLogger ?? new NoOpAgentHomeRunLogger(),
            RunId = runId,
            HostRunDirectory = hostRunDirectory,
            ResolvedFolders = folders,
            WrittenFiles = writtenFiles
        };
    }

    private static ResolvedSelectedFolder Folder(string alias)
    {
        return new ResolvedSelectedFolder { Id = Guid.NewGuid(), Alias = alias, HostPath = "/host/" + alias, Mode = SelectedFolderMode.Copy };
    }

    private static SandboxCreateRequest CreateRequest()
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = "fake",
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = AgentHomeManifest.CurrentVersion
            },
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.None
        };
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenthome-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
