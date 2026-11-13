namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The encrypted external-provider store's contract: what it refuses to store, what it canonicalizes on the way in,
///     what it never writes in plaintext, and the two behaviors an operator would notice immediately if they broke —
///     that renaming a connection does not de-authenticate it, and that a stale editor cannot silently overwrite a
///     concurrent edit.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ExternalProviderStoreTests : IDisposable
{
    // A value no refusal, ToString or payload may ever contain.
    private const string SecretMarker = "v4lue-must-not-leak";

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

    /// <summary>A save replaces the file by rename: a reader holding the previous file keeps its bytes, and no temp file stays behind.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task SaveConnectionAsync_ReplacesTheStoreFileAtomically()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request());
        var before = await File.ReadAllBytesAsync(StorePath);
        await using var previous = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        _ = await SaveAsync(store, Request(id: "second-box", baseUrl: "http://127.0.0.1:18100/v1"));

        var held = new byte[before.Length + 1];
        var read = await previous.ReadAtLeastAsync(held, held.Length, throwOnEndOfStream: false);
        AssertEx.True(before.AsSpan().SequenceEqual(held.AsSpan(0, read)), "an in-place rewrite would change the bytes under the open handle.");
        AssertEx.Equal(expected: 2, (await store.LoadAsync()).Connections.Count);
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
        AssertEx.Empty(Directory.GetFiles(_contentRootPath, "*.tmp"));
    }

    [Test]
    public async Task SaveConnectionAsync_NormalizesTheBaseUrlExactlyOnce()
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(baseUrl: "http://192.168.1.40:8080") with
        {
            AllowInsecureHttp = true
        });

        // The outbound guard pins every request to this stored value, so the canonical /v1/ form has to be what lands
        // on disk — not what the operator happened to type.
        AssertEx.Equal("http://192.168.1.40:8080/v1/", committed.Config.Connections.Single().BaseUrl);
    }

    [Test]
    public async Task SaveConnectionAsync_APlainHttpRemoteAddress_NeedsTheOptIn()
    {
        using var store = CreateStore();

        // HTTPS by default: a LAN or VPN segment is exactly where a plaintext Bearer key is sniffable.
        var exception = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(baseUrl: "http://192.168.1.40:8080/v1", apiKey: "sk-lan")));
        AssertEx.Equal(ExternalProviderTransportPolicy.InsecureRemoteError, exception.Message);
        AssertEx.Empty((await store.LoadAsync()).Connections);

        var committed = await SaveAsync(store, Request(baseUrl: "http://192.168.1.40:8080/v1", apiKey: "sk-lan") with
        {
            AllowInsecureHttp = true
        });

        AssertEx.True(committed.Config.Connections.Single().AllowInsecureHttp);
        AssertEx.True((await store.LoadAsync()).Connections.Single().AllowInsecureHttp);
    }

    [Test]
    [Arguments("http://localhost:18099/v1")]
    [Arguments("http://127.0.0.1:18099/v1")]
    [Arguments("http://127.4.5.6:18099/v1")]
    [Arguments("http://[::1]:18099/v1")]
    [Arguments("https://inference.example.com/v1")]
    public async Task SaveConnectionAsync_LoopbackOrHttps_NeedsNoOptIn(string baseUrl)
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(baseUrl: baseUrl));

        AssertEx.False(committed.Config.Connections.Single().AllowInsecureHttp);
    }

    [Test]
    [Arguments("https://inference.example.com/v1")]
    [Arguments("http://127.0.0.1:18099/v1")]
    public async Task SaveConnectionAsync_DropsTheOptInWhereTheAddressNeedsNone(string baseUrl)
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(baseUrl: baseUrl) with
        {
            AllowInsecureHttp = true
        });

        // A stale tick must not pre-approve plain http the next time the address changes back.
        AssertEx.False(committed.Config.Connections.Single().AllowInsecureHttp);
    }

    [Test]
    public async Task LoadAsync_ALegacyPlainHttpRemoteRow_StillLoadsAndResolves()
    {
        using var store = CreateStore();
        var legacy = JsonSerializer.SerializeToNode(new StoredExternalProviderConfig
        {
            // A real pre-upgrade file: schema 1 AND no allowInsecureHttp, so the lift and the missing field meet.
            SchemaVersion = 1,
            Revision = "r",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "lan-box",
                    DisplayName = "LAN box",
                    BaseUrl = "http://192.168.1.40:8080/v1/",
                    Locality = ExternalProviderLocality.Local,
                    Models =
                    [
                        new StoredExternalProviderModel
                        {
                            WireId = "qwen3"
                        }
                    ]
                }
            ]
        }, RawSerializerOptions)!;
        AssertEx.True(legacy["connections"]![0]!.AsObject().Remove("allowInsecureHttp"), "the fixture must predate the field");
        await File.WriteAllBytesAsync(StorePath, new MockDataProtector().Protect(Encoding.UTF8.GetBytes(legacy.ToJsonString())));

        // The runtime is untouched: only a save is refused, so an upgrade never breaks a working connection.
        var connection = (await store.LoadAsync()).Connections.Single();
        AssertEx.False(connection.AllowInsecureHttp);
        AssertEx.NotNull(await new ExternalProviderRegistry(store).FindRegistrationAsync("ext:lan-box/qwen3", CancellationToken.None));
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
    public async Task LoadAsync_AModelStoredWithoutAnApiSurface_ReadsAsChatCompletions()
    {
        using var store = CreateStore();
        var stored = JsonSerializer.SerializeToNode(new StoredExternalProviderConfig
        {
            Revision = "r",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "http://127.0.0.1:18099/v1/",
                    Locality = ExternalProviderLocality.Local,
                    Models =
                    [
                        new StoredExternalProviderModel
                        {
                            WireId = "qwen3"
                        }
                    ]
                }
            ]
        }, RawSerializerOptions)!;
        AssertEx.True(stored["connections"]![0]!["models"]![0]!.AsObject().Remove("apiSurface"), "the fixture must predate the field");
        await File.WriteAllBytesAsync(StorePath, new MockDataProtector().Protect(Encoding.UTF8.GetBytes(stored.ToJsonString())));

        var registration = AssertEx.NotNull(await new ExternalProviderRegistry(store).FindRegistrationAsync("ext:unsloth-box/qwen3", CancellationToken.None));

        AssertEx.Equal(ExternalProviderApiSurface.ChatCompletions, registration.Model.ApiSurface);
    }

    [Test]
    public async Task SaveConnectionAsync_AResponsesModel_RoundTripsToTheRegistry()
    {
        using var store = CreateStore();

        _ = await SaveAsync(store, Request(models:
        [
            Model("gpt-5.6-terra") with
            {
                ApiSurface = ExternalProviderApiSurface.Responses
            },
            Model("qwen3")
        ]));

        var registry = new ExternalProviderRegistry(store);
        var responses = AssertEx.NotNull(await registry.FindRegistrationAsync("ext:unsloth-box/gpt-5.6-terra", CancellationToken.None));
        var chat = AssertEx.NotNull(await registry.FindRegistrationAsync("ext:unsloth-box/qwen3", CancellationToken.None));
        AssertEx.Equal(ExternalProviderApiSurface.Responses, responses.Model.ApiSurface);
        AssertEx.Equal(ExternalProviderApiSurface.ChatCompletions, chat.Model.ApiSurface);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnUndefinedApiSurface_IsRejected()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models:
            [
                Model("qwen3") with
                {
                    ApiSurface = (ExternalProviderApiSurface)7
                }
            ])));
        AssertEx.Empty((await store.LoadAsync()).Connections);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnUnrecognizedDefaultEffort_IsRejected()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models:
            [
                Model("qwen3") with
                {
                    DefaultReasoningEffort = "extreme"
                }
            ])));
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
            await SaveAsync(store, Request(baseUrl: "http://attacker.example.com/v1", apiKey: null) with
            {
                AllowInsecureHttp = true
            }));

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

        var moved = await SaveAsync(store, Request(baseUrl: "http://127.0.0.1:19000/v1", apiKey: null) with
        {
            ClearApiKey = true
        });

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

        var cleared = await SaveAsync(store, Request(apiKey: null) with
        {
            ClearApiKey = true
        });

        // The only way back from authenticated to keyless — and keyless means NO Authorization header at all.
        AssertEx.Null(cleared.Config.Connections.Single().ApiKey);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAnExplicitClearAndAKey_ClearsRatherThanSets()
    {
        using var store = CreateStore();
        _ = await SaveAsync(store, Request(apiKey: "sk-original"));

        var cleared = await SaveAsync(store, Request(apiKey: "sk-new") with
        {
            ClearApiKey = true
        });

        AssertEx.Null(cleared.Config.Connections.Single().ApiKey);
    }

    [Test]
    public async Task SaveConnectionAsync_WithAStaleRevision_IsSupersededRatherThanOverwriting()
    {
        using var store = CreateStore();
        var first = await SaveAsync(store, Request());
        _ = await SaveAsync(store, Request(displayName: "Edited elsewhere"));

        var result = await store.SaveConnectionAsync(Request(displayName: "Stale editor") with
        {
            ExpectedRevision = first.Config.Revision
        });

        var superseded = result as ExternalProviderWriteResult.Superseded;
        AssertEx.NotNull(superseded);
        AssertEx.Equal("Edited elsewhere", superseded!.Current.Connections.Single().DisplayName);
    }

    [Test]
    public async Task SaveConnectionAsync_WithTheCurrentRevision_Commits()
    {
        using var store = CreateStore();
        var first = await SaveAsync(store, Request());

        var result = await store.SaveConnectionAsync(Request(displayName: "Same editor") with
        {
            ExpectedRevision = first.Config.Revision
        });

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
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task SaveConnectionAsync_WhenRunningOnUnix_CreatesTheFileUserReadWriteOnly()
    {
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

        // A node whose external store will not decrypt has no connections, not unknown ones; the file is moved aside, not
        // deleted, so the next save is not stuck failing forever and restoring the key ring can still recover it.
        AssertEx.Empty(config.Connections);
        AssertEx.False(File.Exists(StorePath));
        var kept = Directory.GetFiles(Path.GetDirectoryName(StorePath)!, Path.GetFileName(StorePath) + ".unreadable-*");
        AssertEx.Equal(expected: 1, kept.Length);
        AssertEx.Equal("not a protected payload", await File.ReadAllTextAsync(kept[0]));
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
            await SaveAsync(store, Request(models:
            [
                Model("qwen3") with
                {
                    SupportsReasoning = false,
                    SupportsReasoningEffort = true
                }
            ])));
    }

    [Test]
    public async Task SaveConnectionAsync_WithADefaultEffortButNoEffortSupport_IsRefused()
    {
        using var store = CreateStore();

        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models:
            [
                Model("qwen3") with
                {
                    SupportsReasoning = true,
                    SupportsReasoningEffort = false,
                    DefaultReasoningEffort = "medium"
                }
            ])));
    }

    [Test]
    public async Task SaveConnectionAsync_WithCoherentReasoningDeclarations_IsAccepted()
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(models:
        [
            Model("qwen3") with
            {
                SupportsReasoning = true,
                SupportsReasoningEffort = true,
                DefaultReasoningEffort = "medium"
            }
        ]));

        AssertEx.Equal("medium", committed.Config.Connections.Single().Models.Single().DefaultReasoningEffort);
    }

    [Test]
    public async Task SaveConnectionAsync_WithEffortOnAnUnknownReasoningModel_IsRefused()
    {
        using var store = CreateStore();

        // Unknown is not "supported": an effort needs reasoning declared Yes, exactly as before the tri-state.
        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models:
            [
                Model("qwen3") with
                {
                    SupportsReasoning = null,
                    SupportsReasoningEffort = true
                }
            ])));
        _ = await AssertEx.ThrowsAsync<ExternalProviderValidationException>(async () =>
            await SaveAsync(store, Request(models:
            [
                Model("qwen3") with
                {
                    SupportsReasoning = true,
                    SupportsReasoningEffort = null,
                    DefaultReasoningEffort = "medium"
                }
            ])));
    }

    [Test]
    public async Task LoadAsync_ASchema1File_ReadsFalseAsUnknownAndKeepsTrue()
    {
        using var store = CreateStore();
        await WriteRawAsync(new StoredExternalProviderConfig
        {
            SchemaVersion = 1,
            Revision = "r",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "http://127.0.0.1:18099/v1/",
                    Locality = ExternalProviderLocality.Local,
                    Models =
                    [
                        new StoredExternalProviderModel
                        {
                            WireId = "qwen3",
                            SupportsTools = true,
                            SupportsVision = false,
                            SupportsReasoning = false,
                            SupportsReasoningEffort = false
                        }
                    ]
                }
            ]
        });

        var model = (await store.LoadAsync()).Connections.Single().Models.Single();

        // Schema 1 stored an unchecked box as false, which never meant "unsupported".
        AssertEx.True(model.SupportsTools == true);
        AssertEx.Null(model.SupportsVision);
        AssertEx.Null(model.SupportsReasoning);
        AssertEx.Null(model.SupportsReasoningEffort);
    }

    [Test]
    public async Task SaveConnectionAsync_OverASchema1File_WritesTheCurrentSchemaAndKeepsTheTriState()
    {
        using var store = CreateStore();
        await WriteRawAsync(new StoredExternalProviderConfig
        {
            SchemaVersion = 1,
            Revision = "r",
            Connections = []
        });

        // The revision read off the schema-1 file is what the CAS compares against across the lift.
        _ = await SaveAsync(store, Request(models:
            [
                Model("qwen3") with
                {
                    SupportsTools = false,
                    SupportsVision = null,
                    SupportsReasoning = true
                }
            ]) with
            {
                ExpectedRevision = "r"
            });

        var onDisk = JsonSerializer.Deserialize<StoredExternalProviderConfig>(new MockDataProtector().Unprotect(await File.ReadAllBytesAsync(StorePath)),
            RawSerializerOptions)!;
        AssertEx.Equal(ExternalProviderStoreSchema.CurrentVersion, onDisk.SchemaVersion);
        var model = (await store.LoadAsync()).Connections.Single().Models.Single();
        AssertEx.True(model.SupportsTools == false, "an explicit No survives a current-schema round-trip");
        AssertEx.Null(model.SupportsVision);
        AssertEx.True(model.SupportsReasoning == true);
    }

    [Test]
    public async Task SaveConnectionAsync_OverASchema1File_WithAStaleRevision_IsSupersededAndLeavesTheFileAtSchema1()
    {
        using var store = CreateStore();
        await WriteRawAsync(new StoredExternalProviderConfig
        {
            SchemaVersion = 1,
            Revision = "r",
            Connections = []
        });

        var result = await store.SaveConnectionAsync(Request() with
        {
            ExpectedRevision = "stale"
        });

        AssertEx.True(result is ExternalProviderWriteResult.Superseded);
        var onDisk = JsonSerializer.Deserialize<StoredExternalProviderConfig>(new MockDataProtector().Unprotect(await File.ReadAllBytesAsync(StorePath)),
            RawSerializerOptions)!;
        AssertEx.Equal(1, onDisk.SchemaVersion, "a refused write persists nothing, the lift included.");
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

    [Test]
    public void StoredConnection_ToString_RedactsTheKey()
    {
        var connection = new StoredExternalProviderConnection
        {
            Id = "gw",
            DisplayName = "Gateway",
            BaseUrl = "https://gateway.example.com/v1/",
            Locality = ExternalProviderLocality.Cloud,
            ApiKey = SecretMarker + "-key"
        };

        var printed = connection.ToString();

        AssertEx.False(printed.Contains(SecretMarker, StringComparison.Ordinal), printed);
        AssertEx.Contains(printed, "[REDACTED]");
    }

    [Test]
    public async Task LoadAsync_ASchema2File_Loads_AndTheNextSaveWritesTheCurrentSchema()
    {
        using var store = CreateStore();
        var schema2 = JsonSerializer.SerializeToNode(new StoredExternalProviderConfig
        {
            SchemaVersion = 2,
            Revision = "r",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "http://127.0.0.1:18099/v1/",
                    Locality = ExternalProviderLocality.Local,
                    ApiKey = "sk-kept"
                }
            ]
        }, RawSerializerOptions)!;
        await File.WriteAllBytesAsync(StorePath, new MockDataProtector().Protect(Encoding.UTF8.GetBytes(schema2.ToJsonString())));

        var loaded = (await store.LoadAsync()).Connections.Single();
        AssertEx.Equal("sk-kept", loaded.ApiKey);

        // A real edit: an identical resave is skipped and would leave the file untouched.
        _ = await SaveAsync(store, Request(displayName: "Renamed box") with
        {
            ExpectedRevision = "r"
        });
        var onDisk = JsonSerializer.Deserialize<StoredExternalProviderConfig>(new MockDataProtector().Unprotect(await File.ReadAllBytesAsync(StorePath)),
            RawSerializerOptions)!;
        AssertEx.Equal(3, onDisk.SchemaVersion);
        AssertEx.Equal("sk-kept", onDisk.Connections.Single().ApiKey);
    }

    [Test]
    public async Task LoadAsync_ASchema3FileWithRetiredHeaders_Loads_AndTheNextSaveWritesNoHeaders()
    {
        using var store = CreateStore();
        var schema3 = JsonSerializer.SerializeToNode(new StoredExternalProviderConfig
        {
            SchemaVersion = 3,
            Revision = "r",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "http://127.0.0.1:18099/v1/",
                    Locality = ExternalProviderLocality.Local,
                    ApiKey = "sk-kept"
                }
            ]
        }, RawSerializerOptions)!;
        // The shape an earlier build wrote while connections carried custom headers.
        schema3["connections"]![0]!["headers"] = new JsonArray(new JsonObject
        {
            ["name"] = "X-Example",
            ["value"] = SecretMarker,
            ["isSecret"] = true
        });
        await File.WriteAllBytesAsync(StorePath, new MockDataProtector().Protect(Encoding.UTF8.GetBytes(schema3.ToJsonString())));

        var loaded = (await store.LoadAsync()).Connections.Single();
        AssertEx.Equal("sk-kept", loaded.ApiKey);

        // A real edit: an identical resave is skipped and would leave the file untouched.
        _ = await SaveAsync(store, Request(displayName: "Renamed box") with
        {
            ExpectedRevision = "r"
        });
        var onDisk = Encoding.UTF8.GetString(new MockDataProtector().Unprotect(await File.ReadAllBytesAsync(StorePath)));
        AssertEx.False(onDisk.Contains("\"headers\"", StringComparison.OrdinalIgnoreCase), onDisk);
        AssertEx.False(onDisk.Contains(SecretMarker, StringComparison.Ordinal), "the retired header value is dropped by the save");
        AssertEx.Contains(onDisk, "sk-kept");
    }

    [Test]
    public async Task SaveConnectionAsync_CloudGrants_RoundTripThroughTheFileAndOntoTheDescriptor()
    {
        var grants = new ExternalProviderCloudGrants
        {
            WebTools = true,
            McpTools = true
        };
        using (var store = CreateStore())
        {
            _ = await SaveAsync(store, Request(baseUrl: "https://gateway.example.com/v1",
                    locality: ExternalProviderLocality.Cloud,
                    models: [Model("qwen3")]) with
                {
                    CloudGrants = grants
                });
        }

        // A fresh store instance reads the file back, so this is the encrypted payload, not an in-memory copy.
        using var reread = CreateStore();
        AssertEx.Equal(grants, (await reread.LoadAsync()).Connections.Single().CloudGrants);
        var registration = AssertEx.NotNull(await new ExternalProviderRegistry(reread).FindRegistrationAsync("ext:unsloth-box/qwen3", CancellationToken.None));
        AssertEx.Equal(grants, registration.Connection.CloudGrants);
    }

    [Test]
    public async Task SaveConnectionAsync_ALocalConnection_StoresNoGrants()
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(locality: ExternalProviderLocality.Local) with
        {
            CloudGrants = AllGrants
        });

        // A later flip to Cloud must start closed, not inherit a tick the operator could not see while Trust was Local.
        AssertEx.Equal(ExternalProviderCloudGrants.None, committed.Config.Connections.Single().CloudGrants);
    }

    [Test]
    public async Task SaveConnectionAsync_ACloudConnectionWithoutGrants_StoresNone()
    {
        using var store = CreateStore();

        var committed = await SaveAsync(store, Request(baseUrl: "https://gateway.example.com/v1", locality: ExternalProviderLocality.Cloud));

        AssertEx.Equal(ExternalProviderCloudGrants.None, committed.Config.Connections.Single().CloudGrants);
    }

    [Test]
    public async Task SaveConnectionAsync_AGrantChange_IsWrittenAndAnIdenticalResaveIsNot()
    {
        using var store = CreateStore();
        var request = Request(baseUrl: "https://gateway.example.com/v1", locality: ExternalProviderLocality.Cloud);
        _ = await SaveAsync(store, request);

        var granted = await SaveAsync(store, request with
        {
            CloudGrants = new ExternalProviderCloudGrants
            {
                SubAgents = true
            }
        });
        var again = await SaveAsync(store, request with
        {
            CloudGrants = new ExternalProviderCloudGrants
            {
                SubAgents = true
            }
        });

        AssertEx.True(granted.Changed, "a grant is operator configuration, so changing it must be written");
        AssertEx.False(again.Changed);
    }

    [Test]
    public async Task LoadAsync_ARowWrittenBeforeGrants_ReadsNoneAndAMatchingResaveIsSkipped()
    {
        using var store = CreateStore();
        var legacy = JsonSerializer.SerializeToNode(new StoredExternalProviderConfig
        {
            SchemaVersion = ExternalProviderStoreSchema.CurrentVersion,
            Revision = "r",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "https://gateway.example.com/v1/",
                    Locality = ExternalProviderLocality.Cloud,
                    Models =
                    [
                        new StoredExternalProviderModel
                        {
                            WireId = "qwen3",
                            // What Model("qwen3") saves, so the grants are the only field the resave could differ in.
                            SupportsTools = false
                        }
                    ]
                }
            ]
        }, RawSerializerOptions)!;
        AssertEx.True(legacy["connections"]![0]!.AsObject().Remove("cloudGrants"), "the fixture must predate the field");
        await File.WriteAllBytesAsync(StorePath, new MockDataProtector().Protect(Encoding.UTF8.GetBytes(legacy.ToJsonString())));

        AssertEx.Null((await store.LoadAsync()).Connections.Single().CloudGrants);
        var registration = AssertEx.NotNull(await new ExternalProviderRegistry(store).FindRegistrationAsync("ext:unsloth-box/qwen3", CancellationToken.None));
        AssertEx.Equal(ExternalProviderCloudGrants.None, registration.Connection.CloudGrants);

        // Null and None are the same grants, so the reconciler's idempotent resave does not churn the file.
        var resave = await SaveAsync(store, Request(baseUrl: "https://gateway.example.com/v1",
                locality: ExternalProviderLocality.Cloud,
                models: [Model("qwen3")]) with
            {
                ExpectedRevision = "r"
            });
        AssertEx.False(resave.Changed);
    }

    [Test]
    public void ToString_PrintsTheGrants()
    {
        var connection = new StoredExternalProviderConnection
        {
            Id = "gw",
            DisplayName = "Gateway",
            BaseUrl = "https://gateway.example.com/v1/",
            ApiKey = SecretMarker,
            Locality = ExternalProviderLocality.Cloud,
            CloudGrants = new ExternalProviderCloudGrants
            {
                McpTools = true
            }
        };

        var printed = connection.ToString();

        AssertEx.Contains(printed, "McpTools = True");
        AssertEx.False(printed.Contains(SecretMarker, StringComparison.Ordinal), "the key stays redacted beside the grants");
    }

    private static ExternalProviderCloudGrants AllGrants =>
        new()
        {
            LocalData = true,
            UnattendedRuns = true,
            WebTools = true,
            McpTools = true,
            SubAgents = true
        };

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
