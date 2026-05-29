namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class FakeSandboxRuntimeProviderTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task CreateOrAttachAsync_PopulatesHandleFromAttachKeyAndClock()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));

        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key(manifest: 3)));

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, handle.ProviderName);
        AssertEx.NotNullOrEmpty(handle.SandboxId);
        AssertEx.Equal(Key(manifest: 3), handle.AttachKey);
        AssertEx.Equal(FixedNow, handle.CreatedAt);
        AssertEx.Equal(3, handle.ManifestVersion);
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

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ConnectAsync(Key(owner: "other-owner")));
    }

    [Test]
    public async Task ExecuteAsync_ReturnsScriptedResultDeterministically()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.RegisterCommand("dotnet --info", 0, "runtime: 10.0.0");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "exec-1",
            Executable = "dotnet",
            Arguments = ["--info"]
        });

        AssertEx.Equal("exec-1", result.ExecutionId);
        AssertEx.Equal(0, result.ExitCode);
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
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest { SourcePath = "/host/file", DestinationPath = "/agent-home/file" });

        await provider.KillAsync(handle);

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ReadFileAsync(handle, "/agent-home/file"));
    }

    [Test]
    public async Task KillAsync_CancelsInFlightCommands()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.RegisterBlockingCommand("sleep");
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var executeTask = provider.ExecuteAsync(handle, new SandboxCommandRequest { ExecutionId = "block-3", Executable = "sleep" });
        await provider.KillAsync(handle);
        var result = await executeTask;

        AssertEx.False(result.Completed);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenOwnerChanges_DoesNotReuseSandboxContents()
    {
        var provider = new FakeSandboxRuntimeProvider(new FixedTimeProvider(FixedNow));
        provider.WriteHostFile("/host/secret", "owner-a secret");
        var handleA = await provider.CreateOrAttachAsync(CreateRequest(Key(owner: "owner-a")));
        await provider.CopyIntoAsync(handleA, new SandboxCopyRequest { SourcePath = "/host/secret", DestinationPath = "/agent-home/secret" });

        var handleB = await provider.CreateOrAttachAsync(CreateRequest(Key(owner: "owner-b")));

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
