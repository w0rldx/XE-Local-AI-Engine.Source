namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The write path an endpoint calls, and the side effects it owes the rest of the node. A bare store write leaves
///     the model unroutable, possibly not tool-capable, and — after a key edit — still being sent to with the previous
///     key from a cached chat client.
/// </summary>
public sealed class ExternalProviderAdministrationServiceTests
{
    [Test]
    public async Task SaveConnectionAsync_InvalidatesTheRegistryBeforeReconciling()
    {
        var fixture = new Fixture();

        _ = await fixture.Service.SaveConnectionAsync(ExternalProviderStoreTests.Request());

        // Ordering, not just occurrence: reconciling against a stale generation would repair the node into the
        // configuration the operator just replaced.
        Received.InOrder(() =>
        {
            fixture.RegistryCache.Invalidate();
            _ = fixture.Reconciler.ReconcileAsync(Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task SaveConnectionAsync_ClearsTheChatClientCache()
    {
        var fixture = new Fixture();

        _ = await fixture.Service.SaveConnectionAsync(ExternalProviderStoreTests.Request(apiKey: "sk-rotated"));

        // An API-key or base-URL edit changes neither the provider map nor the allow-list, so reconciliation reports
        // no drift — and the router is still holding a client built against the previous key.
        fixture.ChatClientCache.Received(1).ClearClientCache();
    }

    [Test]
    public async Task SaveConnectionAsync_WhenNothingChanged_DoesNotDisturbInFlightClients()
    {
        var fixture = new Fixture();
        fixture.Store.NextResult = config => new ExternalProviderWriteResult.Committed(config, Changed: false);

        _ = await fixture.Service.SaveConnectionAsync(ExternalProviderStoreTests.Request());

        fixture.ChatClientCache.DidNotReceive().ClearClientCache();
    }

    [Test]
    public async Task SaveConnectionAsync_WhenSuperseded_RunsNoSideEffects()
    {
        var fixture = new Fixture();
        fixture.Store.NextResult = config => new ExternalProviderWriteResult.Superseded(config);

        var result = await fixture.Service.SaveConnectionAsync(ExternalProviderStoreTests.Request());

        // Nothing was written, so there is nothing to reconcile — and reconciling anyway would let a rejected save
        // still churn the node's caches.
        AssertEx.True(result is ExternalProviderWriteResult.Superseded);
        fixture.RegistryCache.DidNotReceive().Invalidate();
        _ = fixture.Reconciler.DidNotReceive().ReconcileAsync(Arg.Any<CancellationToken>());
        fixture.ChatClientCache.DidNotReceive().ClearClientCache();
    }

    [Test]
    public async Task DeleteConnectionAsync_ReconcilesAndClearsCaches()
    {
        var fixture = new Fixture();

        _ = await fixture.Service.DeleteConnectionAsync("unsloth-box", expectedRevision: null);

        _ = fixture.Reconciler.Received(1).ReconcileAsync(Arg.Any<CancellationToken>());
        fixture.ChatClientCache.Received(1).ClearClientCache();
    }

    [Test]
    public async Task SaveConnectionAsync_PropagatesAValidationFailureWithoutSideEffects()
    {
        var fixture = new Fixture();
        fixture.Store.Failure = new ExternalProviderValidationException("bad base url");

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await fixture.Service.SaveConnectionAsync(ExternalProviderStoreTests.Request()));

        fixture.RegistryCache.DidNotReceive().Invalidate();
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Service = new ExternalProviderAdministrationService(Store,
                RegistryCache,
                Reconciler,
                ChatClientCache,
                NullLogger<ExternalProviderAdministrationService>.Instance);
        }

        public ScriptedExternalProviderStore Store { get; } = new();
        public IExternalProviderRegistryCache RegistryCache { get; } = Substitute.For<IExternalProviderRegistryCache>();
        public IExternalProviderReconciler Reconciler { get; } = Substitute.For<IExternalProviderReconciler>();
        public ILocalChatClientCacheInvalidator ChatClientCache { get; } = Substitute.For<ILocalChatClientCacheInvalidator>();
        public ExternalProviderAdministrationService Service { get; }
    }

    /// <summary>A store whose write outcome the test scripts, so every branch of the side-effect ladder is reachable.</summary>
    private sealed class ScriptedExternalProviderStore : IExternalProviderStore
    {
        private readonly StoredExternalProviderConfig _config = new()
        {
            Revision = "r1"
        };

        public Func<StoredExternalProviderConfig, ExternalProviderWriteResult> NextResult { get; set; } =
            config => new ExternalProviderWriteResult.Committed(config, Changed: true);

        public Exception? Failure { get; set; }

        public Task<StoredExternalProviderConfig> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_config);

        public Task<ExternalProviderLoadResult> ReadForWriteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalProviderLoadResult>(new ExternalProviderLoadResult.Loaded(_config));

        public Task<ExternalProviderWriteResult> SaveConnectionAsync(ExternalProviderConnectionSaveRequest request,
            CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(NextResult(_config)) : Task.FromException<ExternalProviderWriteResult>(Failure);

        public Task<ExternalProviderWriteResult> DeleteConnectionAsync(string connectionId,
            string? expectedRevision,
            CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult(NextResult(_config)) : Task.FromException<ExternalProviderWriteResult>(Failure);
    }
}
