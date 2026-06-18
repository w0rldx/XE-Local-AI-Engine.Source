namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Plan §12 row <c>HfTokenStore_RoundTrips_Encrypted_AndNeverLeaksInErrors</c>: the optional HF token round-trips
///     through the <see cref="IDataProtector" /> store, clears back to anonymous, is never written in plaintext, and a
///     decryption failure self-heals to anonymous without surfacing the token.
/// </summary>
public sealed class HfTokenStoreTests : IDisposable
{
    private const string Token = "hf_secret_access_token_value";

    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, true);
        }
    }

    [Test]
    public async Task SetToken_ThenGetToken_RoundTripsValue()
    {
        using var store = CreateStore();

        await store.SetTokenAsync(Token, CancellationToken.None);

        AssertEx.Equal(Token, await store.GetTokenAsync(CancellationToken.None));
        AssertEx.True(await store.HasTokenAsync(CancellationToken.None));
    }

    [Test]
    public async Task ClearToken_ReturnsToAnonymous()
    {
        using var store = CreateStore();
        await store.SetTokenAsync(Token, CancellationToken.None);

        await store.ClearTokenAsync(CancellationToken.None);

        AssertEx.Null(await store.GetTokenAsync(CancellationToken.None));
        AssertEx.False(await store.HasTokenAsync(CancellationToken.None));
        AssertEx.False(File.Exists(GetTokenPath()));
    }

    [Test]
    public async Task GetToken_WhenNothingStored_ReturnsNull()
    {
        using var store = CreateStore();

        AssertEx.Null(await store.GetTokenAsync(CancellationToken.None));
    }

    [Test]
    public async Task SetToken_WhenUsingDataProtection_DoesNotWriteTokenInPlaintext()
    {
        using var store = CreateStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_contentRootPath, "keys"))));

        await store.SetTokenAsync(Token, CancellationToken.None);

        var payload = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(GetTokenPath()));
        AssertEx.False(payload.Contains(Token, StringComparison.Ordinal));
    }

    [Test]
    public async Task GetToken_WhenFileCorrupted_SelfHealsToAnonymous_AndNeverLeaksToken()
    {
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllBytesAsync(GetTokenPath(), [1, 2, 3]);
        var protector = Substitute.For<IDataProtector>();
        protector.CreateProtector(Arg.Any<string>()).Returns(protector);
        // Surface the token in the thrown message to prove the store never re-emits a decryption error verbatim.
        protector.Unprotect(Arg.Any<byte[]>())
            .Returns(_ => throw new CryptographicException($"boom containing {Token}"));
        using var store = CreateStore(protector);

        var loaded = await store.GetTokenAsync(CancellationToken.None);

        AssertEx.Null(loaded);
        AssertEx.False(File.Exists(GetTokenPath()));
    }

    [Test]
    public async Task SetToken_RejectsBlankToken()
    {
        using var store = CreateStore();

        await AssertEx.ThrowsAsync<ArgumentException>(() => store.SetTokenAsync("   ", CancellationToken.None));
    }

    private HfTokenStore CreateStore(IDataProtectionProvider? dataProtectionProvider = null)
    {
        Directory.CreateDirectory(_contentRootPath);

        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.ContentRootPath.Returns(_contentRootPath);

        return new HfTokenStore(dataProtectionProvider ?? new MockDataProtector(),
            hostEnvironment,
            NullLogger<HfTokenStore>.Instance);
    }

    private string GetTokenPath() => Path.Combine(_contentRootPath, "hf-token.enc");
}
