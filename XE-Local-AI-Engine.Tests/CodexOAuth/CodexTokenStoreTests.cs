namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Verifies the ported <see cref="CodexTokenStore" /> persists the OAuth session encrypted at rest, applies
///     user-only file permissions, clears on logout, and fails closed when the stored payload is tampered.
/// </summary>
public sealed class CodexTokenStoreTests : IDisposable
{
    private const string TokensFileName = "codex-oauth-tokens.enc";

    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    [Test]
    public async Task SaveAsync_WhenSessionIsValid_PersistsAndLoadsSession()
    {
        using var store = CreateStore();
        var tokens = CreateTokens();

        await store.SaveAsync(tokens);
        var loaded = await store.LoadAsync();

        var loadedTokens = AssertEx.NotNull(loaded);
        AssertEx.Equal("access-token-value", loadedTokens.AccessToken);
        AssertEx.Equal("refresh-token-value", loadedTokens.RefreshToken);
        AssertEx.Equal("acct_123", loadedTokens.AccountId);
        AssertEx.Equal(tokens.ExpiresUtc, loadedTokens.ExpiresUtc);
    }

    [Test]
    public async Task SaveAsync_WhenUsingDataProtection_DoesNotWriteTokenMaterialInPlaintext()
    {
        using var store = CreateStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_contentRootPath, "keys"))));

        await store.SaveAsync(CreateTokens());

        var payload = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(GetTokensPath()));
        AssertEx.False(payload.Contains("access-token-value", StringComparison.Ordinal));
        AssertEx.False(payload.Contains("refresh-token-value", StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveAsync_WhenRunningOnUnix_SetsTokenFileModeToUserReadWrite()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var store = CreateStore();

        await store.SaveAsync(CreateTokens());

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(GetTokensPath()));
    }

    [Test]
    public async Task ClearAsync_WhenSessionExists_RemovesTokenFile()
    {
        using var store = CreateStore();
        await store.SaveAsync(CreateTokens());

        await store.ClearAsync();

        AssertEx.Null(await store.LoadAsync());
        AssertEx.False(File.Exists(GetTokensPath()));
    }

    [Test]
    public async Task LoadAsync_WhenTokenFileIsTampered_ReturnsNullFailingClosed()
    {
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllBytesAsync(GetTokensPath(), [9, 8, 7, 6]);
        var protector = Substitute.For<IDataProtector>();
        protector.CreateProtector(Arg.Any<string>()).Returns(protector);
        protector.Unprotect(Arg.Any<byte[]>()).Returns(_ => throw new CryptographicException("tampered"));
        using var store = CreateStore(protector);

        var loaded = await store.LoadAsync();

        AssertEx.Null(loaded);
    }

    private CodexTokenStore CreateStore(IDataProtectionProvider? dataProtectionProvider = null)
    {
        Directory.CreateDirectory(_contentRootPath);

        return new CodexTokenStore(dataProtectionProvider ?? new MockDataProtector(),
            new FakeNodeDataDirectory(_contentRootPath),
            NullLogger<CodexTokenStore>.Instance);
    }

    private string GetTokensPath()
    {
        return Path.Combine(_contentRootPath, TokensFileName);
    }

    private static CodexTokens CreateTokens()
    {
        return new CodexTokens("access-token-value", "refresh-token-value", DateTimeOffset.UtcNow.AddHours(1), "acct_123");
    }
}
