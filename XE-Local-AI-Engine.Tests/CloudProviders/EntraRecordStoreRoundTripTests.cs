namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Text;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     The two Entra record stores write through the shared atomic replace; a save, an overwrite and a load round-trip.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class EntraRecordStoreRoundTripTests
{
    [Test]
    public async Task EntraAuthCodeAccountStore_SaveOverwriteLoad_ReturnsTheLatestValue()
    {
        using var temp = new TempDirectory("xe-entra-store");
        using var store = new EntraAuthCodeAccountStore(new MockDataProtector(),
            new FakeNodeDataDirectory(temp.Path),
            NullLogger<EntraAuthCodeAccountStore>.Instance);

        await store.SaveHomeAccountIdAsync("first-account");
        await store.SaveHomeAccountIdAsync("second-account");

        AssertEx.Equal("second-account", await store.LoadHomeAccountIdAsync());
        AssertEx.Equal(expected: 1, Directory.GetFiles(temp.Path).Length);
    }

    [Test]
    public async Task EntraTokenCacheStore_SaveOverwriteLoad_ReturnsTheLatestRecord()
    {
        using var temp = new TempDirectory("xe-entra-store");
        using var store = new EntraTokenCacheStore(new MockDataProtector(),
            new FakeNodeDataDirectory(temp.Path),
            NullLogger<EntraTokenCacheStore>.Instance);

        await store.SaveRecordAsync(CreateRecord("first@contoso.com"));
        await store.SaveRecordAsync(CreateRecord("second@contoso.com"));

        var loaded = AssertEx.NotNull(await store.LoadRecordAsync());
        AssertEx.Equal("second@contoso.com", loaded.Username);
        AssertEx.Equal(expected: 1, Directory.GetFiles(temp.Path).Length);
    }

    [Test]
    public async Task EntraAuthCodeAccountStore_WhenTheKeyRingChanged_MovesTheRecordAsideInsteadOfDeletingIt()
    {
        using var temp = new TempDirectory("xe-entra-store");
        using (var writer = new EntraAuthCodeAccountStore(new EphemeralDataProtectionProvider(),
                   new FakeNodeDataDirectory(temp.Path),
                   NullLogger<EntraAuthCodeAccountStore>.Instance))
        {
            await writer.SaveHomeAccountIdAsync("account");
        }

        using var reader = new EntraAuthCodeAccountStore(new EphemeralDataProtectionProvider(),
            new FakeNodeDataDirectory(temp.Path),
            NullLogger<EntraAuthCodeAccountStore>.Instance);

        AssertEx.Null(await reader.LoadHomeAccountIdAsync());
        AssertOnlyAMovedAsideCopyRemains(temp.Path);
    }

    [Test]
    public async Task EntraTokenCacheStore_WhenTheKeyRingChanged_MovesTheRecordAsideInsteadOfDeletingIt()
    {
        using var temp = new TempDirectory("xe-entra-store");
        using (var writer = new EntraTokenCacheStore(new EphemeralDataProtectionProvider(),
                   new FakeNodeDataDirectory(temp.Path),
                   NullLogger<EntraTokenCacheStore>.Instance))
        {
            await writer.SaveRecordAsync(CreateRecord("user@contoso.com"));
        }

        using var reader = new EntraTokenCacheStore(new EphemeralDataProtectionProvider(),
            new FakeNodeDataDirectory(temp.Path),
            NullLogger<EntraTokenCacheStore>.Instance);

        AssertEx.Null(await reader.LoadRecordAsync());
        AssertOnlyAMovedAsideCopyRemains(temp.Path);
    }

    private static void AssertOnlyAMovedAsideCopyRemains(string directory)
    {
        var files = Directory.GetFiles(directory);
        AssertEx.Equal(expected: 1, files.Length, "The unreadable record must be kept, not deleted, and nothing else written.");
        AssertEx.Contains(Path.GetFileName(files[0]), ".unreadable-");
    }

    private static AuthenticationRecord CreateRecord(string username)
    {
        var json = $$"""
                     {"username":"{{username}}","authority":"https://login.microsoftonline.com/tenant-id","homeAccountId":"home-account-id","tenantId":"tenant-id","clientId":"client-id","version":"1.0"}
                     """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return AuthenticationRecord.Deserialize(stream);
    }
}
