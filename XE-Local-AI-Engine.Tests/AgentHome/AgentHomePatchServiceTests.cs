namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Patch-export coverage: the real <see cref="AgentHomePatchService" /> runs the two diff commands against
///     the <see cref="FakeSandboxRuntimeProvider" /> (whose git is scripted — no Docker, no real git), then parses the
///     scripted output into <c>changed-files.json</c>, enforces the byte budget, and writes the artifacts host-side.
///     Real-git byte-equality under <c>.gitattributes</c> perturbation, the <c>--binary</c> not-silently-dropped
///     behavior, and binary-patch apply rejection are proven by the env-gated real-git smoke — not here.
/// </summary>
public sealed class AgentHomePatchServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
    public async Task ExportPatchAsync_IssuesHardenedDiffCommandsInWorkspace()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 0, "M\trepo-01/src/App.cs\n");
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 0, "patch-body\n");
        var service = CreateService(provider);

        await service.ExportPatchAsync(handle, Request("run-x", NewTempDir(), Folder("repo-01")));

        var gitCommands = provider.ExecutedCommands.Where(command => command.Executable == "git").ToArray();
        AssertEx.True(gitCommands.Any(command => command.Arguments.Contains("--binary")
                                                 && HasHardenedFlags(command.Arguments)
                                                 && command.WorkingDirectory == "/agent-home/workspace/selected"),
            "the patch diff runs with --binary, the hardened -c flags, and the workspace working directory");
        AssertEx.True(gitCommands.Any(command => command.Arguments.Contains("--name-status")
                                                 && HasHardenedFlags(command.Arguments)
                                                 && command.WorkingDirectory == "/agent-home/workspace/selected"),
            "the name-status diff runs with the hardened -c flags and the workspace working directory");
    }

    [Test]
    public async Task ExportPatchAsync_BuildsChangedFilesJsonMappedToFolderIdsWithRelativePaths()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());

        var nameStatus = string.Join('\n',
            "M\trepo-01/src/Program.cs",
            "A\trepo-01/src/New.cs",
            "D\trepo-01/old/Gone.cs",
            "R100\trepo-01/a.txt\trepo-01/b.txt",
            "M\trepo-02/lib/X.cs");
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 0, nameStatus);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 0, "diff --git a/repo-01/src/Program.cs b/repo-01/src/Program.cs\n");
        var service = CreateService(provider);

        var repo01 = Folder("repo-01");
        var repo02 = Folder("repo-02");
        var runDir = NewTempDir();

        var export = await service.ExportPatchAsync(handle, Request("run-1", runDir, repo01, repo02));

        AssertEx.Equal(5, export.ChangedFileCount);
        AssertEx.False(export.Blocked);
        AssertEx.Equal("runs/run-1/patches/changes.patch", export.PatchRelativePath);
        AssertEx.Equal("runs/run-1/patches/changed-files.json", export.ChangedFilesRelativePath);

        var json = await File.ReadAllTextAsync(Path.Combine(runDir, "patches", "changed-files.json"));
        var entries = JsonSerializer.Deserialize<ChangedFileEntry[]>(json, JsonOptions)!;

        AssertEx.Equal(5, entries.Length);
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
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 0, "M\trepo-01/src/App.cs\n");
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 0, new string('x', 4096));
        var service = CreateService(provider, 16);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-2", runDir, Folder("repo-01")));

        AssertEx.True(export.Blocked, "a patch over MaxPatchBytes is blocked");
        AssertEx.Equal(1, export.ChangedFileCount);
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
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 0, string.Empty);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 0, string.Empty);
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-3", runDir, Folder("repo-01")));

        AssertEx.Equal(0, export.ChangedFileCount);
        AssertEx.True(export.PatchRelativePath is null, "no changes means no patch path");
        AssertEx.True(export.ChangedFilesRelativePath is null, "no changes means no metadata path");
        AssertEx.False(Directory.Exists(Path.Combine(runDir, "patches")), "no patches directory is created when nothing changed");
    }

    [Test]
    public async Task ExportPatchAsync_WhenDiffCommandFails_ReportsFailureAndWritesNothing()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 128, string.Empty, "fatal: bad revision 'HEAD'");
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 128, string.Empty, "fatal: bad revision 'HEAD'");
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-5", runDir, Folder("repo-01")));

        AssertEx.True(export.Failed, "a non-zero git diff exit must be reported as a failure, not a clean zero-change run");
        AssertEx.Equal(0, export.ChangedFileCount);
        AssertEx.True(export.PatchRelativePath is null, "a failed export writes no patch");
        AssertEx.True(export.ChangedFilesRelativePath is null, "a failed export writes no metadata");
        AssertEx.False(Directory.Exists(Path.Combine(runDir, "patches")), "no artifacts are written on failure");
    }

    [Test]
    public async Task ExportPatchAsync_SkipsEntriesWithUnknownAliasOrNoAliasSegment()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());

        var nameStatus = string.Join('\n',
            "M\trepo-01/keep.cs", // mapped
            "M\tunknown-alias/skip.cs", // alias not in the prepared workspace → skipped
            "M\trootfile.txt"); // no alias segment → skipped
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 0, nameStatus);
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 0, "diff --git a/repo-01/keep.cs b/repo-01/keep.cs\n");
        var service = CreateService(provider);

        var runDir = NewTempDir();
        var export = await service.ExportPatchAsync(handle, Request("run-4", runDir, Folder("repo-01")));

        AssertEx.Equal(1, export.ChangedFileCount);
        var entries = JsonSerializer.Deserialize<ChangedFileEntry[]>(await File.ReadAllTextAsync(Path.Combine(runDir, "patches", "changed-files.json")),
            JsonOptions)!;
        AssertEx.Equal(1, entries.Length);
        AssertEx.Equal("keep.cs", entries[0].RelativePath);
    }

    private static void AssertEntry(ChangedFileEntry[] entries, string folderId, string alias, string relativePath, string changeType)
    {
        var entry = entries.Single(candidate => candidate.RelativePath == relativePath);
        AssertEx.Equal(folderId, entry.SelectedFolderId);
        AssertEx.Equal(alias, entry.Alias);
        AssertEx.Equal(changeType, entry.ChangeType);
    }

    private static bool HasHardenedFlags(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("core.hooksPath=/dev/null") && arguments.Contains("core.attributesfile=/dev/null");
    }

    private static AgentHomePatchService CreateService(FakeSandboxRuntimeProvider provider, long maxPatchBytes = 52428800)
    {
        var options = Options.Create(new AgentHomeOptions
        {
            MaxPatchBytes = maxPatchBytes
        });
        return new AgentHomePatchService(provider, options, NullLogger<AgentHomePatchService>.Instance);
    }

    private static AgentHomePatchExportRequest Request(string runId, string hostRunDirectory, params ResolvedSelectedFolder[] folders)
    {
        return new AgentHomePatchExportRequest
        {
            RunId = runId,
            HostRunDirectory = hostRunDirectory,
            ResolvedFolders = folders
        };
    }

    private static ResolvedSelectedFolder Folder(string alias)
    {
        return new ResolvedSelectedFolder(Guid.NewGuid(), alias, "/host/" + alias, SelectedFolderMode.Copy);
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
