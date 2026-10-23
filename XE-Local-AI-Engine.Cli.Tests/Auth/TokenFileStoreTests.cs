namespace XE_Local_AI_Engine.Cli.Tests.Auth;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using XE_Local_AI_Engine.Cli.Auth;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[Category(TestCategories.Unit)]
public sealed class TokenFileStoreTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:5123/");
    private static readonly string[] Written = ["access-a", "access-b"];

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task SaveAsync_FromTwoWritersAtOnce_BothSucceedAndLeaveOneWholeOwnerOnlyFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xe-cli-config-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "xe", "tokens.json");
        try
        {
            // One collision is a race, so the pair of writers runs many times.
            for (var round = 0; round < 20; round++)
            {
                await Task.WhenAll(Task.Run(() => TokenFileStore.SaveAsync(path, Pair(Written[0]), CancellationToken.None)),
                    Task.Run(() => TokenFileStore.SaveAsync(path, Pair(Written[1]), CancellationToken.None)));

                var stored = await TokenFileStore.LoadAsync(path, Origin, CancellationToken.None);
                AssertEx.Contains(Written, stored?.AccessToken ?? "nothing readable");
                AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                AssertEx.Equal("tokens.json", string.Join(',', Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName)));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task SaveAsync_OnWindows_LeavesAProtectedAclThatGrantsOnlyTheCurrentUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xe-cli-config-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "xe", "tokens.json");
        try
        {
            await TokenFileStore.SaveAsync(path, Pair(Written[0]), CancellationToken.None);

            var security = new FileInfo(path).GetAccessControl();
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                                .Cast<FileSystemAccessRule>()
                                .ToList();
            using var currentIdentity = WindowsIdentity.GetCurrent();
            AssertEx.True(security.AreAccessRulesProtected);
            AssertEx.Equal(currentIdentity.User!.Value, string.Join(',', rules.Select(rule => rule.IdentityReference.Value)));
            AssertEx.Equal(AccessControlType.Allow, rules[0].AccessControlType);
            AssertEx.Equal(Written[0], (await TokenFileStore.LoadAsync(path, Origin, CancellationToken.None))?.AccessToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TokenPair Pair(string access) =>
        new()
        {
            AccessToken = access,
            RefreshToken = "refresh",
            Origin = Origin.GetLeftPart(UriPartial.Authority)
        };
}
