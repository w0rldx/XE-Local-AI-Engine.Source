namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Workspace-copy coverage: the real <see cref="AgentHomeWorkspaceService" /> walks a real temp source
///     tree and copies survivors into the <see cref="FakeSandboxRuntimeProvider" /> in-memory sandbox (no Docker). It
///     proves exclusions, symlink-escape rejection, the per-folder byte budget, the git baseline, and the read-only
///     mount copy fallback.
/// </summary>
public sealed class AgentHomeWorkspaceServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 5, day: 29, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

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
    public async Task PrepareSelectedFoldersAsync_CopiesSurvivorsAndExcludesSecretsAndOutputs()
    {
        var source = NewTempDir();
        Directory.CreateDirectory(Path.Combine(source, "src"));
        Directory.CreateDirectory(Path.Combine(source, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(source, "src", "App.cs"), "x");
        await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "x");
        await File.WriteAllTextAsync(Path.Combine(source, ".env"), "SECRET=1");
        await File.WriteAllTextAsync(Path.Combine(source, "node_modules", "lib.js"), "x");

        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider);

        var snapshots = await service.PrepareSelectedFoldersAsync(handle, [Folder("proj", source)]);

        AssertEx.Equal(expected: 1, snapshots.Count);
        var snapshot = snapshots[0];
        AssertEx.Equal(SelectedFolderCopyStatus.Copied, snapshot.Status);
        AssertEx.Equal(expected: 2, snapshot.CopiedFileCount);
        AssertEx.Equal(expected: 1, snapshot.ExcludedFileCount);
        AssertEx.Equal(expected: 1, snapshot.ExcludedDirectoryCount);
        AssertEx.Equal("workspace/selected/proj", snapshot.WorkspacePath);

        var paths = provider.SnapshotSandboxPaths(handle);
        AssertEx.Contains(paths, path => path == "/agent-home/workspace/selected/proj/src/App.cs");
        AssertEx.Contains(paths, path => path == "/agent-home/workspace/selected/proj/README.md");
        AssertEx.True(paths.All(path => !path.EndsWith("/.env", StringComparison.Ordinal)), ".env must be excluded");
        AssertEx.True(paths.All(path => !path.Contains("node_modules", StringComparison.Ordinal)), "node_modules must be pruned");
    }

    [Test]
    public async Task PrepareSelectedFoldersAsync_WhenSymlinkEscapesRoot_Throws()
    {
        var source = NewTempDir();
        var outside = NewTempDir();
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "leak");
        Directory.CreateSymbolicLink(Path.Combine(source, "escape"), outside);

        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider);

        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() =>
            service.PrepareSelectedFoldersAsync(handle, [Folder("proj", source)]));

        AssertEx.Empty(provider.SnapshotSandboxPaths(handle));
    }

    [Test]
    public async Task PrepareSelectedFoldersAsync_WhenSymlinkStaysInsideRoot_CopiesRealFileAndSkipsLink()
    {
        var source = NewTempDir();
        await File.WriteAllTextAsync(Path.Combine(source, "real.txt"), "x");
        File.CreateSymbolicLink(Path.Combine(source, "link.txt"), Path.Combine(source, "real.txt"));

        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider);

        var snapshots = await service.PrepareSelectedFoldersAsync(handle, [Folder("proj", source)]);

        AssertEx.Equal(SelectedFolderCopyStatus.Copied, snapshots[0].Status);
        var paths = provider.SnapshotSandboxPaths(handle);
        AssertEx.Contains(paths, path => path.EndsWith("/real.txt", StringComparison.Ordinal));
        AssertEx.True(paths.All(path => !path.EndsWith("/link.txt", StringComparison.Ordinal)), "an in-root symlink is not copied");
    }

    [Test]
    public async Task PrepareSelectedFoldersAsync_WhenHostPathMissingOrExtended_FailsClosed()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider);

        var missing = Path.Combine(Path.GetTempPath(), "agenthome-missing-" + Guid.NewGuid().ToString("N"));
        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() =>
            service.PrepareSelectedFoldersAsync(handle, [Folder("proj", missing)]));

        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() =>
            service.PrepareSelectedFoldersAsync(handle, [Folder("proj", @"\\?\C:\windows")]));
    }

    [Test]
    public async Task PrepareSelectedFoldersAsync_WhenOverByteBudget_BlocksBeforeCopy()
    {
        var source = NewTempDir();
        await File.WriteAllTextAsync(Path.Combine(source, "big.bin"), new string(c: 'a', count: 4096));

        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider, maxBytes: 100);

        var snapshots = await service.PrepareSelectedFoldersAsync(handle, [Folder("proj", source)]);

        AssertEx.Equal(SelectedFolderCopyStatus.BlockedQuota, snapshots[0].Status);
        AssertEx.Equal(expected: 0, snapshots[0].CopiedFileCount);
        AssertEx.Empty(provider.SnapshotSandboxPaths(handle));
    }

    [Test]
    public async Task PrepareSelectedFoldersAsync_AfterCopy_IssuesHardenedGitBaselineInWorkspace()
    {
        var source = NewTempDir();
        await File.WriteAllTextAsync(Path.Combine(source, "App.cs"), "x");

        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider);

        await service.PrepareSelectedFoldersAsync(handle, [Folder("proj", source)]);

        var gitCommands = provider.ExecutedCommands.Where(command => command.Executable == "git").ToArray();

        // Every baseline command runs in the selected workspace with the byte-stabilizing git flags so a copied
        // .gitattributes cannot perturb the baseline (and thus the later diff) bytes.
        AssertEx.True(gitCommands.Length > 0, "the baseline must issue git commands");
        AssertEx.True(gitCommands.All(command => command.WorkingDirectory == "/agent-home/workspace/selected"),
            "every baseline command runs in the selected workspace");
        AssertEx.True(gitCommands.All(command => HasHardenedFlags(command.Arguments)),
            "every baseline command carries -c core.hooksPath=/dev/null and -c core.attributesfile=/dev/null");

        AssertEx.True(IssuesArgument(gitCommands, "init"), "git init must run");
        AssertEx.True(gitCommands.Any(command => command.Arguments.Contains("config")
                                                 && command.Arguments.Contains("core.autocrlf")
                                                 && command.Arguments.Contains("false")),
            "core.autocrlf must be disabled");
        AssertEx.True(gitCommands.Any(command => command.Arguments.Contains("config")
                                                 && command.Arguments.Contains("core.filemode")
                                                 && command.Arguments.Contains("false")),
            "core.filemode must be disabled");
        AssertEx.True(IssuesArgument(gitCommands, "add"), "git add -A must run");
        AssertEx.True(IssuesArgument(gitCommands, "commit"), "git commit must run for the baseline");
    }

    [Test]
    public async Task PrepareSelectedFoldersAsync_WhenBaselineCommandFails_Throws()
    {
        var source = NewTempDir();
        await File.WriteAllTextAsync(Path.Combine(source, "App.cs"), "x");

        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        // git init returns a non-zero exit code; the baseline must fail the prepare rather than continue.
        provider.RegisterCommand(BaselineCommandKey("init"), exitCode: 1, string.Empty, "fatal: cannot init");
        var service = CreateService(provider);

        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() =>
            service.PrepareSelectedFoldersAsync(handle, [Folder("proj", source)]));
    }

    private static string BaselineCommandKey(params string[] tail)
    {
        return AgentHomeGit.Executable + " " + string.Join(" ", AgentHomeGit.Arguments(tail));
    }

    private static bool HasHardenedFlags(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("core.hooksPath=/dev/null") && arguments.Contains("core.attributesfile=/dev/null");
    }

    private static bool IssuesArgument(IReadOnlyList<SandboxCommandRequest> commands, string argument)
    {
        return commands.Any(command => command.Arguments.Contains(argument));
    }

    [Test]
    public async Task PrepareSelectedFoldersAsync_WhenReadOnlyMountMode_FallsBackToCopy()
    {
        var source = NewTempDir();
        await File.WriteAllTextAsync(Path.Combine(source, "App.cs"), "x");

        var provider = new FakeSandboxRuntimeProvider(new FixedClock(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest());
        var service = CreateService(provider);

        var snapshots = await service.PrepareSelectedFoldersAsync(handle,
            [Folder("proj", source, SelectedFolderMode.ReadOnlyMount)]);

        AssertEx.Equal(SelectedFolderCopyStatus.Copied, snapshots[0].Status);
        AssertEx.True(snapshots[0].CopiedFileCount > 0, "a read-only mount folder is still copied in the MVP");
    }

    private static AgentHomeWorkspaceService CreateService(FakeSandboxRuntimeProvider provider, long maxBytes = 536870912)
    {
        var runtimeSettings = StubNodeRuntimeSettings.Create()
            .WithAgentHomeMaxSelectedFolderBytes(maxBytes)
            .Build();
        return new AgentHomeWorkspaceService(provider,
            new SensitiveFileExclusionService(),
            runtimeSettings,
            NullLogger<AgentHomeWorkspaceService>.Instance);
    }

    private static ResolvedSelectedFolder Folder(string alias, string hostPath, SelectedFolderMode mode = SelectedFolderMode.Copy)
    {
        return new ResolvedSelectedFolder(Guid.NewGuid(), alias, hostPath, mode);
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
        var dir = Path.Combine(Path.GetTempPath(), "agenthome-ws-" + Guid.NewGuid().ToString("N"));
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
