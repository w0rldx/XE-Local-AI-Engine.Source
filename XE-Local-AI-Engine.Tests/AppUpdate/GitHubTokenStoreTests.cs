namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Verifies the GitHub session store persists the user token + login encrypted at rest, applies owner-only file
///     permissions, clears on sign-out, and fails closed when the stored payload is tampered. Mirrors
///     <c>CodexTokenStoreTests</c> (the encrypted <c>.enc</c> token-store precedent).
/// </summary>
public sealed class GitHubTokenStoreTests : IDisposable
{
    private const string SessionFileName = "github-token.enc";

    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, recursive: true);
        }
    }

    [Test]
    public async Task SetSession_WhenValid_PersistsAndLoadsSession()
    {
        using var store = CreateStore();

        await store.SetSessionAsync(new GitHubSession("ghu_secret_token", "octocat"), CancellationToken.None);
        var loaded = await store.GetSessionAsync(CancellationToken.None);

        var session = AssertEx.NotNull(loaded);
        AssertEx.Equal("ghu_secret_token", session.AccessToken);
        AssertEx.Equal("octocat", session.Login);
    }

    [Test]
    public async Task SetSession_WhenUsingDataProtection_DoesNotWriteTokenInPlaintext()
    {
        using var store = CreateStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_contentRootPath, "keys"))));

        await store.SetSessionAsync(new GitHubSession("ghu_secret_token", "octocat"), CancellationToken.None);

        var payload = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(GetSessionPath()));
        AssertEx.False(payload.Contains("ghu_secret_token", StringComparison.Ordinal),
            "the access token must never be written in plaintext");
    }

    [Test]
    public async Task SetSession_WhenRunningOnUnix_SetsFileModeToOwnerOnly()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var store = CreateStore();

        await store.SetSessionAsync(new GitHubSession("ghu_secret_token", "octocat"), CancellationToken.None);

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(GetSessionPath()));
    }

    [Test]
    public async Task ClearSession_WhenSessionExists_RemovesFile()
    {
        using var store = CreateStore();
        await store.SetSessionAsync(new GitHubSession("ghu_secret_token", "octocat"), CancellationToken.None);

        await store.ClearSessionAsync(CancellationToken.None);

        AssertEx.Null(await store.GetSessionAsync(CancellationToken.None));
        AssertEx.False(File.Exists(GetSessionPath()));
    }

    [Test]
    public async Task HasSession_ReflectsPresence()
    {
        using var store = CreateStore();

        AssertEx.False(await store.HasSessionAsync(CancellationToken.None));
        await store.SetSessionAsync(new GitHubSession("ghu_secret_token", "octocat"), CancellationToken.None);
        AssertEx.True(await store.HasSessionAsync(CancellationToken.None));
    }

    [Test]
    public async Task GetSession_WhenFileTampered_ReturnsNullFailingClosed()
    {
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllBytesAsync(GetSessionPath(), [9, 8, 7, 6]);
        var protector = Substitute.For<IDataProtector>();
        protector.CreateProtector(Arg.Any<string>()).Returns(protector);
        protector.Unprotect(Arg.Any<byte[]>()).Returns(_ => throw new CryptographicException("tampered"));
        using var store = CreateStore(protector);

        var loaded = await store.GetSessionAsync(CancellationToken.None);

        AssertEx.Null(loaded);
    }

    private GitHubTokenStore CreateStore(IDataProtectionProvider? dataProtectionProvider = null)
    {
        Directory.CreateDirectory(_contentRootPath);

        return new GitHubTokenStore(dataProtectionProvider ?? new MockDataProtector(),
            new FakeNodeDataDirectory(_contentRootPath),
            NullLogger<GitHubTokenStore>.Instance);
    }

    private string GetSessionPath()
    {
        return Path.Combine(_contentRootPath, SessionFileName);
    }
}
