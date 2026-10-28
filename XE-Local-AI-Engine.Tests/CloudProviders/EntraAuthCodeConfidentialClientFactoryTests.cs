namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Fallback-path coverage for the authorization-code flow's MSAL persistent-cache registration:
///     <see cref="EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync" /> must never throw, whether
///     or not this machine's keyring, Secret Service or DPAPI works.
/// </summary>
/// <remarks>
///     Unlike the device-code / interactive-browser fallbacks, which wrap sealed-shaped Azure.Identity credentials with
///     no seam to force a persistence failure, this class calls the real MSAL persistence API, so the test drives the
///     REAL code path without fakes. A Secret-Service-less Linux host (e.g. a CI runner) exercises the
///     persistence-unavailable branch; a host with a working keyring exercises the happy path.
/// </remarks>
[Category(TestCategories.Unit)]
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

        var logger = new RecordingLogger<EntraAuthCodeConfidentialClientFactoryTests>();

        // A broken/absent OS-native persistence backend (no org.freedesktop.secrets, no Keychain, no DPAPI) is an
        // accepted degraded mode (in-memory cache, logged), never an escaped exception.
        await AssertEx.CompletesAsync(EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(app, new FakeNodeDataDirectory(_dataDirectoryPath), logger),
            TestBudgets.Contended,
            "registration returns whether or not the platform can persist the cache.");

        AssertRegisteredOrDegradedWithOneWarningEach(logger, calls: 1);
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

        var logger = new RecordingLogger<EntraAuthCodeConfidentialClientFactoryTests>();

        await AssertEx.CompletesAsync(EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(first, dataDirectory, logger),
            TestBudgets.Contended,
            "the first registration returns.");
        await AssertEx.CompletesAsync(EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(second, dataDirectory, logger),
            TestBudgets.Contended,
            "a second registration against the same cache file returns too.");

        AssertRegisteredOrDegradedWithOneWarningEach(logger, calls: 2);
    }

    /// <summary>
    ///     The platform decides which branch runs, so the outcome is one of exactly two shapes: every call registered
    ///     silently, or every call logged the one in-memory fallback warning. Anything else escaped the contract.
    /// </summary>
    private static void AssertRegisteredOrDegradedWithOneWarningEach(RecordingLogger<EntraAuthCodeConfidentialClientFactoryTests> logger, int calls)
    {
        AssertEx.True(logger.Entries.Count is 0 || logger.Entries.Count == calls,
            $"either every registration persisted or every one degraded; logged {logger.Entries.Count} entries for {calls} calls.");
        AssertEx.True(logger.Entries.All(static entry => entry.Level == LogLevel.Warning && entry.Message.Contains("in-memory", StringComparison.Ordinal)),
            "the only thing a registration may log is the in-memory fallback warning.");
    }
}
