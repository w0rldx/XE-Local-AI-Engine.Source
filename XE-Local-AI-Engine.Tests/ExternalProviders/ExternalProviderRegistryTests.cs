namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The registry as the chat path consumes it: a cached projection that reflects a save without a restart, hands
///     out no keys with its descriptors, and answers the synchronous send-path question honestly — including the one
///     answer that is not "no": "I do not know yet."
/// </summary>
public sealed class ExternalProviderRegistryTests
{
    [Test]
    public async Task ListRegistrationsAsync_WithNoConnections_ReturnsEmpty()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore());

        AssertEx.Empty(await registry.ListRegistrationsAsync(CancellationToken.None));
    }

    [Test]
    public async Task ListRegistrationsAsync_ProjectsEveryModelOfEveryConnection()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(
            Connection("box-a", models: ["qwen3", "llama4"]),
            Connection("box-b", models: ["qwen3"])));

        var registrations = await registry.ListRegistrationsAsync(CancellationToken.None);

        AssertEx.Equal(3, registrations.Count);
        // The same backing model on two connections stays two distinct identities — the whole reason the id is
        // namespaced by connection.
        AssertEx.Contains(registrations.Select(registration => registration.ModelId), "ext:box-a/qwen3");
        AssertEx.Contains(registrations.Select(registration => registration.ModelId), "ext:box-b/qwen3");
    }

    [Test]
    public async Task TryResolveAsync_CanonicalizesTheConnectionSlugButNotTheScheme()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(Connection("box-a", models: ["qwen3"])));

        // The slug is ours to mint and is canonicalized, so a caller holding a case variant of it still resolves.
        var resolved = await registry.TryResolveAsync("ext:BOX-A/qwen3", CancellationToken.None);
        AssertEx.Equal("ext:box-a/qwen3", AssertEx.NotNull(resolved).ModelId);

        // The SCHEME is matched ordinally, so "EXT:" is not an external id at all — the same rule the model-name
        // validator applies, which is what stops the two from disagreeing about whether an id is even routable.
        AssertEx.Null(await registry.TryResolveAsync("EXT:box-a/qwen3", CancellationToken.None));
    }

    [Test]
    public async Task TryResolveAsync_WithAMalformedId_ReturnsNull()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(Connection("box-a", models: ["qwen3"])));

        AssertEx.Null(await registry.TryResolveAsync("ext:box-a", CancellationToken.None));
        AssertEx.Null(await registry.TryResolveAsync("qwen3:8b", CancellationToken.None));
    }

    [Test]
    public async Task TryResolveAsync_CachesTheProjection()
    {
        var store = new FakeExternalProviderStore(Connection("box-a", models: ["qwen3"]));
        var registry = new ExternalProviderRegistry(store);

        _ = await registry.TryResolveAsync("ext:box-a/qwen3", CancellationToken.None);
        _ = await registry.TryResolveAsync("ext:box-a/qwen3", CancellationToken.None);
        _ = await registry.ListRegistrationsAsync(CancellationToken.None);

        // Every miss would otherwise be a file read plus a data-protection unprotect, on the chat path.
        AssertEx.Equal(1, store.LoadCount);
    }

    [Test]
    public async Task Invalidate_MakesTheNextReadSeeASave()
    {
        var store = new FakeExternalProviderStore(Connection("box-a", models: ["qwen3"]));
        var registry = new ExternalProviderRegistry(store);
        _ = await registry.ListRegistrationsAsync(CancellationToken.None);

        store.Replace(Connection("box-a", models: ["qwen3", "llama4"]));
        registry.Invalidate();

        // The contract requires a save to take effect without a restart: a stale generation here would keep sending to
        // a base URL the operator has already changed.
        AssertEx.Equal(2, (await registry.ListRegistrationsAsync(CancellationToken.None)).Count);
    }

    [Test]
    public async Task GetApiKeyAsync_ReturnsTheStoredKeyAndNullWhenKeyless()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(
            Connection("keyed", models: ["qwen3"], apiKey: "sk-secret"),
            Connection("keyless", models: ["qwen3"])));

        AssertEx.Equal("sk-secret", await registry.GetApiKeyAsync("keyed", CancellationToken.None));
        // Distinct from an empty key: null means send NO Authorization header at all.
        AssertEx.Null(await registry.GetApiKeyAsync("keyless", CancellationToken.None));
        AssertEx.Null(await registry.GetApiKeyAsync("never-existed", CancellationToken.None));
    }

    [Test]
    public async Task GetApiKeyAsync_CanonicalizesTheConnectionId()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(Connection("keyed", models: ["qwen3"], apiKey: "sk-secret")));

        AssertEx.Equal("sk-secret", await registry.GetApiKeyAsync("KEYED", CancellationToken.None));
    }

    [Test]
    public void TryClassifyCached_BeforeAnythingIsCached_ReportsUnresolved()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(Connection("box-a", models: ["qwen3"])));

        // NOT "not registered": the send-path gates must fail closed during the pre-boot window rather than treat a
        // cold cache as a benign miss.
        AssertEx.False(registry.TryClassifyCached("ext:box-a/qwen3", out _));
    }

    [Test]
    public async Task TryClassifyCached_AfterPriming_AnswersFromTheSnapshot()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(Connection("box-a", models: ["qwen3"])));
        await registry.PrimeAsync();

        AssertEx.True(registry.TryClassifyCached("ext:box-a/qwen3", out var hit));
        AssertEx.Equal("ext:box-a/qwen3", AssertEx.NotNull(hit).ModelId);

        // A primed snapshot CAN answer "definitely not registered" — the null registration with a true return.
        AssertEx.True(registry.TryClassifyCached("ext:box-a/gone", out var miss));
        AssertEx.Null(miss);
    }

    [Test]
    public async Task Build_WithAnUnparseableStoredBaseUrl_DropsOnlyThatConnection()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(
            Connection("broken", models: ["qwen3"]) with { BaseUrl = "not a url" },
            Connection("healthy", models: ["qwen3"])));

        var registrations = await registry.ListRegistrationsAsync(CancellationToken.None);

        // One hand-edited connection must not take the operator's other connections offline with it; its own models
        // then resolve to null, which every consumer already treats as fail-closed.
        AssertEx.Equal("ext:healthy/qwen3", registrations.Single().ModelId);
    }

    internal static StoredExternalProviderConnection Connection(string id,
        IReadOnlyList<string>? models = null,
        string? apiKey = null,
        ExternalProviderLocality locality = ExternalProviderLocality.Local,
        bool supportsTools = false,
        int? contextLength = null)
    {
        return new StoredExternalProviderConnection
        {
            Id = id,
            DisplayName = id,
            BaseUrl = "http://127.0.0.1:18099/v1/",
            ApiKey = apiKey,
            Locality = locality,
            Models = (models ?? []).Select(wireId => new StoredExternalProviderModel
            {
                WireId = wireId,
                SupportsTools = supportsTools,
                ContextLength = contextLength
            }).ToArray()
        };
    }
}

