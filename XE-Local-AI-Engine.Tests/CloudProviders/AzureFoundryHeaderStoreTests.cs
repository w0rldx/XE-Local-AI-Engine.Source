namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Covers the header + host-suffix surface of <see cref="CloudCredentialStore" />: the encrypted round-trip
///     (including secret values and operator suffixes), a v2 blob with no Headers field defaulting to empty (legacy
///     load), and the defense-in-depth <c>ValidateConfig</c> rejections (Locked #6–#10, #14).
/// </summary>
public sealed class AzureFoundryHeaderStoreTests : IDisposable
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
    public async Task SaveConfigAsync_ThenLoadConfigAsync_RoundTripsHeadersAndHostSuffixes()
    {
        using var store = CreateStore();
        var config = new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://gateway.azure-api.net/",
                AuthMode = AzureFoundryAuthMode.ApiKey,
                ApiKey = "k",
                Models = [new StoredAzureFoundryModel { DeploymentName = "gpt-4o" }],
                Headers =
                [
                    new StoredAzureFoundryHeader { Name = "Ocp-Apim-Subscription-Key", Value = "sub-secret", IsSecret = true },
                    new StoredAzureFoundryHeader { Name = "X-Tenant", Value = "tenant-a", IsSecret = false }
                ],
                AdditionalAllowedHostSuffixes = [".azure-api.net"]
            }
        };

        await store.SaveConfigAsync(config);
        var loaded = await store.LoadConfigAsync();

        var connection = AssertEx.NotNull(loaded?.AzureFoundry);
        AssertEx.Equal(expected: 2, connection.Headers.Count);
        var secret = AssertEx.NotNull(connection.Headers.FirstOrDefault(header => header.Name == "Ocp-Apim-Subscription-Key"));
        AssertEx.Equal("sub-secret", secret.Value);
        AssertEx.True(secret.IsSecret);
        AssertEx.Equal(".azure-api.net", connection.AdditionalAllowedHostSuffixes.Single());
    }

    [Test]
    public async Task LoadConfigAsync_WhenV2BlobHasNoHeadersField_DefaultsToEmpty()
    {
        Directory.CreateDirectory(_contentRootPath);
        var blob = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                providerName = "AzureFoundry",
                azureFoundry = new
                {
                    endpoint = "https://example.openai.azure.com/",
                    authMode = (int)AzureFoundryAuthMode.ApiKey,
                    apiKey = "k",
                    models = new[] { new { deploymentName = "gpt-4o" } }
                }
            },
            JsonOptions);
        await File.WriteAllBytesAsync(GetCredentialsPath(), blob);
        using var store = CreateStore();

        var config = await store.LoadConfigAsync();

        var connection = AssertEx.NotNull(config?.AzureFoundry);
        AssertEx.Empty(connection.Headers);
        AssertEx.Empty(connection.AdditionalAllowedHostSuffixes);
    }

    [Test]
    public async Task SaveConfigAsync_WhenEndpointHostNotInEffectiveAllowlist_ThrowsArgumentException()
    {
        // A custom (non-Azure) endpoint host with no covering suffix must be rejected (Locked #14).
        using var store = CreateStore();
        var config = CreateConfig(connection => connection with
        {
            Endpoint = "https://gateway.azure-api.net/"
        });

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenReservedHeaderName_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var config = CreateConfig(connection => connection with
        {
            Headers = [new StoredAzureFoundryHeader { Name = "authorization", Value = "x", IsSecret = false }]
        });

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenHeaderValueHasControlChar_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var config = CreateConfig(connection => connection with
        {
            Headers = [new StoredAzureFoundryHeader { Name = "X-Inject", Value = "a\r\nEvil: 1", IsSecret = false }]
        });

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenSecretHeaderHasNoValue_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var config = CreateConfig(connection => connection with
        {
            Headers = [new StoredAzureFoundryHeader { Name = "X-Secret", Value = null, IsSecret = true }]
        });

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenOverHeaderCap_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var headers = Enumerable.Range(0, AzureFoundryHeaderRules.MaxHeaderCount + 1)
            .Select(index => new StoredAzureFoundryHeader { Name = $"X-H{index}", Value = "v", IsSecret = false })
            .ToArray();
        var config = CreateConfig(connection => connection with { Headers = headers });

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    [Test]
    public async Task SaveConfigAsync_WhenMalformedHostSuffix_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var config = CreateConfig(connection => connection with
        {
            AdditionalAllowedHostSuffixes = [".com"]
        });

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveConfigAsync(config));
    }

    private static StoredCloudProviderConfig CreateConfig(Func<StoredAzureFoundryConnection, StoredAzureFoundryConnection> mutate)
    {
        var connection = new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiKey = "k",
            Models = [new StoredAzureFoundryModel { DeploymentName = "gpt-4o" }]
        };

        return new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = mutate(connection)
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
