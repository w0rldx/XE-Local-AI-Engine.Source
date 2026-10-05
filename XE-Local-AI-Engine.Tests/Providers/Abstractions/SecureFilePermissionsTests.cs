namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using System.Runtime.Versioning;
using System.Text;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The atomic write every credential store replaces its protected blob through: a write that fails part-way must
///     leave the previous credential readable, because a torn blob no longer decrypts and the store would drop it.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SecureFilePermissionsTests
{
    [Test]
    public async Task WriteAllBytesAtomicAsync_ReplacesTheContentAndLeavesNoTempSibling()
    {
        using var temp = new TempDirectory("xe-secure-write");
        var path = temp.FilePath("secret.enc");
        await File.WriteAllTextAsync(path, "previous");

        await SecureFilePermissions.WriteAllBytesAtomicAsync(path, Encoding.UTF8.GetBytes("next"), CancellationToken.None);

        AssertEx.Equal("next", await File.ReadAllTextAsync(path));
        AssertEx.Equal(expected: 1, Directory.GetFiles(temp.Path).Length);
    }

    [Test]
    public async Task WriteAllBytesAtomicAsync_WhenTheWriteIsCancelled_LeavesThePreviousFileIntact()
    {
        using var temp = new TempDirectory("xe-secure-write");
        var path = temp.FilePath("secret.enc");
        await File.WriteAllTextAsync(path, "previous");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () =>
            await SecureFilePermissions.WriteAllBytesAtomicAsync(path, Encoding.UTF8.GetBytes("torn"), cts.Token));

        AssertEx.Equal("previous", await File.ReadAllTextAsync(path));
        AssertEx.Equal(expected: 1, Directory.GetFiles(temp.Path).Length);
    }

    [Test]
    public async Task WriteAllBytesAtomicAsync_WhenTheReplaceFails_LeavesTheTargetAndNoTempSibling()
    {
        using var temp = new TempDirectory("xe-secure-write");
        // A directory at the target path makes the final rename fail after the bytes were written to the temp sibling.
        var path = temp.FilePath("secret.enc");
        Directory.CreateDirectory(path);

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () =>
            await SecureFilePermissions.WriteAllBytesAtomicAsync(path, Encoding.UTF8.GetBytes("next"), CancellationToken.None));

        // rename(2) onto a directory is EISDIR (IOException); MoveFileEx onto one is ERROR_ACCESS_DENIED (UnauthorizedAccessException).
        AssertEx.Equal(OperatingSystem.IsWindows() ? typeof(UnauthorizedAccessException) : typeof(IOException), thrown.GetType());
        AssertEx.True(Directory.Exists(path));
        AssertEx.Equal(expected: 0, Directory.GetFiles(temp.Path).Length);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteAllBytesAtomicAsync_OnUnix_ReplacesAWorldReadableFileWithAnOwnerOnlyOne()
    {
        using var temp = new TempDirectory("xe-secure-write");
        var path = temp.FilePath("secret.enc");
        await File.WriteAllTextAsync(path, "previous");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await SecureFilePermissions.WriteAllBytesAtomicAsync(path, Encoding.UTF8.GetBytes("next"), CancellationToken.None);

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
