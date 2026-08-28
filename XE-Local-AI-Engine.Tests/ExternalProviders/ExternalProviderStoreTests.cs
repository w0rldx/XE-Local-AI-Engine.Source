namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     The encrypted external-provider store's contract: what it refuses to store, what it canonicalizes on the way in,
///     what it never writes in plaintext, and the two behaviors an operator would notice immediately if they broke —
///     that renaming a connection does not de-authenticate it, and that a stale editor cannot silently overwrite a
///     concurrent edit.
/// </summary>
public sealed class ExternalProviderStoreTests : IDisposable
{
    // Matches the store's own options, so a hand-written payload is spelled exactly as the store would spell it.
    private static readonly JsonSerializerOptions RawSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    [Test]
    public async Task LoadAsync_WithNoStoredFile_ReturnsAnEmptyConfigRatherThanNull()
    {
        using var store = CreateStore();

        var config = await store.LoadAsync();

        AssertEx.Empty(config.Connections);
        AssertEx.Equal(ExternalProviderStoreSchema.CurrentVersion, config.SchemaVersion);
    }

    [Test]
    public async Task SaveConnectionAsync_NormalizesTheBaseUrlExactlyOnce()
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(baseUrl: "http://192.168.1.40:8080"));

        // The outbound guard pins every request to this stored value, so the canonical /v1/ form has to be what lands
        // on disk — not what the operator happened to type.
        AssertEx.Equal("http://192.168.1.40:8080/v1/", committed.Config.Connections.Single().BaseUrl);
    }

    [Test]
    public async Task SaveConnectionAsync_CanonicalizesTheConnectionSlug()
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(id: "  Unsloth-BOX  "));

        // One canonical spelling is what keeps the case-insensitive provider map and the ordinal tool-capable
        // allow-list agreeing about the same model.
        AssertEx.Equal("unsloth-box", committed.Config.Connections.Single().Id);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnUnusableSlug_IsRejected()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(id: "unsloth box!")));
    }

    [Test]
    public async Task SaveConnectionAsync_WithANonHttpBaseUrl_IsRejected()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(baseUrl: "file:///etc/passwd")));
    }

    [Test]
    public async Task SaveConnectionAsync_WithCredentialsInTheBaseUrl_IsRejected()
    {
        using var store = CreateStore();

        // Credentials belong in the encrypted key field, not in a base URL that is logged and rendered.
        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(baseUrl: "https://user:secret@api.example.com/v1")));
    }

    [Test]
    public async Task SaveConnectionAsync_WithADuplicateWireId_IsRejected()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models: [Model("qwen3"), Model("qwen3")])));
    }

    [Test]
    public async Task SaveConnectionAsync_KeepsWireIdsThatDifferOnlyByCase()
    {
        using var store = CreateStore();

        // Remote model ids ARE case-sensitive: collapsing these would make one of them unreachable.
        var committed = await SaveAsync(store, Request(models: [Model("Qwen/qwen3"), Model("qwen/Qwen3")]));

        AssertEx.Equal(2, committed.Config.Connections.Single().Models.Count);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnUnrecognizedDefaultEffort_IsRejected()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models: [Model("qwen3") with { DefaultReasoningEffort = "extreme" }])));
    }

    [Test]
    public async Task SaveConnectionAsync_WithATraversingWireId_IsRejected()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models: [Model("../../etc/passwd")])));
    }

    [Test]
    public async Task SaveConnectionAsync_WithABlankKey_PreservesTheStoredKey()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(apiKey: "sk-unsloth-original"));

        // The editor masks the key and sends nothing back, so a blank key on an ordinary save is "I did not touch it".
        var renamed = await SaveAsync(store, Request(displayName: "Renamed box", apiKey: null));

        AssertEx.Equal("sk-unsloth-original", renamed.Config.Connections.Single().ApiKey);
        AssertEx.Equal("Renamed box", renamed.Config.Connections.Single().DisplayName);
    }

    [Test]
    public async Task SaveConnectionAsync_WithABlankKeyAndAPathOnlyEdit_PreservesTheStoredKey()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:18099/v1", apiKey: "sk-unsloth-original"));

        // Same origin: the credential's audience did not move, so a path edit must not force the operator to re-type a
        // secret the editor never showed them.
        var moved = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:18099/openai/v1", apiKey: null));

        AssertEx.Equal("sk-unsloth-original", moved.Config.Connections.Single().ApiKey);
    }

    [Test]
    public async Task SaveConnectionAsync_WhenTheOriginChangesWithNoNewKey_IsRefused()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:18099/v1", apiKey: "sk-unsloth-original"));

        // THE exfiltration path: an Operator API caller who cannot read the encrypted key repoints the connection at a
        // listener they control and saves with no key, after which the node presents the stored secret as a bearer
        // token on the next request. Moving the endpoint has to be an explicit decision about the credential too.
        var exception = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(baseUrl: "http://attacker.example.com/v1", apiKey: null)));

        AssertEx.Contains(exception.Message, "Enter the key again");

        // And nothing was written: the stored connection still points where the operator left it.
        var stored = await store.LoadAsync();
        AssertEx.Equal("http://127.0.0.1:18099/v1/", stored.Connections.Single().BaseUrl);
    }

    [Test]
    public async Task SaveConnectionAsync_WhenTheOriginChangesWithANewKey_IsAccepted()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:18099/v1", apiKey: "sk-unsloth-original"));

        var moved = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:19000/v1", apiKey: "sk-new-endpoint"));

        AssertEx.Equal("sk-new-endpoint", moved.Config.Connections.Single().ApiKey);
    }

    [Test]
    public async Task SaveConnectionAsync_WhenTheOriginChangesAndTheKeyIsCleared_IsAccepted()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:18099/v1", apiKey: "sk-unsloth-original"));

        var moved = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:19000/v1", apiKey: null) with { ClearApiKey = true });

        AssertEx.Null(moved.Config.Connections.Single().ApiKey);
        AssertEx.Equal("http://127.0.0.1:19000/v1/", moved.Config.Connections.Single().BaseUrl);
    }

    [Test]
    public async Task SaveConnectionAsync_WhenTheOriginChangesOnAKeylessConnection_IsAccepted()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:18099/v1", apiKey: null));

        // There is no credential to leak, so nothing to re-authorize.
        var moved = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:19000/v1", apiKey: null));

        AssertEx.Equal("http://127.0.0.1:19000/v1/", moved.Config.Connections.Single().BaseUrl);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnExplicitClear_RemovesTheStoredKey()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(apiKey: "sk-unsloth-original"));

        var cleared = await SaveAsync(store, Request(apiKey: null) with { ClearApiKey = true });

        // The only way back from authenticated to keyless — and keyless means NO Authorization header at all.
        AssertEx.Null(cleared.Config.Connections.Single().ApiKey);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnExplicitClearAndAKey_ClearsRatherThanSets()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(apiKey: "sk-original"));

        var cleared = await SaveAsync(store, Request(apiKey: "sk-new") with { ClearApiKey = true });

        AssertEx.Null(cleared.Config.Connections.Single().ApiKey);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAStaleRevision_IsSupersededRatherThanOverwriting()
    {
        using var store = CreateStore();
        var first = await SaveAsync(store, Request());
        _ = await SaveAsync(store, Request(displayName: "Edited elsewhere"));

        var result = await store.SaveConnectionAsync(Request(displayName: "Stale editor") with { ExpectedRevision = first.Config.Revision });

        var superseded = result as ExternalProviderWriteResult.Superseded;
        AssertEx.NotNull(superseded);
        AssertEx.Equal("Edited elsewhere", superseded!.Current.Connections.Single().DisplayName);
    }

    [Test]
    public async Task SaveConnectionAsync_WithTheCurrentRevision_Commits()
    {
        using var store = CreateStore();
        var first = await SaveAsync(store, Request());

        var result = await store.SaveConnectionAsync(Request(displayName: "Same editor") with { ExpectedRevision = first.Config.Revision });

        AssertEx.Equal("Same editor", (result as ExternalProviderWriteResult.Committed)!.Config.Connections.Single().DisplayName);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnIdenticalPayload_ReportsNoChange()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(models: [Model("qwen3")]));

        var repeated = await store.SaveConnectionAsync(Request(models: [Model("qwen3")]));

        // Structural, not reference, comparison of the model list: record equality alone would compare the lists by
        // reference and report every re-save as a change, churning the file the reconciliation pass re-saves on boot.
        AssertEx.False((repeated as ExternalProviderWriteResult.Committed)!.Changed);
    }

    [Test]
    public async Task SaveConnectionAsync_EditingAConnection_KeepsItsPositionInTheList()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(id: "first"));
        _ = await SaveAsync(store, Request(id: "second"));
        _ = await SaveAsync(store, Request(id: "third"));

        var edited = await SaveAsync(store, Request(id: "first", displayName: "Edited"));

        AssertEx.Equal("first", edited.Config.Connections[0].Id);
        AssertEx.Equal("third", edited.Config.Connections[2].Id);
    }

    [Test]
    public async Task SaveConnectionAsync_IssuesAFreshRevisionPerWrite()
    {
        using var store = CreateStore();

        var first = await SaveAsync(store, Request());
        var second = await SaveAsync(store, Request(displayName: "Second"));

        AssertEx.NotEqual(first.Config.Revision, second.Config.Revision);
        AssertEx.NotNullOrEmpty(second.Config.Revision);
    }

    [Test]
    public async Task SaveConnectionAsync_DoesNotWriteTheApiKeyInPlaintext()
    {
        using var store = CreateStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_contentRootPath, "keys"))));

        _ = await SaveAsync(store, Request(apiKey: "sk-unsloth-secret"));

        var payload = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(StorePath));
        AssertEx.False(payload.Contains("sk-unsloth-secret", StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveConnectionAsync_WhenRunningOnUnix_CreatesTheFileUserReadWriteOnly()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var store = CreateStore();

        _ = await SaveAsync(store, Request(apiKey: "sk-unsloth-secret"));

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
    }

    [Test]
    public async Task LoadAsync_WithAnUndecryptablePayload_QuarantinesItAndReportsEmpty()
    {
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllTextAsync(StorePath, "not a protected payload");
        using var store = CreateStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_contentRootPath, "keys"))));

        var config = await store.LoadAsync();

        // A node whose external store will not decrypt has no connections, not unknown ones — and the unusable file is
        // removed so the next save is not stuck failing its read-modify-write forever.
        AssertEx.Empty(config.Connections);
        AssertEx.False(File.Exists(StorePath));
    }

    [Test]
    public async Task LoadAsync_WithANewerSchema_ReportsEmptyWithoutDeletingTheFile()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(apiKey: "sk-from-a-newer-build"));
        await WriteRawAsync(new StoredExternalProviderConfig
        {
            SchemaVersion = ExternalProviderStoreSchema.CurrentVersion + 1,
            Revision = "r",
            Connections = []
        });

        var config = await store.LoadAsync();

        // An operator who downgraded keeps their connections. Discarding a payload this build cannot interpret would
        // be the worse of the two failures, because it takes their API keys with it.
        AssertEx.Empty(config.Connections);
        AssertEx.True(File.Exists(StorePath));
    }

    [Test]
    public async Task ReadForWriteAsync_WithANewerSchema_ReportsUnsupportedRatherThanEmpty()
    {
        using var store = CreateStore();
        await WriteRawAsync(new StoredExternalProviderConfig
        {
            SchemaVersion = ExternalProviderStoreSchema.CurrentVersion + 1,
            Revision = "r",
            Connections = []
        });

        var result = await store.ReadForWriteAsync();

        // Empty is the right answer for a READER and the wrong one for a writer: reconciliation removes every route,
        // allow-list entry and default the configuration does not list, so it must be able to tell "there is nothing"
        // from "we cannot see what is there".
        AssertEx.False(result.IsAuthoritative);
        AssertEx.True(result is ExternalProviderLoadResult.UnsupportedSchema);
    }

    [Test]
    public async Task SaveConnectionAsync_WithANewerSchemaOnDisk_RefusesRatherThanClobbering()
    {
        using var store = CreateStore();
        await WriteRawAsync(new StoredExternalProviderConfig
        {
            SchemaVersion = ExternalProviderStoreSchema.CurrentVersion + 1,
            Revision = "r",
            Connections = []
        });

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () => await SaveAsync(store, Request()));

        // The refusal is only worth anything if the payload survives it.
        AssertEx.True(File.Exists(StorePath));
    }

    [Test]
    public async Task DeleteConnectionAsync_WithANewerSchemaOnDisk_RefusesRatherThanClobbering()
    {
        using var store = CreateStore();
        await WriteRawAsync(new StoredExternalProviderConfig
        {
            SchemaVersion = ExternalProviderStoreSchema.CurrentVersion + 1,
            Revision = "r",
            Connections = []
        });

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () => await store.DeleteConnectionAsync("unsloth-box", expectedRevision: null));

        AssertEx.True(File.Exists(StorePath));
    }

    [Test]
    public async Task SaveConnectionAsync_WithEffortOnANonReasoningModel_IsRefused()
    {
        using var store = CreateStore();

        // Every capability here is an operator ASSERTION about a server no probe can interrogate, and "it does not
        // reason, but here is its default reasoning effort" has no defensible reading: accepting it would put
        // reasoning_effort on the wire for a model the catalog reports as non-reasoning.
        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models: [
                Model("qwen3") with { SupportsReasoning = false, SupportsReasoningEffort = true }
            ])));
    }

    [Test]
    public async Task SaveConnectionAsync_WithADefaultEffortButNoEffortSupport_IsRefused()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models: [
                Model("qwen3") with { SupportsReasoning = true, SupportsReasoningEffort = false, DefaultReasoningEffort = "medium" }
            ])));
    }

    [Test]
    public async Task SaveConnectionAsync_WithCoherentReasoningDeclarations_IsAccepted()
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(models: [
            Model("qwen3") with { SupportsReasoning = true, SupportsReasoningEffort = true, DefaultReasoningEffort = "medium" }
        ]));

        AssertEx.Equal("medium", committed.Config.Connections.Single().Models.Single().DefaultReasoningEffort);
    }

    [Test]
    public async Task DeleteConnectionAsync_RemovesOnlyTheNamedConnection()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(id: "keep"));
        _ = await SaveAsync(store, Request(id: "drop"));

        var result = await store.DeleteConnectionAsync("DROP", expectedRevision: null);

        var committed = (result as ExternalProviderWriteResult.Committed)!;
        AssertEx.True(committed.Changed);
        AssertEx.Equal("keep", committed.Config.Connections.Single().Id);
    }

    [Test]
    public async Task DeleteConnectionAsync_WhenAlreadyAbsent_SucceedsWithNoChange()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(id: "keep"));

        var result = await store.DeleteConnectionAsync("never-existed", expectedRevision: null);

        // A retried delete after a partial failure is the reconciliation path's normal shape, not an error.
        AssertEx.False((result as ExternalProviderWriteResult.Committed)!.Changed);
    }

    [Test]
    public async Task DeleteConnectionAsync_WithAStaleRevision_IsSuperseded()
    {
        using var store = CreateStore();
        var first = await SaveAsync(store, Request(id: "keep"));
        _ = await SaveAsync(store, Request(id: "other"));

        var result = await store.DeleteConnectionAsync("keep", first.Config.Revision);

        AssertEx.True(result is ExternalProviderWriteResult.Superseded);
    }

    private string StorePath => Path.Combine(_contentRootPath, "external-providers.enc");

    private static async Task<ExternalProviderWriteResult.Committed> SaveAsync(ExternalProviderStore store,
        ExternalProviderConnectionSaveRequest request)
    {
        return (ExternalProviderWriteResult.Committed)await store.SaveConnectionAsync(request);
    }

    internal static ExternalProviderConnectionSaveRequest Request(string id = "unsloth-box",
        string displayName = "Unsloth box",
        string baseUrl = "http://127.0.0.1:18099/v1",
        ExternalProviderLocality locality = ExternalProviderLocality.Local,
        string? apiKey = null,
        IReadOnlyList<ExternalProviderModelSaveRequest>? models = null)
    {
        return new ExternalProviderConnectionSaveRequest
        {
            Id = id,
            DisplayName = displayName,
            BaseUrl = baseUrl,
            Locality = locality,
            ApiKey = apiKey,
            Models = models ?? []
        };
    }

    internal static ExternalProviderModelSaveRequest Model(string wireId, bool supportsTools = false, int? contextLength = null)
    {
        return new ExternalProviderModelSaveRequest
        {
            WireId = wireId,
            SupportsTools = supportsTools,
            ContextLength = contextLength
        };
    }

    private ExternalProviderStore CreateStore(IDataProtectionProvider? dataProtectionProvider = null)
    {
        Directory.CreateDirectory(_contentRootPath);
        return new ExternalProviderStore(dataProtectionProvider ?? new MockDataProtector(),
            new FakeNodeDataDirectory(_contentRootPath),
            NullLogger<ExternalProviderStore>.Instance);
    }

    // Writes a payload the store itself would never produce (here: a future schema version), through the same
    // pass-through protector CreateStore uses.
    private async Task WriteRawAsync(StoredExternalProviderConfig config)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(config, RawSerializerOptions);
        await File.WriteAllBytesAsync(StorePath, new MockDataProtector().Protect(payload));
    }
}
