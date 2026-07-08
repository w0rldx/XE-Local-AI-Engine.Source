namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Fallback-path coverage for the authorization-code flow's MSAL persistent-cache registration. Unlike the
///     device-code / interactive-browser fallbacks (which wrap Azure.Identity's <c>DeviceCodeCredential</c> /
///     <c>InteractiveBrowserCredential</c> — sealed-shaped SDK types with no seam to force a persistence failure
///     without a live tenant), this class calls the real MSAL persistence API directly, so the test below drives the
///     REAL code path end-to-end rather than faking anything: it must never throw regardless of whether THIS
///     machine's keyring/Secret-Service/DPAPI actually works, which is exactly the contract
///     <see cref="EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync" /> promises. On a
///     Secret-Service-less Linux box (e.g. this CI/dev sandbox) this genuinely exercises the persistence-unavailable
///     branch; on a box with a working keyring it exercises the happy path — both must be silent.
/// </summary>
public sealed class EntraAuthCodeConfidentialClientFactoryTests : IDisposable
{
    private readonly string _dataDirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectoryPath))
        {
            Directory.Delete(_dataDirectoryPath, recursive: true);
        }
    }

    [Test]
    public async Task TryRegisterPersistentCacheAsync_NeverThrows_RegardlessOfPlatformPersistenceAvailability()
    {
        Directory.CreateDirectory(_dataDirectoryPath);
        var app = EntraAuthCodeConfidentialClientFactory.Build("tenant-id", "client-id", "client-secret", "http://localhost:53682/signin-oidc");

        // Must complete without throwing — a broken/absent OS-native persistence backend (no org.freedesktop.secrets,
        // no Keychain, no DPAPI) is an accepted degraded mode (in-memory cache, logged), never an escaped exception.
        await EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(app,
            new FakeNodeDataDirectory(_dataDirectoryPath),
            NullLogger.Instance);
    }

    [Test]
    public async Task TryRegisterPersistentCacheAsync_WhenCalledTwiceForTheSameApp_NeverThrows()
    {
        // Mirrors a real sequence: the coordinator registers persistence during redemption, and the chat-client
        // factory's silent-rebuild path registers it again against a freshly-built confidential client app later.
        Directory.CreateDirectory(_dataDirectoryPath);
        var dataDirectory = new FakeNodeDataDirectory(_dataDirectoryPath);
        var first = EntraAuthCodeConfidentialClientFactory.Build("tenant-id", "client-id", "client-secret", "http://localhost:53682/signin-oidc");
        var second = EntraAuthCodeConfidentialClientFactory.Build("tenant-id", "client-id", "client-secret", "http://localhost:53682/signin-oidc");

        await EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(first, dataDirectory, NullLogger.Instance);
        await EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(second, dataDirectory, NullLogger.Instance);
    }
}
