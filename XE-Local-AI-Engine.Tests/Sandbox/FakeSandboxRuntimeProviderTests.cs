namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class FakeSandboxRuntimeProviderTests
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 5, day: 29, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    [Test]
    public async Task CreateOrAttachAsync_PopulatesHandleFromAttachKeyAndClock()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));

        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key(manifest: 3)));

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, handle.ProviderName);
        AssertEx.NotNullOrEmpty(handle.SandboxId);
        AssertEx.Equal(Key(manifest: 3), handle.AttachKey);
        AssertEx.Equal(FixedNow, handle.CreatedAt);
        AssertEx.Equal(expected: 3, handle.ManifestVersion);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenSameKey_ReusesSandbox()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));

        var first = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var second = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        AssertEx.Equal(first.SandboxId, second.SandboxId);
    }

    [Test]
    public async Task ConnectAsync_WhenKeyMatchesLiveSandbox_ReturnsHandle()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var connected = await provider.ConnectAsync(Key());

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, connected.ProviderName);
    }

    [Test]
    public async Task ConnectAsync_WhenKeyDoesNotMatch_Throws()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        await provider.CreateOrAttachAsync(CreateRequest(Key()));

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ConnectAsync(Key("other-owner")));
    }

    [Test]
    public async Task ExecuteAsync_ReturnsScriptedResultDeterministically()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.RegisterCommand("dotnet --info", exitCode: 0, "runtime: 10.0.0");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "exec-1",
            Executable = "dotnet",
            Arguments = ["--info"]
        });

        AssertEx.Equal("exec-1", result.ExecutionId);
        AssertEx.Equal(expected: 0, result.ExitCode);
        AssertEx.Equal("runtime: 10.0.0", result.StandardOutput);
        AssertEx.True(result.Completed);
    }

    [Test]
    public async Task CopyInto_Read_CopyOut_RoundTripsContent()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.WriteHostFile("/host/repo/main.cs", "class C { }");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "/host/repo/main.cs",
            DestinationPath = "/agent-home/workspace/main.cs"
        });
        var readBack = await provider.ReadFileAsync(handle, "/agent-home/workspace/main.cs");
        await provider.CopyOutAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "/agent-home/workspace/main.cs",
            DestinationPath = "/host/out/main.cs"
        });

        AssertEx.Equal("class C { }", readBack);
        AssertEx.Equal("class C { }", provider.TryReadHostFile("/host/out/main.cs"));
    }

    [Test]
    public async Task ResetDirectoryAsync_RemovesOnlyRequestedSubtree()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.WriteHostFile("selected", "old");
        provider.WriteHostFile("other", "keep");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "selected",
            DestinationPath = "/agent-home/workspace/selected/a.txt"
        });
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "other",
            DestinationPath = "/agent-home/memory/keep.txt"
        });

        await provider.ResetDirectoryAsync(handle, "/agent-home/workspace/selected");

        var paths = provider.SnapshotSandboxPaths(handle);
        AssertEx.True(paths.All(path => !path.StartsWith("/agent-home/workspace/selected/", StringComparison.Ordinal)));
        AssertEx.Contains(paths, path => path == "/agent-home/memory/keep.txt");
    }

    [Test]
    public async Task CopyIntoAsync_WhenSourceNotSeeded_FallsBackToRealDisk()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var hostFile = Path.Combine(Path.GetTempPath(), "fake-src-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(hostFile, "on disk");

        try
        {
            await provider.CopyIntoAsync(handle, new SandboxCopyRequest
            {
                SourcePath = hostFile,
                DestinationPath = "/agent-home/workspace/x.txt"
            });

            AssertEx.Equal("on disk", await provider.ReadFileAsync(handle, "/agent-home/workspace/x.txt"));
        }
        finally
        {
            File.Delete(hostFile);
        }
    }

    [Test]
    public async Task SnapshotSandboxPaths_ReturnsCopiedDestinationsSorted()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.WriteHostFile("/host/b", "b");
        provider.WriteHostFile("/host/a", "a");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "/host/b",
            DestinationPath = "/agent-home/b"
        });
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "/host/a",
            DestinationPath = "/agent-home/a"
        });

        var paths = provider.SnapshotSandboxPaths(handle);

        AssertEx.Equal(expected: 2, paths.Count);
        AssertEx.Equal("/agent-home/a", paths[0]);
        AssertEx.Equal("/agent-home/b", paths[1]);
    }

    [Test]
    public async Task Surveys_ApplyCallerSuppressionBeforeResultBudgets()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        provider.WriteHostFile("visible", "visible needle");
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "visible",
            DestinationPath = "/workspace/project/visible.cs"
        });
        for (var index = 0; index < 4; index++)
        {
            var source = "metadata-" + index;
            provider.WriteHostFile(source, "hidden needle");
            await provider.CopyIntoAsync(handle, new SandboxCopyRequest
            {
                SourcePath = source,
                DestinationPath = $"/workspace/.git/objects/{index}"
            });
        }

        static bool SuppressGit(string path) =>
            path.Split('/').Contains(".git", StringComparer.Ordinal);

        var files = await provider.ListFilesAsync(handle, new SandboxListFilesRequest
        {
            DirectoryPath = "/workspace",
            MaxEntries = 1,
            IsPathSuppressed = SuppressGit
        });
        var matches = await provider.SearchTextAsync(handle, new SandboxSearchTextRequest
        {
            DirectoryPath = "/workspace",
            Pattern = "needle",
            MaxMatches = 1,
            MaxOutputBytes = 4096,
            IsPathSuppressed = SuppressGit
        });

        AssertEx.Equal(1, files.Count);
        AssertEx.Equal("./project/visible.cs", files[0]);
        AssertEx.Equal(1, matches.Count);
        AssertEx.Contains(matches[0], "./project/visible.cs:1:visible needle");
    }

    [Test]
    public async Task ExecutedCommands_RecordsEachExecutedCommandInOrder()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "c1",
            Executable = "git",
            Arguments = ["init"]
        });
        await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "c2",
            Executable = "git",
            Arguments = ["status"]
        });

        var commands = provider.ExecutedCommands;

        AssertEx.Equal(expected: 2, commands.Count);
        AssertEx.Equal("git", commands[0].Executable);
        AssertEx.Contains(commands[0].Arguments, "init");
        AssertEx.Equal("c2", commands[1].ExecutionId);
    }

    [Test]
    public async Task CancelCommandAsync_CancelsInFlightCommandBestEffort()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.RegisterBlockingCommand("sleep");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var executeTask = provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "block-1",
            Executable = "sleep"
        });
        await provider.CancelCommandAsync(handle, "block-1");
        var result = await executeTask;

        AssertEx.False(result.Completed);
        AssertEx.Equal("block-1", result.ExecutionId);
    }

    [Test]
    public async Task ExecuteAsync_WhenCallerTokenCancels_Throws()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.RegisterBlockingCommand("sleep");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var cancellationTokenSource = new CancellationTokenSource();

        var executeTask = provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "block-2",
            Executable = "sleep"
        }, cancellationTokenSource.Token);
        await cancellationTokenSource.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => executeTask);
    }

    [Test]
    public async Task KillAsync_TerminatesSandboxAndInvalidatesHandle()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.WriteHostFile("/host/file", "data");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "/host/file",
            DestinationPath = "/agent-home/file"
        });

        await provider.KillAsync(handle);

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ReadFileAsync(handle, "/agent-home/file"));
    }

    [Test]
    public async Task KillAsync_CancelsInFlightCommands()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.RegisterBlockingCommand("sleep");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var executeTask = provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "block-3",
            Executable = "sleep"
        });
        await provider.KillAsync(handle);
        var result = await executeTask;

        AssertEx.False(result.Completed);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenOwnerChanges_DoesNotReuseSandboxContents()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.WriteHostFile("/host/secret", "owner-a secret");
        var handleA = await provider.CreateOrAttachAsync(CreateRequest(Key("owner-a")));
        await provider.CopyIntoAsync(handleA, new SandboxCopyRequest
        {
            SourcePath = "/host/secret",
            DestinationPath = "/agent-home/secret"
        });

        var handleB = await provider.CreateOrAttachAsync(CreateRequest(Key("owner-b")));

        AssertEx.NotEqual(handleA.SandboxId, handleB.SandboxId);
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ReadFileAsync(handleA, "/agent-home/secret"));
        await AssertEx.ThrowsAsync<FileNotFoundException>(() => provider.ReadFileAsync(handleB, "/agent-home/secret"));
    }

    [Test]
    public void Capabilities_AdvertiseOnlyImplementedSurface()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));

        var capabilities = provider.Capabilities;

        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyInto));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyOut));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCommandCancellation));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsAttach));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsKill));
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits));
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy));
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts));
    }

    private static SandboxCreateRequest CreateRequest(SandboxAttachKey attachKey)
    {
        return new SandboxCreateRequest
        {
            AttachKey = attachKey,
            RuntimeProfile = "dotnet-agent-home"
        };
    }

    private static SandboxAttachKey Key(string owner = "owner-1", string node = "node-1", int manifest = 1)
    {
        return new SandboxAttachKey
        {
            OwnerUserId = owner,
            NodeId = node,
            ProviderName = FakeSandboxRuntimeProvider.Name,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = manifest
        };
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
