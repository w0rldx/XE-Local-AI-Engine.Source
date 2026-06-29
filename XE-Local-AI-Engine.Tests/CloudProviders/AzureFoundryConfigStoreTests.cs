namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Covers the v2 multi-model / managed-identity config surface of <see cref="CloudCredentialStore" />: the
///     legacy-v1 → v2 migration (HIGH-2, must NOT delete a liftable file), the v2 round-trip, and
///     <c>ValidateConfig</c> (MEDIUM-1/MEDIUM-4: API key required only for the ApiKey auth mode, ≥1 model, Azure host
///     allowlist, HTTPS).
/// </summary>
public sealed class AzureFoundryConfigStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    [Test]
    public async Task LoadConfigAsync_WhenLegacyV1Payload_LiftsToOneModelConnection_AndDoesNotDeleteFile()
    {
        // HIGH-2: a tester's pre-existing flat single-deployment blob must be lifted, not silently dropped or deleted.
        Directory.CreateDirectory(_contentRootPath);
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new
            {
                providerName = "AzureFoundry",
                endpoint = "https://example.openai.azure.com/",
                apiKey = "legacy-key",
                deploymentName = "gpt-4o"
            },
            JsonOptions);
        await File.WriteAllBytesAsync(GetCredentialsPath(), legacy);
        using var store = CreateStore();

        var config = await store.LoadConfigAsync();

        var connection = AssertEx.NotNull(config?.AzureFoundry);
        AssertEx.Equal("https://example.openai.azure.com/", connection.Endpoint);
        AssertEx.Equal(AzureFoundryAuthMode.ApiKey, connection.AuthMode);
        AssertEx.Equal("legacy-key", connection.ApiKey);
        AssertEx.Equal("gpt-4o", connection.Models.Single().DeploymentName);
        AssertEx.True(File.Exists(GetCredentialsPath()), "a liftable legacy payload must NOT be deleted");
    }

    [Test]
    public async Task SaveConfigAsync_ThenLoadConfigAsync_RoundTripsManagedIdentityMultiModel()
    {
        using var store = CreateStore();
        var config = new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.services.ai.azure.com/",
                AuthMode = AzureFoundryAuthMode.ManagedIdentity,
                ApiKey = null,
                Models =
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = "gpt-4o",
                        DisplayLabel = "GPT-4o"
                    },
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = "gpt-4o-mini"
                    }
                ]
            }
        };

        await store.SaveConfigAsync(config);
        var loaded = await store.LoadConfigAsync();

        var connection = AssertEx.NotNull(loaded?.AzureFoundry);
        AssertEx.Equal(AzureFoundryAuthMode.ManagedIdentity, connection.AuthMode);
        AssertEx.Null(connection.ApiKey);
        AssertEx.Equal(expected: 2, connection.Models.Count);
    }

    [Test]
    public async Task LoadConfigAsync_WhenBlobIsNotJson_ReturnsNullAndClearsFile()
    {
        // A genuinely corrupt (non-JSON) blob is the ONLY case that reaches the destructive clear.
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllBytesAsync(GetCredentialsPath(), [0x01, 0x02, 0x03]);
        using var store = CreateStore();

        var config = await store.LoadConfigAsync();

        AssertEx.Null(config);
        AssertEx.False(File.Exists(GetCredentialsPath()));
    }

    [Test]
    public async Task SaveConfigAsync_WhenApiKeyModeMissingKey_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var config = CreateConfig(AzureFoundryAuthMode.ApiKey, apiKey: null);

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenManagedIdentityWithoutKey_Succeeds()
    {
        // MEDIUM-1: a managed-identity connection carries no key and must save without error.
        using var store = CreateStore();
        var config = CreateConfig(AzureFoundryAuthMode.ManagedIdentity, apiKey: null);

        await store.SaveConfigAsync(config);

        AssertEx.NotNull((await store.LoadConfigAsync())?.AzureFoundry);
    }

    [Test]
    public async Task SaveConfigAsync_WhenNoModels_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var config = CreateConfig(AzureFoundryAuthMode.ApiKey, apiKey: "k", models: []);

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenHostNotAllowlisted_ThrowsArgumentException()
    {
        // MEDIUM-4: an Entra token (managed identity) must not be sendable to an arbitrary HTTPS host.
        using var store = CreateStore();
        var config = CreateConfig(AzureFoundryAuthMode.ManagedIdentity, apiKey: null, endpoint: "https://evil.example.com/");

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenEndpointNotHttps_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var config = CreateConfig(AzureFoundryAuthMode.ApiKey, apiKey: "k", endpoint: "http://example.openai.azure.com/");

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    private static StoredCloudProviderConfig CreateConfig(AzureFoundryAuthMode authMode,
        string? apiKey,
        string endpoint = "https://example.openai.azure.com/",
        IReadOnlyList<StoredAzureFoundryModel>? models = null)
    {
        return new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = endpoint,
                AuthMode = authMode,
                ApiKey = apiKey,
                Models = models ??
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = "gpt-4o"
                    }
                ]
            }
        };
    }

    private CloudCredentialStore CreateStore()
    {
        Directory.CreateDirectory(_contentRootPath);
        return new CloudCredentialStore(new MockDataProtector(),
            new FakeNodeDataDirectory(_contentRootPath),
            NullLogger<CloudCredentialStore>.Instance);
    }

    private string GetCredentialsPath()
    {
        return Path.Combine(_contentRootPath, "cloud-credentials.enc");
    }
}
