namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Implementation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using CategoryAttribute = TUnit.Core.CategoryAttribute;

/// <summary>
///     The patch export's git is the node's own, and it runs over the workspace the model just had <c>write_file</c>
///     and <c>run_command</c> access to, AFTER the model's turn has ended and outside the run's budgets. Git executes
///     programs named by configuration, so without a guard that is host code execution the operator never approved and
///     never sees.
///     <para>
///         This class is the negative control for that, end to end on the REAL process jail with REAL git: a scripted
///         inner run plants each payload shape the way a model actually could — its own <c>run_command</c> for the
///         config, its own <c>write_file</c> for the <c>.gitattributes</c> — and every payload would create a MARKER
///         FILE OUTSIDE the workspace. The assertion is that no marker exists after export.
///     </para>
///     <para>
///         Each payload was verified to EXECUTE against the pre-guard argument vector on git 2.53.0, so a green run
///         here is a guard working rather than a payload that never fired. The deliberate-break proof re-establishes
///         that on demand.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentHomePatchExportGitHardeningTests : IDisposable
{
    private const string Model = "qwen3:8b";
    private const string WorkspaceAlias = "selected-project";

    /// <summary>The alias of a selected folder with nothing to copy, so the workspace holds no directory for it.</summary>
    private const string EmptyAlias = "selected-empty";

    private static readonly JsonSerializerOptions ChangedFilesJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
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

    /// <summary>
    ///     The four payloads that define a program in the repository's OWN configuration and select it from the tree.
    ///     All four executed against the pre-guard vector; all four must be inert now, and the export must still
    ///     produce the model's real edit, because a guard that broke the diff would be traded for an outage.
    /// </summary>
    [Test]
    [Arguments("textconv", "diff.pwn.textconv", "* diff=pwn", "a repository-local textconv driver selected by an in-tree .gitattributes")]
    [Arguments("clean", "filter.pwn.clean", "* filter=pwn", "a repository-local clean filter selected by an in-tree .gitattributes")]
    [Arguments("external", "diff.pwn.command", "* diff=pwn", "a repository-local external diff command")]
    // fsmonitor is additionally covered by AgentHomeGit's own -c pin; it stays here as the regression control for the
    // key that was already known to be live, so removing either control shows up.
    [Arguments("fsmonitor", "core.fsmonitor", "", "a repository-local fsmonitor hook that runs on any index refresh")]
    public async Task Export_WhenTheModelPlantedAGitConfigProgram_NeverRunsIt_AndStillExportsTheRealEdit(string caseName,
        string configKey,
        string attributes,
        string because)
    {
        SkipUnlessRealGitAndProcessJail();

        var marker = MarkerRelativePath(caseName);
        var payload = PayloadCommand(marker);

        using var fixture = CreateFixture();
        var script = new List<(string, Dictionary<string, object?>)>
        {
            // The model's own command sets the repository-local key — indistinguishable from a legitimate
            // `git config` call in a `commands.jsonl` an operator reads.
            ("run_command", new()
            {
                ["executable"] = "git",
                ["arguments"] = new[] { "config", configKey, payload }
            }),
            // …and a real edit, so the export has something to find and the test can prove the diff still works.
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" })
        };

        if (attributes.Length > 0)
        {
            // .gitattributes is NOT under .git, so the write guard does not refuse it. That is the point.
            script.Insert(index: 1, ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitattributes", ["content"] = attributes + "\n" }));
        }

        var run = await fixture.RunAsync([.. script]);

        AssertEx.False(await MarkerExistsAsync(fixture.Provider, marker), $"the export ran {because}; the payload must never execute");
        AssertEx.Equal(expected: 1, run.Patch.ChangedFileCount > 0 ? 1 : 0,
            $"the guard must not break the diff — export reported {run.Patch.ChangedFileCount} changed file(s)");
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     The shape that defeats a configuration rewrite outright: <c>.git</c> replaced by a GITFILE pointing at a
    ///     directory the model owns. The rewrite finds no directory, and a naive guard returns silently while git
    ///     happily reads the model's configuration from wherever the pointer leads. Verified executing against a guard
    ///     that only rewrote an existing <c>.git/config</c>.
    /// </summary>
    [Test]
    public async Task Export_WhenTheModelReplacedTheGitDirectoryWithAGitfile_RefusesToExportAtAll()
    {
        SkipUnlessRealGitAndProcessJail();

        var marker = MarkerRelativePath("gitfile");
        var payload = PayloadCommand(marker);

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitattributes", ["content"] = "* diff=pwn\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }),
            ("run_command", new()
            {
                ["executable"] = "/bin/sh",
                ["arguments"] = new[]
                {
                    "-c",
                    // Copy the real repository aside, point config at the payload, then leave a gitfile behind.
                    $"cp -r .git ../evil-git && printf '[core]\\n\\trepositoryformatversion = 0\\n[diff \"pwn\"]\\n\\ttextconv = {payload}\\n' > ../evil-git/config"
                    + " && rm -rf .git && printf 'gitdir: %s\\n' \"$(cd ../evil-git && pwd)\" > .git"
                }
            }));

        AssertEx.False(await MarkerExistsAsync(fixture.Provider, marker), "a gitfile pointing at a model-owned git directory must not get the payload run");

        // Fail CLOSED, and say so: exporting from a repository the model replaced would be exporting a patch the node
        // cannot vouch for, and silently reporting "no changes" would be worse than reporting nothing.
        AssertEx.True(run.Patch.Failed, "the export must refuse a workspace whose git directory is not the one the baseline created");
        AssertEx.Equal(expected: 0, run.Patch.ChangedFileCount);
        AssertEx.True(run.Patch.PatchRelativePath is null, "a refused export writes no patch");
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     The GLOBAL configuration file is reachable too — the sandbox forwards <c>HOME</c>, and
    ///     <c>run_command</c> is not confined to the workspace, so the model can write <c>$HOME/.gitconfig</c>. Both
    ///     the home-directory and the XDG form were verified executing without the environment guard, which is why the
    ///     repository-config rewrite alone is not the whole fix.
    /// </summary>
    /// <remarks>
    ///     <c>HOME</c> is moved on the TEST HOST for the duration, because that is the variable the sandbox provider
    ///     forwards and therefore the only way to exercise the real path — writing the developer's own
    ///     <c>~/.gitconfig</c> is not an option. It is process-global, hence <c>[NotInParallel]</c>, and it is restored
    ///     in a <c>finally</c>.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task Export_WhenAGlobalGitConfigDefinesADriver_NeverRunsIt()
    {
        SkipUnlessRealGitAndProcessJail();

        var marker = MarkerRelativePath("global");
        var payload = PayloadCommand(marker);

        var home = CreateTempDirectory("xe-ah-home");
        await File.WriteAllTextAsync(Path.Combine(home, ".gitconfig"), $"[diff \"pwn\"]\n\ttextconv = {payload}\n");

        var previousHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);

            using var fixture = CreateFixture();
            var run = await fixture.RunAsync(
                ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitattributes", ["content"] = "* diff=pwn\n" }),
                ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }));

            AssertEx.False(await MarkerExistsAsync(fixture.Provider, marker), "a driver defined in the GLOBAL git config must not execute during export");
            AssertEx.True(run.Patch.ChangedFileCount > 0, "the guard must not break the diff");
            await AssertHostFolderUnchangedAsync(fixture);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
        }
    }

    /// <summary>
    ///     The node's git runs in the same sandbox over the same workspace as the model's commands, so a
    ///     <c>commands.jsonl</c> showing only the model's half would hide the invocations this class protects.
    /// </summary>
    /// <remarks>
    ///     The whole sequence is graded: the workspace-copy baseline's three git commands run during PREPARE, before
    ///     a run id or a log exists, and are flushed the moment the log opens; then the model's turn; then the
    ///     export's two diffs. Graded on the SERIALIZED file, not the records the logger was handed — those diverged
    ///     once when the envelope never listed the actor, leaving every field an operator reads right, the
    ///     attribution absent and the in-memory assertion green.
    /// </remarks>
    [Test]
    public async Task Export_WritesItsOwnGitCommandsToTheRunLog_AttributedToTheNode()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("run_command", new() { ["executable"] = "/bin/echo", ["arguments"] = new[] { "hello" } }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }));

        var logged = (await File.ReadAllLinesAsync(Path.Combine(run.LogPath, "commands.jsonl")))
                     .Where(line => !string.IsNullOrWhiteSpace(line))
                     .Select(line => JsonDocument.Parse(line).RootElement)
                     .ToList();

        var expectedActors = string.Join(separator: ',',
            AgentHomeCommandActors.Node,
            AgentHomeCommandActors.Node,
            AgentHomeCommandActors.Node,
            AgentHomeCommandActors.Model,
            AgentHomeCommandActors.Node,
            AgentHomeCommandActors.Node,
            AgentHomeCommandActors.Node);
        var actors = string.Join(separator: ',', logged.Select(record => record.GetProperty("actor").GetString()));
        AssertEx.Equal(expectedActors, actors,
            "commands.jsonl records the baseline's three, then the model's own command, then all three of the export's, each attributed to who ran it");

        // The execution ids say WHICH node git ran, in order: a bare actor sequence would still pass if the flush
        // duplicated the export's two records instead of carrying the baseline's.
        var executionIds = logged.Select(record => record.GetProperty("executionId").GetString()).ToList();
        AssertEx.Equal("agent-home-baseline-init", executionIds[0]);
        AssertEx.Equal("agent-home-baseline-add", executionIds[1]);
        AssertEx.Equal("agent-home-baseline-commit", executionIds[2]);
        AssertEx.Equal($"{run.RunId}-patch-stage", executionIds[4]);
        AssertEx.Equal($"{run.RunId}-patch-diff", executionIds[5]);
        AssertEx.Equal($"{run.RunId}-patch-status", executionIds[6]);

        var nodeCommands = logged.Where(record => string.Equals(record.GetProperty("actor").GetString(), AgentHomeCommandActors.Node, StringComparison.Ordinal)).ToList();
        AssertEx.True(nodeCommands.TrueForAll(record => string.Equals(record.GetProperty("executable").GetString(), "git", StringComparison.Ordinal)),
            "every command the node ran itself is git — the baseline's and the export's alike");
        AssertEx.True(nodeCommands.TrueForAll(record => record.GetProperty("arguments").EnumerateArray().Any(argument => string.Equals(argument.GetString(), "core.autocrlf=false", StringComparison.Ordinal))),
            "the logged argument vector is the one that really ran, including the byte-stabilizing pins the baseline and the diff must share");

        // The run log is an audit of what the node RAN, never of what the workspace said back: no record carries
        // captured output, and none may grow one.
        AssertEx.True(logged.TrueForAll(static record => !record.TryGetProperty("standardOutput", out _) && !record.TryGetProperty("standardError", out _)),
            "a command record carries argv, exit code and duration — never the bytes the command produced");
    }

    /// <summary>
    ///     <c>git diff HEAD</c> does not see an UNTRACKED path, so every file a run CREATED was silently absent from
    ///     both artifacts. All four change kinds are graded in ONE run: a fix that restored creations by losing
    ///     deletions or renames is not a fix.
    /// </summary>
    [Test]
    public async Task Export_WhenTheRunCreatedModifiedDeletedAndRenamedFiles_ReportsAllFourWithTheirChangeTypes()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/docs/notes.md", ["content"] = "# notes\ncreated by the run\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }),
            ("run_command", new()
            {
                ["executable"] = "/bin/sh",
                ["arguments"] = new[] { "-c", $"rm {WorkspaceAlias}/notes.txt && mv {WorkspaceAlias}/guide.txt {WorkspaceAlias}/manual.txt" }
            }));

        var changed = await ReadChangedFilesAsync(run);
        AssertChange(changed, "docs/notes.md", "added");
        AssertChange(changed, "README.md", "modified");
        AssertChange(changed, "notes.txt", "deleted");
        AssertChange(changed, "manual.txt", "renamed");

        var patch = await ReadPatchAsync(run);
        AssertEx.Contains(patch, $"+++ b/{WorkspaceAlias}/docs/notes.md");
        AssertEx.Contains(patch, "created by the run");
        AssertEx.Equal(expected: 4, run.Patch.ChangedFileCount);
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     Two creations a content diff alone would drop: a binary file, which needs the <c>--binary</c> payload, and
    ///     an empty one, which has no content at all and survives only as a mode line.
    /// </summary>
    [Test]
    public async Task Export_WhenTheRunCreatedBinaryAndEmptyFiles_ReportsBoth()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(("run_command", new()
        {
            ["executable"] = "/bin/sh",
            ["arguments"] = new[] { "-c", $"head -c 4 /dev/zero > {WorkspaceAlias}/data.bin && : > {WorkspaceAlias}/empty.txt" }
        }));

        var changed = await ReadChangedFilesAsync(run);
        AssertChange(changed, "data.bin", "added");
        AssertChange(changed, "empty.txt", "added");

        var patch = await ReadPatchAsync(run);
        AssertEx.Contains(patch, "GIT binary patch");
        AssertEx.Contains(patch, $"b/{WorkspaceAlias}/empty.txt");
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     <c>run_command</c> has no allow-list, so a run can still put a file at the workspace ROOT, outside every
    ///     copied folder.
    /// </summary>
    /// <remarks>
    ///     With the diff scoped to the alias pathspecs that file reaches neither artifact, so the file count, the
    ///     line totals and the patch bytes describe one set — the disagreement that made the host apply refuse the
    ///     whole patch over a single alias-less block.
    /// </remarks>
    [Test]
    public async Task Export_WhenTheRunWroteAFileAtTheWorkspaceRoot_LeavesItOutOfBothArtifacts()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("run_command", new()
            {
                ["executable"] = "/bin/sh",
                ["arguments"] = new[] { "-c", "printf 'stray\\n' > rootfile.txt" }
            }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/docs/notes.md", ["content"] = "# notes\n" }));

        var changed = await ReadChangedFilesAsync(run);
        AssertChange(changed, "docs/notes.md", "added");
        AssertEx.Equal(expected: 1, changed.Count, "the root-level file is not a change inside any reviewed folder");
        AssertEx.Equal(changed.Count, run.Patch.ChangedFileCount, "the reported count is the list, with nothing filtered away after it");

        var patch = await ReadPatchAsync(run);
        AssertEx.False(patch.Contains("rootfile.txt", StringComparison.Ordinal), "the patch text never carries the alias-less path");
        AssertEx.False(patch.Contains("stray", StringComparison.Ordinal), "nor its content");
        AssertEx.Equal(expected: 1, run.Patch.LinesAdded, "the line totals count the reviewed hunk only, so they agree with the file count");

        // The file really is there — the export left it alone rather than the command having failed.
        AssertEx.True(await MarkerExistsAsync(fixture.Provider, "rootfile.txt"), "the run did write at the workspace root");
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     A selection may hold a folder that resolved but copied nothing, and that folder has no directory in the
    ///     sandbox at all.
    /// </summary>
    /// <remarks>
    ///     <c>git add -A</c> exits 128 the moment ONE of its pathspecs matches neither the index nor the working
    ///     tree, so naming such an alias would fail the export for the whole run and throw away the other folder's
    ///     real changes. Only real git shows that, which is why this lives here rather than on the scripted seam.
    /// </remarks>
    [Test]
    public async Task Export_WhenASelectedFolderCopiedNothing_StillExportsTheOtherFoldersChanges()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture(emptyAlias: EmptyAlias);
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/docs/notes.md", ["content"] = "# notes\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }));

        AssertEx.False(run.Patch.Failed,
            "an alias with no directory must never reach a pathspec — git add -A would exit 128 and refuse the whole export");

        var changed = await ReadChangedFilesAsync(run);
        AssertChange(changed, "docs/notes.md", "added");
        AssertChange(changed, "README.md", "modified");
        AssertEx.Equal(expected: 2, changed.Count, "only the folder that copied contributes changes");
        AssertEx.True(changed.TrueForAll(static entry => entry.Alias != EmptyAlias), "the empty folder names no change");

        // commands.jsonl is the argv that really ran, so it is where an alias that leaked into a pathspec would show.
        var logged = await ReadCommandsAsync(run);
        AssertEx.True(logged.TrueForAll(static record => record.GetProperty("arguments")
                                                               .EnumerateArray()
                                                               .All(static argument => argument.GetString()?.Contains(EmptyAlias, StringComparison.Ordinal) != true)),
            "no git the node ran names the folder that copied nothing");

        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     Staging honours <c>.gitignore</c> because the BASELINE's own <c>add -A</c> did: forcing here would report
    ///     every ignored-but-copied file as one the run added. A model can hide its own work; it cannot reach the host.
    /// </summary>
    [Test]
    public async Task Export_WhenAModelWrittenGitignoreHidesACreatedFile_LeavesItOutAndStillExportsTheRest()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitignore", ["content"] = "hidden.txt\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/hidden.txt", ["content"] = "invisible\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/visible.txt", ["content"] = "visible\n" }));

        var changed = await ReadChangedFilesAsync(run);
        AssertChange(changed, "visible.txt", "added");
        AssertChange(changed, ".gitignore", "added");
        AssertEx.True(changed.TrueForAll(entry => entry.RelativePath != "hidden.txt"),
            "an ignored path stays out of the export, exactly as it stayed out of the baseline");

        var patch = await ReadPatchAsync(run);
        AssertEx.False(patch.Contains("invisible", StringComparison.Ordinal), "the ignored file's content is not in the patch either");
    }

    /// <summary>
    ///     Staging makes the patch bigger, so the over-budget path carries more traffic than it did: it must still be
    ///     honest — metadata written, oversized patch withheld, and the header's <c>patch=none</c> earned.
    /// </summary>
    [Test]
    public async Task Export_WhenTheStagedPatchIsOverBudget_KeepsMetadataAndWritesNoPatch()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture(maxPatchBytes: 64);
        var run = await fixture.RunAsync(("write_file",
            new() { ["path"] = $"{WorkspaceAlias}/docs/notes.md", ["content"] = new string(c: 'n', count: 4096) + "\n" }));

        AssertEx.True(run.Patch.Blocked, "a patch over MaxPatchBytes is blocked");
        AssertEx.True(run.Patch.PatchRelativePath is null, "a blocked patch is not written");
        var changed = await ReadChangedFilesAsync(run);
        AssertChange(changed, "docs/notes.md", "added");
        AssertEx.False(File.Exists(Path.Combine(PatchesDirectory(run), "changes.patch")), "the oversized patch must not be written");
    }

    /// <summary>
    ///     The property that matters to the operator: a created file survives export, preview and apply, and lands on
    ///     the host with identical bytes. Applied to a SCRATCH folder, so the run's source folder stays untouched.
    /// </summary>
    [Test]
    public async Task ExportedPatch_PreviewsAndAppliesTheCreatedFileToTheHost()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        const string Created = "# notes\ncreated by the run\n";
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/docs/notes.md", ["content"] = Created }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }));

        var target = CreateTempDirectory("xe-ah-apply");
        foreach (var seeded in Directory.GetFiles(fixture.HostFolder))
        {
            File.Copy(seeded, Path.Combine(target, Path.GetFileName(seeded)));
        }

        var applyService = CreateApplyService(fixture, target);
        var preview = await applyService.PreviewAsync(new NodePatchApplyRequest { RunId = run.RunId });

        AssertEx.True(preview.CanApply, $"the exported patch checks clean. rejections: {string.Join(separator: ';', preview.Rejections)}");
        AssertEx.Contains(preview.Files, file => file is { Alias: WorkspaceAlias, RelativePath: "docs/notes.md", ChangeType: "added" });

        var result = await applyService.ApplyApprovedAsync(new NodePatchApplyRequest { RunId = run.RunId });

        AssertEx.True(result.Applied, $"the exported patch applies. rejections: {string.Join(separator: ';', result.Rejections)}");
        AssertEx.Equal(Created, await File.ReadAllTextAsync(Path.Combine(target, "docs", "notes.md")),
            "the created file lands on the host with the bytes the run wrote");
        await AssertHostFolderUnchangedAsync(fixture);
    }

    /// <summary>
    ///     <c>run_command</c> has no allow-list, so a run can <c>ln -s</c>. Staging carries the link in as
    ///     <c>mode 120000</c>, whose content is the TARGET, and applying it would point a real link on the
    ///     operator's folder anywhere. End to end: it must be refused.
    /// </summary>
    [Test]
    public async Task ExportedPatch_WithACreatedSymlink_IsRefusedAndLeavesNoLinkOnTheHost()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("run_command", new()
            {
                ["executable"] = "/bin/sh",
                ["arguments"] = new[]
                {
                    "-c",
                    $"ln -s /etc/passwd {WorkspaceAlias}/abs-link && ln -s ../../escape.txt {WorkspaceAlias}/rel-link"
                }
            }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/docs/notes.md", ["content"] = "# notes\n" }));

        // The export is honest about what the run did — it is the APPLY that refuses.
        var changed = await ReadChangedFilesAsync(run);
        AssertChange(changed, "abs-link", "added");
        AssertChange(changed, "rel-link", "added");

        var target = CreateTempDirectory("xe-ah-symlink-apply");
        foreach (var seeded in Directory.GetFiles(fixture.HostFolder))
        {
            File.Copy(seeded, Path.Combine(target, Path.GetFileName(seeded)));
        }

        var applyService = CreateApplyService(fixture, target);
        var preview = await applyService.PreviewAsync(new NodePatchApplyRequest { RunId = run.RunId });

        AssertEx.False(preview.CanApply, "a patch that creates a symbolic link must not be offered as applicable");
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("symbolic link", StringComparison.Ordinal));
        AssertEx.True(preview.Rejections.All(rejection => !rejection.Reason.Contains(target, StringComparison.Ordinal)
                                                          && !rejection.Reason.Contains(fixture.HostFolder, StringComparison.Ordinal)
                                                          && rejection.Path?.Contains(fixture.HostFolder, StringComparison.Ordinal) != true),
            "the rejection carries no host path");

        var result = await applyService.ApplyApprovedAsync(new NodePatchApplyRequest { RunId = run.RunId });

        AssertEx.False(result.Applied, "the apply refuses the whole patch");
        // Graded on the DIRECTORY LISTING, not on Path.Exists: a dangling link (rel-link points at nothing) is
        // absent from Path.Exists whether or not it was created, so that check could pass over a real escape.
        var entries = Directory.GetFileSystemEntries(target).Select(Path.GetFileName).ToArray();
        foreach (var name in new[] { "abs-link", "rel-link" })
        {
            AssertEx.False(entries.Contains(name, StringComparer.Ordinal), $"no '{name}' entry exists on the host");
        }

        AssertEx.True(new DirectoryInfo(target).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                                               .All(static info => info.LinkTarget is null),
            "nothing under the host folder is a link");

        AssertEx.False(Path.Exists(Path.Combine(target, "docs", "notes.md")),
            "one refused block refuses the whole patch — the legitimate creation does not land either");
    }

    /// <summary>
    ///     The sibling shape: a run that initializes a nested repository inside the workspace. Staging turns it into
    ///     a gitlink, and the apply must answer with the SUBMODULE refusal rather than an empty directory.
    /// </summary>
    [Test]
    public async Task ExportedPatch_WithANestedRepository_IsRefusedAsASubmodule()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(("run_command", new()
        {
            ["executable"] = "/bin/sh",
            ["arguments"] = new[]
            {
                "-c",
                $"mkdir -p {WorkspaceAlias}/vendor && cd {WorkspaceAlias}/vendor && git init -q ."
                + " && echo inner > file.txt && git add -A"
                + " && git -c user.email=t@example.invalid -c user.name=t commit -q -m inner"
            }
        }));

        AssertEx.True(run.Patch.ChangedFileCount > 0, "the nested repository reaches the export as a change");

        var target = CreateTempDirectory("xe-ah-nested-apply");
        foreach (var seeded in Directory.GetFiles(fixture.HostFolder))
        {
            File.Copy(seeded, Path.Combine(target, Path.GetFileName(seeded)));
        }

        var applyService = CreateApplyService(fixture, target);
        var preview = await applyService.PreviewAsync(new NodePatchApplyRequest { RunId = run.RunId });

        AssertEx.False(preview.CanApply, "a nested repository is not something the operator can apply");
        AssertEx.Contains(preview.Rejections, rejection => rejection.Reason.Contains("submodule", StringComparison.Ordinal));
        AssertEx.False(Path.Exists(Path.Combine(target, "vendor")), "no empty submodule directory is created on the host");
    }

    /// <summary>
    ///     The quiet case, and the one that must stay quiet: every path the loop recorded writing is named by the
    ///     diff, so there is no gap, no event and nothing extra for the node to run.
    /// </summary>
    [Test]
    public async Task Export_WhenEveryWriteReachesThePatch_ReportsNoGapAndLogsNoGapEvent()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/docs/notes.md", ["content"] = "# notes\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmall\n" }));

        AssertEx.Equal(expected: 0, run.Patch.WrittenGap.Total, "both writes are in the patch, so nothing is missing from it");
        AssertEx.True((await ReadEventsAsync(run)).TrueForAll(static record => record.GetProperty("eventName").GetString() != "written_not_exported"),
            "a run with nothing to report must not write a gap event");
    }

    /// <summary>
    ///     All four reasons a write can be missing from the patch, in ONE real-git run: each one is an assumption
    ///     about what git answers, not about the node's arithmetic.
    /// </summary>
    [Test]
    public async Task Export_WhenWritesAreMissingFromThePatch_ClassifiesEachOneAndRecordsThePathsInTheRunLog()
    {
        SkipUnlessRealGitAndProcessJail();

        using var fixture = CreateFixture();
        var run = await fixture.RunAsync(
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/.gitignore", ["content"] = "secret.txt\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/secret.txt", ["content"] = "hidden\n" }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/scratch.txt", ["content"] = "temporary\n" }),
            // Byte-identical to what the host folder was seeded with, so the baseline has nothing to diff against.
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/README.md", ["content"] = "# project\nsmal\n" }),
            ("run_command", new()
            {
                ["executable"] = "git",
                ["arguments"] = new[] { "update-index", "--skip-worktree", $"{WorkspaceAlias}/notes.txt" }
            }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/notes.txt", ["content"] = "notes rewritten by the run\n" }),
            ("run_command", new()
            {
                ["executable"] = "/bin/sh",
                ["arguments"] = new[] { "-c", $"rm {WorkspaceAlias}/scratch.txt" }
            }),
            ("write_file", new() { ["path"] = $"{WorkspaceAlias}/guide.txt", ["content"] = "rewritten\n" }));

        var gap = run.Patch.WrittenGap;
        AssertEx.Equal(expected: 1, gap.IgnoredCount, $"secret.txt is ignored. gap was {Describe(gap)}");
        AssertEx.Equal(expected: 1, gap.DeletedCount, $"scratch.txt was removed after the write. gap was {Describe(gap)}");
        AssertEx.Equal(expected: 1, gap.UnchangedCount, $"README.md was rewritten with the baseline's own bytes. gap was {Describe(gap)}");
        AssertEx.Equal(expected: 1, gap.UnexplainedCount,
            $"a skip-worktree edit is a real change the patch does not carry, and the node cannot explain it. gap was {Describe(gap)}");
        AssertEx.True(run.Patch.ChangedFileCount > 0, "the writes that did reach the patch are still exported");

        var gapEvents = (await ReadEventsAsync(run))
                        .Where(static record => record.GetProperty("eventName").GetString() == "written_not_exported")
                        .ToList();
        AssertEx.Equal(expected: 1, gapEvents.Count, "the run's own log carries exactly one reconciliation event");

        var gapEvent = gapEvents[0];
        AssertEx.Equal("total=4;ignored=1;deleted=1;unchanged=1;unexplained=1", gapEvent.GetProperty("detail").GetString());

        var data = gapEvent.GetProperty("data");
        AssertEx.Equal($"{WorkspaceAlias}/secret.txt", data.GetProperty("ignored")[0].GetString());
        AssertEx.Equal($"{WorkspaceAlias}/scratch.txt", data.GetProperty("deleted")[0].GetString());
        AssertEx.Equal($"{WorkspaceAlias}/README.md", data.GetProperty("unchanged")[0].GetString());
        AssertEx.Equal($"{WorkspaceAlias}/notes.txt", data.GetProperty("unexplained")[0].GetString());

        // The two classification commands are the node's own, and an audit must see them like every other one.
        var logged = await ReadCommandsAsync(run);
        foreach (var executionId in new[] { $"{run.RunId}-patch-check-ignore", $"{run.RunId}-patch-ls-files" })
        {
            var matching = logged.Where(candidate => candidate.GetProperty("executionId").GetString() == executionId).ToList();
            AssertEx.Equal(expected: 1, matching.Count, $"commands.jsonl records '{executionId}' exactly once");
            AssertEx.Equal(AgentHomeCommandActors.Node, matching[0].GetProperty("actor").GetString());
            AssertEx.True(!matching[0].TryGetProperty("standardOutput", out _) && !matching[0].TryGetProperty("standardError", out _),
                "a classification command is logged by its argv and exit code, never by what it printed");
        }
    }

    // ---------------------------------------------------------------- harness

    private static string Describe(AgentHomeWrittenFileGap gap)
    {
        return $"ignored={gap.IgnoredCount},deleted={gap.DeletedCount},unchanged={gap.UnchangedCount},unexplained={gap.UnexplainedCount}";
    }

    private static async Task<List<JsonElement>> ReadEventsAsync(AgentHomeRunResult run)
    {
        return await ReadJsonLinesAsync(Path.Combine(run.LogPath, "events.jsonl"));
    }

    private static async Task<List<JsonElement>> ReadCommandsAsync(AgentHomeRunResult run)
    {
        return await ReadJsonLinesAsync(Path.Combine(run.LogPath, "commands.jsonl"));
    }

    private static async Task<List<JsonElement>> ReadJsonLinesAsync(string path)
    {
        return [.. (await File.ReadAllLinesAsync(path))
                  .Where(static line => !string.IsNullOrWhiteSpace(line))
                  .Select(static line => JsonDocument.Parse(line).RootElement)];
    }

    /// <summary>
    ///     The whole loop needs REAL git in the jail and a POSIX shell for the payloads. Skipping VISIBLY is the
    ///     contract; a silent return would report a green that proved nothing about a security control.
    /// </summary>
    private static void SkipUnlessRealGitAndProcessJail()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("BLOCKED: the patch-export hardening negative controls drive the process jail with POSIX utilities and need Linux.");
        }

        if (!IsGitAvailable())
        {
            Skip.Test("BLOCKED: real `git` is required on PATH — these are negative controls for what git executes.");
        }
    }

    private static bool IsGitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return false;
            }

            _ = process.WaitForExit(milliseconds: 10000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     The marker a payload would leave, named as a WORKSPACE-relative path.
    ///     <para>
    ///         It used to be a host temp path. That stopped being a valid probe the moment AgentHome started asking
    ///         for a filesystem boundary: under isolation the sandbox has its own <c>/tmp</c> and cannot see the
    ///         host's, so a payload that "failed" to create a host marker would have proved the mount namespace and
    ///         nothing about the git guard. Inside the workspace the marker is reachable under BOTH modes, so the
    ///         assertion grades the guard either way.
    ///     </para>
    /// </summary>
    private static string MarkerRelativePath(string caseName)
    {
        return $"pwned-{caseName}.marker";
    }

    /// <summary>
    ///     The payload a git config key would name. <c>touch</c> plus the marker path: git appends the blob path it is
    ///     converting, so the command becomes <c>touch &lt;marker&gt; &lt;blob&gt;</c> and the marker appears if — and
    ///     only if — git ran it. A real binary from the sandbox's own read-only <c>/usr</c>, so it resolves under
    ///     isolation too, and it exits at once: a payload that blocked would hang the export rather than prove it safe.
    /// </summary>
    private static string PayloadCommand(string markerRelativePath)
    {
        // BOTH views of the workspace, because the payload runs wherever the node's git runs and that differs by
        // isolation mode: under SandboxIsolationMode.Filesystem the child sees the jail at SandboxIsolatedPaths.Work,
        // so the sandbox-absolute /agent-home/… path does not exist for it; without isolation the opposite is true.
        // `touch` creates what it can and reports the rest, so naming both makes the probe fire under either mode.
        //
        // This is not defensive padding: the first version named only the non-isolated path, and the deliberate-break
        // proof caught it — with the guard REMOVED the tests still passed, because the payload could never have
        // created its marker. A negative control that cannot fire proves nothing.
        return $"/usr/bin/touch {SandboxIsolatedPaths.Work}{AgentHomeGit.WorkspaceSelectedRoot}/{markerRelativePath} "
               + $"{AgentHomeGit.WorkspaceSelectedRoot}/{markerRelativePath}";
    }

    /// <summary>
    ///     Whether the payload's marker exists in the workspace, asked of the PROVIDER rather than of the host
    ///     filesystem — under isolation the workspace is inside a mount namespace, and the provider's own survey is
    ///     the one way to look that works under both modes.
    /// </summary>
    private static async Task<bool> MarkerExistsAsync(ProcessSandboxRuntimeProvider provider, string markerRelativePath)
    {
        var handle = await provider.ConnectAsync(AttachKey());
        var entries = await provider.ListFilesAsync(handle,
            new SandboxListFilesRequest
            {
                DirectoryPath = AgentHomeGit.WorkspaceSelectedRoot,
                MaxEntries = 500
            });

        return entries.Any(entry => entry.EndsWith(markerRelativePath, StringComparison.Ordinal));
    }

    private string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempPaths.Add(directory);
        return directory;
    }

    private static string PatchesDirectory(AgentHomeRunResult run)
    {
        // LogPath is runs/<id>/logs; the artifacts are its sibling.
        return Path.Combine(Path.GetDirectoryName(run.LogPath)!, "patches");
    }

    private static async Task<List<ChangedFileEntry>> ReadChangedFilesAsync(AgentHomeRunResult run)
    {
        var path = Path.Combine(PatchesDirectory(run), "changed-files.json");
        AssertEx.True(File.Exists(path), "the export wrote changed-files.json");
        return JsonSerializer.Deserialize<List<ChangedFileEntry>>(await File.ReadAllTextAsync(path), ChangedFilesJsonOptions)!;
    }

    private static async Task<string> ReadPatchAsync(AgentHomeRunResult run)
    {
        var path = Path.Combine(PatchesDirectory(run), "changes.patch");
        AssertEx.True(File.Exists(path), "the export wrote changes.patch");
        return await File.ReadAllTextAsync(path);
    }

    private static void AssertChange(List<ChangedFileEntry> entries, string relativePath, string changeType)
    {
        var entry = AssertEx.NotNull(entries.Find(candidate => candidate.RelativePath == relativePath),
            $"changed-files.json lists '{relativePath}'");
        AssertEx.Equal(changeType, entry.ChangeType, $"'{relativePath}' is reported as {changeType}");
        AssertEx.Equal(WorkspaceAlias, entry.Alias);
    }

    /// <summary>
    ///     The host apply service pointed at a scratch folder under the run's own alias, reading the patch the export
    ///     really wrote from the run's own <c>agent-home</c> root.
    /// </summary>
    private static NodePatchApplyService CreateApplyService(ExportFixture fixture, string targetFolder)
    {
        var scopeFactory = new ServiceCollection()
                           .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(TimeProvider.System))
                           .BuildServiceProvider();

        return new NodePatchApplyService(new StaticSelectedFolderResolver(WorkspaceAlias, targetFolder),
            Options.Create(new AgentHomeOptions { RootPath = fixture.StateRoot, PatchApplyTimeoutSeconds = 120 }),
            StubNodeRuntimeSettings.Create().Build(),
            new FakeNodeDataDirectory(fixture.StateRoot),
            new StaticIdentityProvider(),
            new AgentHomeRunApplyGuard(),
            scopeFactory.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodePatchApplyService>.Instance);
    }

    private static async Task AssertHostFolderUnchangedAsync(ExportFixture fixture)
    {
        // The run works on a COPY. Whatever the payload attempted, the operator's own folder is the thing that must
        // come out byte-identical.
        AssertEx.Equal("# project\nsmal\n", await File.ReadAllTextAsync(Path.Combine(fixture.HostFolder, "README.md")));
        AssertEx.Equal("notes\n", await File.ReadAllTextAsync(Path.Combine(fixture.HostFolder, "notes.txt")));
    }

    private static SandboxAttachKey AttachKey()
    {
        return new SandboxAttachKey
        {
            OwnerUserId = "owner-hardening",
            NodeId = "node-hardening",
            ProviderName = ProcessSandboxRuntimeProvider.Name,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private ExportFixture CreateFixture(long? maxPatchBytes = null, string? emptyAlias = null)
    {
        var clock = TimeProvider.System;
        var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), clock);

        var hostFolder = CreateTempDirectory("xe-ah-src");
        File.WriteAllText(Path.Combine(hostFolder, "README.md"), "# project\nsmal\n");
        File.WriteAllText(Path.Combine(hostFolder, "notes.txt"), "notes\n");
        // Renamed, not modified, by the four-change-kinds run: enough lines that git's similarity detection reports R.
        File.WriteAllText(Path.Combine(hostFolder, "guide.txt"), "one\ntwo\nthree\nfour\nfive\nsix\n");

        var resolver = new StaticSelectedFolderResolver(WorkspaceAlias, hostFolder);
        if (emptyAlias is { Length: > 0 })
        {
            // A second selection with nothing to copy: the workspace copy makes no directory for it, so it is the
            // shape whose alias must never reach a git pathspec.
            resolver.Add(emptyAlias, CreateTempDirectory("xe-ah-empty-src"));
        }

        var root = CreateTempDirectory("xe-ah-state");
        var options = Options.Create(new AgentHomeOptions
        {
            RootPath = root,
            CommandTimeoutSeconds = 120,
            MaxRunSeconds = 600
        });
        var settingsBuilder = StubNodeRuntimeSettings.Create()
                                                     .WithAgentHomeCommandTimeoutSeconds(120)
                                                     .WithAgentHomePrepareTimeoutSeconds(300);
        if (maxPatchBytes is { } budget)
        {
            settingsBuilder = settingsBuilder.WithAgentHomeMaxPatchBytes(budget);
        }

        var runtimeSettings = settingsBuilder.Build();

        var manifestService = new AgentHomeManifestService(new FakeNodeDataDirectory(root), options, provider, clock, NullLogger<AgentHomeManifestService>.Instance);
        var serviceProvider = new ServiceCollection()
                              .AddScoped<ISelectedFolderResolver>(_ => resolver)
                              // The REAL file-writing logger: the audit test below reads the run's commands.jsonl back
                              // off disk, which is the artefact an operator audits and the one that lost the actor.
                              .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(clock))
                              .BuildServiceProvider();

        var leases = new AgentHomeExecutionLeaseManager();
        var isolation = new AgentHomeWorkspaceIsolation(provider, leases, NullLogger<AgentHomeWorkspaceIsolation>.Instance);
        var workspaceService = new AgentHomeWorkspaceService(provider,
            isolation,
            new SensitiveFileExclusionService(),
            runtimeSettings,
            clock,
            NullLogger<AgentHomeWorkspaceService>.Instance);
        var patchService = new AgentHomePatchService(provider, runtimeSettings, clock, NullLogger<AgentHomePatchService>.Instance);

        var reader = new CoderWorkspaceReader(provider,
            new StaticIdentityProvider(),
            leases,
            new SensitiveFileExclusionService(),
            Options.Create(new CoderOptions()),
            Options.Create(new AgentHomeOptions()));

        var chatClient = new ScriptedGitPayloadChatClient();
        var executor = new AgentHomeGoalExecutor(chatClient,
            provider,
            reader,
            new FakeModelTrustResolver(),
            options,
            clock,
            NullLoggerFactory.Instance,
            NullLogger<AgentHomeGoalExecutor>.Instance);

        var service = new AgentHomeService(manifestService,
            provider,
            new StaticIdentityProvider(),
            leases,
            new AgentHomeRunExecutionRegistry(),
            isolation,
            workspaceService,
            patchService,
            executor,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            Options.Create(new SandboxOptions()),
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            runtimeSettings,
            new FakeConversationUploadedFileStore(),
            clock,
            NullLogger<AgentHomeService>.Instance);

        return new ExportFixture(service, provider, manifestService, serviceProvider, chatClient, hostFolder, root, resolver.FolderIds);
    }

    private sealed class ExportFixture : IDisposable
    {
        private readonly AgentHomeManifestService _manifestService;
        private readonly ScriptedGitPayloadChatClient _chatClient;
        private readonly ProcessSandboxRuntimeProvider _provider;
        private readonly AgentHomeService _service;
        private readonly ServiceProvider _serviceProvider;
        private readonly IReadOnlyList<Guid> _folderIds;

        public ExportFixture(AgentHomeService service,
            ProcessSandboxRuntimeProvider provider,
            AgentHomeManifestService manifestService,
            ServiceProvider serviceProvider,
            ScriptedGitPayloadChatClient chatClient,
            string hostFolder,
            string stateRoot,
            IReadOnlyList<Guid> folderIds)
        {
            _service = service;
            _provider = provider;
            _manifestService = manifestService;
            _serviceProvider = serviceProvider;
            _chatClient = chatClient;
            HostFolder = hostFolder;
            StateRoot = stateRoot;
            _folderIds = folderIds;
        }

        /// <summary>The real provider, so a test can ask it what is in the workspace under either isolation mode.</summary>
        public ProcessSandboxRuntimeProvider Provider => _provider;

        public string HostFolder { get; }

        /// <summary>The AgentHome state root — what <c>AgentHomeOptions.RootPath</c> was set to for this run.</summary>
        public string StateRoot { get; }

        /// <summary>Runs the whole lifecycle — copy, baseline, the scripted inner loop, export — on the real jail.</summary>
        public async Task<AgentHomeRunResult> RunAsync(params (string Tool, Dictionary<string, object?> Arguments)[] script)
        {
            _chatClient.Script = script;

            // The ambient root a real chat turn seeds; without it the executor refuses to run at all.
            using var root = SpawnContext.BeginRoot(fanOutCap: 1, cloudSpawnCap: 0, Model);
            return await _service.RunLifecycleAsync(new AgentHomeRunLifecycleRequest
            {
                SelectedFolderIds = [.. _folderIds.Select(static id => id.ToString())],
                Goal = "fix the typo in the readme",
                AllowedActions =
                [
                    AgentHomeAllowedActions.ReadWorkspace,
                    AgentHomeAllowedActions.WriteWorkspace,
                    AgentHomeAllowedActions.RunCommands,
                    AgentHomeAllowedActions.ExportPatch
                ]
            });
        }

        public void Dispose()
        {
            _provider.Dispose();
            _manifestService.Dispose();
            _serviceProvider.Dispose();
            _chatClient.Dispose();
        }
    }

    /// <summary>Plays the planted-payload script against whatever tools the executor offered, then answers with text.</summary>
    private sealed class ScriptedGitPayloadChatClient : IChatClient
    {
        private int _calls;

        public (string Tool, Dictionary<string, object?> Arguments)[] Script { get; set; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
            }

            var functions = options?.Tools?.OfType<AIFunction>().ToArray() ?? [];
            foreach (var (tool, arguments) in Script)
            {
                var function = AssertEx.NotNull(Array.Find(functions, candidate => string.Equals(candidate.Name, tool, StringComparison.Ordinal)),
                    $"the scripted tool '{tool}' must be offered for this run");

                var callArguments = new AIFunctionArguments(StringComparer.Ordinal);
                foreach (var (key, value) in arguments)
                {
                    callArguments[key] = value;
                }

                _ = await function.InvokeAsync(callArguments, cancellationToken);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StaticIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity { OwnerUserId = "owner-hardening", NodeId = "node-hardening" });
        }
    }

    private sealed class StaticSelectedFolderResolver : ISelectedFolderResolver
    {
        private readonly List<ResolvedSelectedFolder> _folders = [];

        public StaticSelectedFolderResolver(string alias, string hostPath)
        {
            FolderId = Guid.NewGuid();
            _folders.Add(new ResolvedSelectedFolder { Id = FolderId, Alias = alias, HostPath = hostPath, Mode = SelectedFolderMode.Copy });
        }

        /// <summary>The first folder's id — the one every test's real content lives in.</summary>
        public Guid FolderId { get; }

        /// <summary>Every registered folder's id, in registration order, as the run selects them.</summary>
        public IReadOnlyList<Guid> FolderIds => [.. _folders.Select(static folder => folder.Id)];

        /// <summary>Registers a further selected folder, so a run can select more than one.</summary>
        public void Add(string alias, string hostPath)
        {
            _folders.Add(new ResolvedSelectedFolder { Id = Guid.NewGuid(), Alias = alias, HostPath = hostPath, Mode = SelectedFolderMode.Copy });
        }

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SelectedFolderReference> references =
                [.. _folders.Select(static folder => new SelectedFolderReference { Id = folder.Id.ToString(), Alias = folder.Alias })];
            return Task.FromResult(references);
        }

        public Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default)
        {
            var match = _folders.Find(folder => string.Equals(id, folder.Id.ToString(), StringComparison.Ordinal)
                                                || string.Equals(id, folder.Alias, StringComparison.Ordinal));

            return match is null
                ? throw new SelectedFolderValidationException($"Unknown selected folder id '{id}'.")
                : Task.FromResult(match);
        }
    }
}