/// <summary>An in-memory <see cref="IExternalProviderStore" /> that counts loads, so caching can be asserted.</summary>
internal sealed class FakeExternalProviderStore(params StoredExternalProviderConnection[] connections) : IExternalProviderStore
{
    private StoredExternalProviderConfig _config = new()
    {
        Revision = "r0",
        Connections = connections
    };

    public int LoadCount { get; private set; }

    public Exception? LoadFailure { get; set; }

    public void Replace(params StoredExternalProviderConnection[] replacement)
    {
        _config = new StoredExternalProviderConfig
        {
            Revision = Guid.NewGuid().ToString("N"),
            Connections = replacement
        };
    }

    public Task<StoredExternalProviderConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCount++;
        return LoadFailure is null ? Task.FromResult(_config) : Task.FromException<StoredExternalProviderConfig>(LoadFailure);
    }

    public async Task<ExternalProviderLoadResult> ReadForWriteAsync(CancellationToken cancellationToken = default)
    {
        return new ExternalProviderLoadResult.Loaded(await LoadAsync(cancellationToken));
    }

    public Task<ExternalProviderWriteResult> SaveConnectionAsync(ExternalProviderConnectionSaveRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake store is a read fixture.");

    public Task<ExternalProviderWriteResult> DeleteConnectionAsync(string connectionId,
        string? expectedRevision,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake store is a read fixture.");
}
