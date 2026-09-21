namespace XE_Local_AI_Engine.Tests.Auth;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     <see cref="TokenStore" /> is read-only since the Central Platform pairing flow that wrote
///     <c>worker-credentials.enc</c> was removed: nothing creates, updates or deletes the file any more. These tests
///     pin the two reads a shipped node still depends on — a node that never paired answers null (so
///     <c>AgentHomeIdentityProvider</c> falls back to the loopback identity), and one whose file an earlier build
///     wrote keeps answering with the node id it was issued.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TokenStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    [Test]
    public async Task GetClientNodeIdAsync_WhenNothingStored_ReturnsNull()
    {
        var tokenStore = CreateTokenStore();

        AssertEx.Null(await tokenStore.GetClientNodeIdAsync());
        AssertEx.Null(await tokenStore.GetAccessTokenAsync());
    }

    [Test]
    public async Task GetClientNodeIdAsync_WhenCredentialsFileExists_ReturnsTheStoredNodeId()
    {
        var clientNodeId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        WriteCredentials(clientNodeId, "stored-access-token", DateTimeOffset.UtcNow.AddHours(1));

        var tokenStore = CreateTokenStore();

        AssertEx.Equal(clientNodeId, await tokenStore.GetClientNodeIdAsync());
        AssertEx.Equal("stored-access-token", await tokenStore.GetAccessTokenAsync());
    }

    [Test]
    public async Task GetAccessTokenAsync_WhenStoredTokenHasExpired_ReturnsNullButKeepsTheNodeId()
    {
        var clientNodeId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        WriteCredentials(clientNodeId, "stale-access-token", now.AddMinutes(-1));

        var tokenStore = CreateTokenStore(new FixedTimeProvider(now));

        AssertEx.Null(await tokenStore.GetAccessTokenAsync());
        // The identity outlives the token: AgentHomeIdentityProvider keys on the node id, not on a live session.
        AssertEx.Equal(clientNodeId, await tokenStore.GetClientNodeIdAsync());
    }

    [Test]
    public async Task GetClientNodeIdAsync_WhenTheCredentialsFileIsUnreadable_ReportsUnpaired()
    {
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllBytesAsync(GetCredentialsPath(), Encoding.UTF8.GetBytes("not a protected payload"));

        var tokenStore = CreateTokenStore();

        AssertEx.Null(await tokenStore.GetClientNodeIdAsync());
        AssertEx.Null(await tokenStore.GetAccessTokenAsync());
        // Read-only: an unreadable file is left exactly where it was rather than deleted.
        AssertEx.True(File.Exists(GetCredentialsPath()));
    }

    private TokenStore CreateTokenStore(TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(_contentRootPath);

        return new TokenStore(new MockDataProtector(),
            new FakeNodeDataDirectory(_contentRootPath),
            NullLogger<TokenStore>.Instance,
            timeProvider ?? TimeProvider.System);
    }

    private void WriteCredentials(Guid clientNodeId, string accessToken, DateTimeOffset expiresAt)
    {
        Directory.CreateDirectory(_contentRootPath);

        // The exact envelope the removed pairing flow wrote, so this proves the store still reads a real install's file.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            clientNodeId,
            accessToken,
            refreshToken = "stored-refresh-token",
            expiresAt,
            bindingMethod = "pairing-token",
            autoConnectOnStart = false,
            lastKnownNodeName = "XE-Local-Worker-01"
        }, SerializerOptions);

        var protector = new MockDataProtector().CreateProtector("WorkerNode.TokenStore.v1");
        File.WriteAllBytes(GetCredentialsPath(), protector.Protect(payload));
    }

    private string GetCredentialsPath()
    {
        return Path.Combine(_contentRootPath, "worker-credentials.enc");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
