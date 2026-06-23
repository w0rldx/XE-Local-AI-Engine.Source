namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class CloudCredentialStoreTests : IDisposable
{
    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    [Test]
    public async Task SaveAsync_WhenCredentialsAreValid_PersistsAndLoadsCredentials()
    {
        using var store = CreateStore();
        var credentials = CreateCredentials();

        await store.SaveAsync(credentials);
        var loaded = await store.LoadAsync();

        var loadedCredentials = AssertEx.NotNull(loaded);
        AssertEx.Equal("AzureFoundry", loadedCredentials.ProviderName);
        AssertEx.Equal("https://example.openai.azure.com/", loadedCredentials.Endpoint);
        AssertEx.Equal("test-api-key", loadedCredentials.ApiKey);
        AssertEx.Equal("gpt-4o", loadedCredentials.DeploymentName);
    }

    [Test]
    public async Task SaveAsync_WhenUsingDataProtection_DoesNotWriteApiKeyInPlaintext()
    {
        using var store = CreateStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_contentRootPath, "keys"))));

        await store.SaveAsync(CreateCredentials());

        var payload = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(GetCredentialsPath()));
        AssertEx.False(payload.Contains("test-api-key", StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveAsync_WhenRunningOnUnix_SetsCredentialFileModeToUserReadWrite()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var store = CreateStore();

        await store.SaveAsync(CreateCredentials());

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(GetCredentialsPath()));
    }

    [Test]
    public async Task ClearAsync_WhenCredentialsExist_RemovesCredentialFile()
    {
        using var store = CreateStore();
        await store.SaveAsync(CreateCredentials());

        await store.ClearAsync();

        AssertEx.Null(await store.LoadAsync());
        AssertEx.False(File.Exists(GetCredentialsPath()));
    }

    [Test]
    public async Task SaveAsync_WhenEndpointIsNotHttps_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var credentials = CreateCredentials(endpoint: "http://example.openai.azure.com/");

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveAsync(credentials));
    }

    [Test]
    public async Task SaveAsync_WhenProviderIsUnknown_ThrowsArgumentException()
    {
        using var store = CreateStore();
        var credentials = CreateCredentials("OtherCloud");

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SaveAsync(credentials));
    }

    [Test]
    public async Task LoadAsync_WhenCredentialFileIsCorrupted_ReturnsNullAndClearsFile()
    {
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllBytesAsync(GetCredentialsPath(), [1, 2, 3]);
        var protector = Substitute.For<IDataProtector>();
        protector.CreateProtector(Arg.Any<string>()).Returns(protector);
        protector.Unprotect(Arg.Any<byte[]>()).Returns(_ => throw new CryptographicException("boom"));
        using var store = CreateStore(protector);

        var loaded = await store.LoadAsync();

        AssertEx.Null(loaded);
        AssertEx.False(File.Exists(GetCredentialsPath()));
    }

    private CloudCredentialStore CreateStore(IDataProtectionProvider? dataProtectionProvider = null)
    {
        Directory.CreateDirectory(_contentRootPath);

        return new CloudCredentialStore(dataProtectionProvider ?? new MockDataProtector(),
            new FakeNodeDataDirectory(_contentRootPath),
            NullLogger<CloudCredentialStore>.Instance);
    }

    private string GetCredentialsPath()
    {
        return Path.Combine(_contentRootPath, "cloud-credentials.enc");
    }

    private static StoredCloudCredentials CreateCredentials(string providerName = "AzureFoundry", string endpoint = "https://example.openai.azure.com/")
    {
        return new StoredCloudCredentials
        {
            ProviderName = providerName,
            Endpoint = endpoint,
            ApiKey = "test-api-key",
            DeploymentName = "gpt-4o"
        };
    }
}
