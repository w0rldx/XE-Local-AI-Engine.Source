namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker I-pre service-level coverage: the real <see cref="AgentHomeService" /> drives the real
///     <see cref="AgentHomeManifestService" /> (temp host root) and the <see cref="FakeSandboxRuntimeProvider" />
///     end-to-end, with a fake resolver/identity injected through a real scope factory. No Docker, no Ollama.
/// </summary>
public sealed class AgentHomeServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    public async Task RunAsync_WhenPreparedWithKnownFolder_ReturnsRunScopedResult()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        AssertEx.Equal(AgentHomeStatus.Ready, prepared.Layout.Manifest.Status);
        AssertEx.Equal("fake", prepared.Handle.ProviderName);
        AssertEx.Equal(1, prepared.ResolvedFolders.Count);

        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "analyze the project",
            AllowedActions = ["read_workspace"]
        });

        AssertEx.NotNullOrEmpty(run.RunId);
        AssertEx.True(run.Completed, "the scripted no-op probe completes on the fake provider");
        AssertEx.Equal(0, run.ExitCode);
        AssertEx.True(Directory.Exists(run.LogPath), "the run-scoped log directory must exist");
        AssertEx.Contains(run.LogPath, Path.Combine("runs", run.RunId, "logs"));
    }

    [Test]
    public async Task PrepareAsync_WhenFolderHasMixedTree_CopiesSurvivorsExcludesSecretsAndOutputs()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();

        var source = Path.Combine(Path.GetTempPath(), "agenthome-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "src"));
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        await File.WriteAllTextAsync(Path.Combine(source, "src", "Program.cs"), "class P { }");
        await File.WriteAllTextAsync(Path.Combine(source, ".env"), "SECRET=1");
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "app.dll"), "binary");
        _tempRoots.Add(source);
        resolver.Add(folderId, "selected-project", source);

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        AssertEx.Equal(1, prepared.FolderSnapshots.Count);
        var snapshot = prepared.FolderSnapshots[0];
        AssertEx.Equal(SelectedFolderCopyStatus.Copied, snapshot.Status);
        AssertEx.Equal(1, snapshot.CopiedFileCount);
        AssertEx.Equal("workspace/selected/selected-project", snapshot.WorkspacePath);

        var copied = provider.SnapshotSandboxPaths(prepared.Handle);
        AssertEx.Contains(copied, path => path.EndsWith("/src/Program.cs", StringComparison.Ordinal));
        AssertEx.True(copied.All(path => !path.EndsWith("/.env", StringComparison.Ordinal)), ".env must be excluded");
        AssertEx.True(copied.All(path => !path.Contains("/bin/", StringComparison.Ordinal)), "bin/ must be pruned");
    }

    [Test]
    public async Task PrepareAsync_WhenFolderIdUnknown_ThrowsBeforeAnyProviderCall()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        using var harness = CreateHarness(clock, provider, resolver);

        await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() =>
            harness.Service.PrepareAsync(new AgentHomePrepareRequest
            {
                SelectedFolderIds = [Guid.NewGuid().ToString()]
            }));

        // Resolution precedes manifest/provider work, so no sandbox was created for any key.
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() =>
            provider.ConnectAsync(AnyKey()));
    }

    [Test]
    public async Task PrepareAsync_WhenRuntimeProfileNotAllowed_ThrowsBeforeAnyProviderCall()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() =>
            harness.Service.PrepareAsync(new AgentHomePrepareRequest
            {
                SelectedFolderIds = [folderId.ToString()],
                RuntimeProfile = "unsupported-profile"
            }));

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() =>
            provider.ConnectAsync(AnyKey()));
    }

    [Test]
    public async Task RunAsync_WhenCancelledDuringBlockingCommand_PropagatesCancellation()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        provider.RegisterBlockingCommand("dotnet --version");
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        using var cancellation = new CancellationTokenSource();
        var runTask = harness.Service.RunAsync(
            new AgentHomeRunRequest { Prepared = prepared, Goal = "g", AllowedActions = ["run_commands"] },
            cancellation.Token);

        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runTask);
    }

    [Test]
    public async Task PrepareAsync_WhenOwnerChanges_ReinitializesUnderNewOwner()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        var identity = new MutableIdentityProvider("owner-a", "node-1");
        using var harness = CreateHarness(clock, provider, resolver, identity);

        var first = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });
        AssertEx.Equal("owner-a", first.Layout.Manifest.OwnerUserId);

        identity.OwnerUserId = "owner-b";
        var second = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });

        AssertEx.Equal("owner-b", second.Layout.Manifest.OwnerUserId);
    }

    private static SandboxAttachKey AnyKey()
    {
        return new SandboxAttachKey
        {
            OwnerUserId = "owner-a",
            NodeId = "node-1",
            ProviderName = "fake",
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private string CreateSourceFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agenthome-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README.md"), "# project");
        _tempRoots.Add(directory);
        return directory;
    }

    private ServiceHarness CreateHarness(
        TimeProvider clock,
        FakeSandboxRuntimeProvider provider,
        ISelectedFolderResolver resolver,
        IAgentHomeIdentityProvider? identity = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "agenthome-svc-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);

        var options = Options.Create(new AgentHomeOptions { RootPath = root });
        var hostEnvironment = new TestHostEnvironment { ContentRootPath = root };
        var manifestService = new AgentHomeManifestService(
            hostEnvironment, options, provider, clock, NullLogger<AgentHomeManifestService>.Instance);

        var serviceProvider = new ServiceCollection()
            .AddScoped(_ => resolver)
            .BuildServiceProvider();

        var workspaceService = new AgentHomeWorkspaceService(
            provider,
            new SensitiveFileExclusionService(),
            options,
            NullLogger<AgentHomeWorkspaceService>.Instance);

        var service = new AgentHomeService(
            manifestService,
            provider,
            identity ?? new MutableIdentityProvider("owner-a", "node-1"),
            workspaceService,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            clock,
            NullLogger<AgentHomeService>.Instance);

        return new ServiceHarness(service, manifestService, serviceProvider);
    }

    private sealed class ServiceHarness : IDisposable
    {
        private readonly AgentHomeManifestService _manifestService;
        private readonly ServiceProvider _serviceProvider;

        public ServiceHarness(AgentHomeService service, AgentHomeManifestService manifestService, ServiceProvider serviceProvider)
        {
            Service = service;
            _manifestService = manifestService;
            _serviceProvider = serviceProvider;
        }

        public AgentHomeService Service { get; }

        public void Dispose()
        {
            _manifestService.Dispose();
            _serviceProvider.Dispose();
        }
    }

    private sealed class MutableIdentityProvider : IAgentHomeIdentityProvider
    {
        public MutableIdentityProvider(string ownerUserId, string nodeId)
        {
            OwnerUserId = ownerUserId;
            NodeId = nodeId;
        }

        public string OwnerUserId { get; set; }

        public string NodeId { get; set; }

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity(OwnerUserId, NodeId));
        }
    }

    private sealed class FakeSelectedFolderResolver : ISelectedFolderResolver
    {
        private readonly Dictionary<Guid, ResolvedSelectedFolder> _folders = [];

        public void Add(Guid id, string alias, string hostPath)
        {
            _folders[id] = new ResolvedSelectedFolder(id, alias, hostPath, SelectedFolderMode.Copy);
        }

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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
