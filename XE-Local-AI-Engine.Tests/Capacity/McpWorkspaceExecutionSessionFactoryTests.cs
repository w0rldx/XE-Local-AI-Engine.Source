namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpWorkspaceExecutionSessionFactoryTests
{
    [Test]
    public async Task OpenAsync_WhenLeaseIsBusy_ReturnsStableBusyCodeBeforeSandboxPreparation()
    {
        var harness = new Harness(lease: null);

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspaceBusy, result.FailureCode!);
        AssertEx.False(result.DisplayMessage.Contains(harness.Workspace.HostPath, StringComparison.Ordinal),
            "busy response must not expose the resolved host path.");
        AssertEx.Equal(0, harness.Manifest.InitializeCallCount);
        await harness.Provider.DidNotReceiveWithAnyArgs().CreateOrAttachAsync(default!, default);
        AssertEx.Equal(0, harness.WorkspaceService.PrepareCallCount);
    }

    [Test]
    public async Task OpenAsync_WhenPreparationSucceeds_UsesEstablishedSandboxAndReleasesLeaseExactlyOnce()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        var order = new List<string>();
        harness.Identity.OnGet = () => order.Add("identity");
        harness.LeaseManager.OnAcquire = () => order.Add("lease");
        harness.Manifest.OnInitialize = () => order.Add("manifest");
        harness.Provider.CreateOrAttachAsync(Arg.Any<SandboxCreateRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            order.Add("sandbox");
            return Task.FromResult(harness.Handle);
        });
        harness.Resolver.ResolveAsync(harness.Workspace.Id.ToString("D"), Arg.Any<CancellationToken>()).Returns(call =>
        {
            order.Add("resolve");
            return Task.FromResult(harness.Workspace);
        });
        harness.WorkspaceService.OnPrepare = () => order.Add("workspace");

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);
        using (result.Session)
            using (result.Session!.EnterAmbientScope())
            {
                AssertEx.True(order.SequenceEqual(["identity", "lease", "manifest", "sandbox", "resolve", "workspace"], StringComparer.Ordinal),
                    "workspace session must follow the established identity/lease/sandbox/preparation order.");
            }

        AssertEx.Equal(1, harness.WorkspaceService.PrepareCallCount);
        AssertEx.Equal(harness.Handle, harness.WorkspaceService.LastHandle!);
        AssertEx.True(harness.WorkspaceService.LastFolders!.Count == 1
                      && harness.WorkspaceService.LastFolders[0] == harness.Workspace,
            "preparation must receive exactly the authorized workspace.");
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.Equal(1, lease.Ambient.DisposeCallCount);
    }

    [Test]
    public async Task OpenAsync_WhenPreparationFails_FailsClosedAndReleasesLease()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        harness.WorkspaceService.PreparationException = new IOException($"copy refused at {harness.Workspace.HostPath}");

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Null(result.Session);
        AssertEx.False(result.DisplayMessage.Contains(harness.Workspace.HostPath, StringComparison.Ordinal),
            "preparation failure must not expose the resolved host path.");
        AssertEx.True(harness.Logger.AllText.Contains(nameof(IOException), StringComparison.Ordinal),
            "preparation diagnostics must retain the opaque exception type.");
        AssertEx.True(harness.Logger.AllText.Contains(harness.Workspace.Id.ToString("D"), StringComparison.Ordinal),
            "preparation diagnostics must identify the opaque workspace id.");
        AssertEx.False(harness.Logger.AllText.Contains(harness.Workspace.HostPath, StringComparison.Ordinal),
            "preparation diagnostics must not expose the resolved host path.");
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
    }

    [Test]
    public async Task OpenAsync_WhenPreparationReportsBlockedQuota_RecoversAndRefusesSession()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        harness.WorkspaceService.Snapshots = [Snapshot(harness.Workspace.Alias, SelectedFolderCopyStatus.BlockedQuota)];

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Null(result.Session);
        AssertEx.False(result.DisplayMessage.Contains(harness.Workspace.HostPath, StringComparison.Ordinal),
            "quota rejection must not expose the authorized host path.");
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.Equal(0, lease.EnterAmbientScopeCallCount);
    }

    [Test]
    public async Task OpenAsync_WhenPreparationReturnsNoSnapshot_RecoversAndRefusesSession()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        harness.WorkspaceService.Snapshots = [];

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Null(result.Session);
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.Equal(0, lease.EnterAmbientScopeCallCount);
    }

    [Test]
    public async Task OpenAsync_WhenPreparationReturnsMismatchedSnapshot_RecoversAndRefusesSession()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        harness.WorkspaceService.Snapshots = [Snapshot("different-workspace", SelectedFolderCopyStatus.Copied)];

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Null(result.Session);
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.Equal(0, lease.EnterAmbientScopeCallCount);
    }

    [Test]
    public async Task OpenAsync_AfterPriorWorkspace_WhenManifestInitializationFails_RecoversBeforeLeaseRelease()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        var order = new List<string>();
        harness.Manifest.OnInitialize = () => order.Add("manifest");
        harness.Isolation.OnRecover = () => order.Add("recover");
        lease.OnDispose = () => order.Add("release");
        harness.Manifest.InitializationException = new IOException("manifest refused");

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.True(order.SequenceEqual(["manifest", "recover", "release"], StringComparer.Ordinal),
            "manifest failure must recover the shared sandbox before releasing its owner-node lease.");
        await harness.Provider.DidNotReceiveWithAnyArgs().CreateOrAttachAsync(default!, default);
        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Test]
    public async Task OpenAsync_AfterPriorWorkspace_WhenCreateFails_RecoversBeforeResolutionAndRelease()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        var order = new List<string>();
        harness.Isolation.OnRecover = () => order.Add("recover");
        lease.OnDispose = () => order.Add("release");
        harness.Provider.CreateOrAttachAsync(Arg.Any<SandboxCreateRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<SandboxHandle>>(_ =>
               {
                   order.Add("create");
                   throw new IOException("create refused");
               });

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.True(order.SequenceEqual(["create", "recover", "release"], StringComparer.Ordinal),
            "sandbox creation failure must recover before releasing the owner-node lease.");
        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Test]
    public async Task OpenAsync_WhenWorkspaceIsRevokedAfterLeaseAcquisition_RecoversAndReturnsPathFreeAuthorizationFailure()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        var order = new List<string>();
        harness.LeaseManager.OnAcquire = () => order.Add("lease");
        harness.Isolation.OnRecover = () => order.Add("recover");
        lease.OnDispose = () => order.Add("release");
        harness.Resolver.ResolveAsync(harness.Workspace.Id.ToString("D"), Arg.Any<CancellationToken>())
               .Returns<Task<ResolvedSelectedFolder>>(_ =>
               {
                   order.Add("resolve");
                   throw new SelectedFolderValidationException("revoked");
               });

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspaceNotAuthorized, result.FailureCode!);
        AssertEx.False(result.DisplayMessage.Contains(harness.Workspace.HostPath, StringComparison.Ordinal),
            "revocation response must not expose the previously resolved path.");
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
        AssertEx.Equal(1, lease.DisposeCallCount);
        AssertEx.Equal(0, harness.WorkspaceService.PrepareCallCount);
        AssertEx.True(order.SequenceEqual(["lease", "resolve", "recover", "release"], StringComparer.Ordinal),
            "authorization must be resolved under the lease and revocation must recover before release.");
    }

    [Test]
    public async Task OpenAsync_WhenRecoveryCannotProveClean_ReturnsPreparationFailureAndRefusesSession()
    {
        var lease = new TrackingLease();
        var harness = new Harness(lease);
        harness.WorkspaceService.PreparationException = new IOException("copy refused");
        harness.Isolation.RecoveryException = new AgentHomeWorkspacePoisonedException();

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Null(result.Session);
        AssertEx.Equal(1, harness.Isolation.RecoverCallCount);
        AssertEx.Equal(1, lease.DisposeCallCount);
    }

    [Test]
    public async Task OpenAsync_WhenOwnerNodeKeyIsPoisoned_RefusesBeforePostLeasePhase()
    {
        var harness = new Harness(lease: null);
        harness.LeaseManager.IsPoisonedValue = true;

        var result = await harness.Factory.OpenAsync(harness.Workspace.Id, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, result.FailureCode!);
        AssertEx.Equal(0, harness.Manifest.InitializeCallCount);
        AssertEx.Equal(0, harness.Isolation.RecoverCallCount);
    }

    private sealed class Harness
    {
        public Harness(TrackingLease? lease)
        {
            Identity.Owner = Owner;
            LeaseManager.Lease = lease;
            Manifest.Layout = Layout;
            Resolver.ResolveAsync(Workspace.Id.ToString("D"), Arg.Any<CancellationToken>()).Returns(Workspace);
            Provider.ProviderName.Returns("fake");
            Provider.Capabilities.Returns(SandboxProviderCapabilities.SupportsNetworkPolicy);
            Provider.CreateOrAttachAsync(Arg.Any<SandboxCreateRequest>(), Arg.Any<CancellationToken>()).Returns(Handle);
            WorkspaceService.Snapshots = [Snapshot(Workspace.Alias, SelectedFolderCopyStatus.Copied)];
            Factory = new McpWorkspaceExecutionSessionFactory(Identity,
                LeaseManager,
                Isolation,
                Manifest,
                Provider,
                Resolver,
                WorkspaceService,
                Options.Create(new AgentHomeOptions()),
                Logger);
        }

        public McpWorkspaceExecutionSessionFactory Factory { get; }

        public FakeIdentityProvider Identity { get; } = new();

        public FakeWorkspaceIsolation Isolation { get; } = new();

        public AgentHomeLayout Layout { get; } = new()
        {
            RootPath = "/agent-home",
            Manifest = new AgentHomeManifest
            {
                Version = AgentHomeManifest.CurrentVersion,
                Status = AgentHomeStatus.Ready,
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = "fake",
                RuntimeProfile = "dotnet-agent-home",
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch
            }
        };

        public FakeLeaseManager LeaseManager { get; } = new();

        public CapturingLogger<McpWorkspaceExecutionSessionFactory> Logger { get; } = new();

        public FakeManifestService Manifest { get; } = new();

        public AgentHomeOwnerIdentity Owner { get; } = new("owner", "node");

        public IAgentSandboxRuntimeProvider Provider { get; } = Substitute.For<IAgentSandboxRuntimeProvider>();

        public ISelectedFolderResolver Resolver { get; } = Substitute.For<ISelectedFolderResolver>();

        public SandboxHandle Handle { get; } = CreateHandle();

        public ResolvedSelectedFolder Workspace { get; } =
            new(Guid.Parse("2253c107-339d-47e6-a7c0-46084464bfa1"), "repo", "/private/repo", SelectedFolderMode.ReadOnlyMount);

        public FakeWorkspaceService WorkspaceService { get; } = new();

        private static SandboxHandle CreateHandle()
        {
            var key = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = "fake",
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = AgentHomeManifest.CurrentVersion
            };
            return new SandboxHandle
            {
                ProviderName = "fake",
                SandboxId = "sandbox",
                AttachKey = key,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = AgentHomeManifest.CurrentVersion
            };
        }
    }

    private static SelectedFolderSnapshot Snapshot(string alias, SelectedFolderCopyStatus status) =>
        new()
        {
            Alias = alias,
            Status = status,
            CopiedFileCount = status == SelectedFolderCopyStatus.Copied ? 1 : 0,
            ExcludedFileCount = 0,
            ExcludedDirectoryCount = 0,
            CopiedBytes = status == SelectedFolderCopyStatus.Copied ? 1 : 0,
            WorkspacePath = $"workspace/selected/{alias}"
        };

    private sealed class FakeIdentityProvider : IAgentHomeIdentityProvider
    {
        public Action? OnGet { get; set; }

        public AgentHomeOwnerIdentity? Owner { get; set; }

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnGet?.Invoke();
            return Task.FromResult(Owner ?? throw new InvalidOperationException("No owner was configured."));
        }
    }

    private sealed class FakeLeaseManager : IAgentHomeExecutionLeaseManager
    {
        public Action? OnAcquire { get; set; }

        public TrackingLease? Lease { get; set; }

        public IAgentHomeExecutionLease? TryAcquire(AgentHomeExecutionLeaseKey key)
        {
            OnAcquire?.Invoke();
            return Lease;
        }

        public IAgentHomeExecutionLease? TryAcquireForRecovery(AgentHomeExecutionLeaseKey key) =>
            Lease;

        public bool IsPoisoned(AgentHomeExecutionLeaseKey key) =>
            IsPoisonedValue;

        public bool IsPoisonedValue { get; set; }

        public void MarkPoisoned(AgentHomeExecutionLeaseKey key) =>
            IsPoisonedValue = true;

        public void ClearPoison(AgentHomeExecutionLeaseKey key) =>
            IsPoisonedValue = false;
    }

    private sealed class FakeWorkspaceIsolation : IAgentHomeWorkspaceIsolation
    {
        public Action? OnRecover { get; set; }

        public int RecoverCallCount { get; private set; }

        public Exception? RecoveryException { get; set; }

        public Task<AgentHomeWorkspaceClearResult> ClearAsync(SandboxHandle handle,
            AgentHomeExecutionLeaseKey key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentHomeWorkspaceClearResult.Reset);

        public Task RecoverExistingAsync(SandboxAttachKey attachKey,
            AgentHomeExecutionLeaseKey key,
            CancellationToken cancellationToken = default)
        {
            RecoverCallCount++;
            OnRecover?.Invoke();
            return RecoveryException is { } exception ? Task.FromException(exception) : Task.CompletedTask;
        }
    }

    private sealed class FakeManifestService : IAgentHomeManifestService
    {
        public int InitializeCallCount { get; private set; }

        public AgentHomeLayout? Layout { get; set; }

        public Exception? InitializationException { get; set; }

        public Action? OnInitialize { get; set; }

        public Task<AgentHomeLayout> InitializeAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCallCount++;
            OnInitialize?.Invoke();
            return InitializationException is { } exception
                ? Task.FromException<AgentHomeLayout>(exception)
                : Task.FromResult(Layout ?? throw new InvalidOperationException("No layout was configured."));
        }
    }

    private sealed class FakeWorkspaceService : IAgentHomeWorkspaceService
    {
        public SandboxHandle? LastHandle { get; private set; }

        public IReadOnlyList<ResolvedSelectedFolder>? LastFolders { get; private set; }

        public Action? OnPrepare { get; set; }

        public int PrepareCallCount { get; private set; }

        public Exception? PreparationException { get; set; }

        public IReadOnlyList<SelectedFolderSnapshot> Snapshots { get; set; } = [];

        public Task<IReadOnlyList<SelectedFolderSnapshot>> PrepareSelectedFoldersAsync(SandboxHandle handle,
            IReadOnlyList<ResolvedSelectedFolder> resolvedFolders,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCallCount++;
            LastHandle = handle;
            LastFolders = resolvedFolders;
            OnPrepare?.Invoke();
            return PreparationException is { } exception
                ? Task.FromException<IReadOnlyList<SelectedFolderSnapshot>>(exception)
                : Task.FromResult(Snapshots);
        }
    }

    private sealed class TrackingLease : IAgentHomeExecutionLease
    {
        public Action? OnDispose { get; set; }

        public TrackingDisposable Ambient { get; } = new();

        public int DisposeCallCount { get; private set; }

        public int EnterAmbientScopeCallCount { get; private set; }

        public bool IsBorrowed => false;

        public IDisposable EnterAmbientScope()
        {
            EnterAmbientScopeCallCount++;
            return Ambient;
        }

        public void Dispose()
        {
            DisposeCallCount++;
            OnDispose?.Invoke();
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose()
        {
            DisposeCallCount++;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _entries = [];

        public string AllText => string.Join(Environment.NewLine, _entries);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(formatter(state, exception));
        }
    }
}
