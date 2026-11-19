namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The installed-training-runtime record is replaced through the shared owner-only atomic writer: a replacement never weakens its
///     permissions, and one that fails keeps the previous record readable.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InstalledTrainingRuntimeStoreTests
{
    private static readonly InstalledTrainingRuntimeState Previous = new("0.1.0", "digest-a", "3.12.1", "lock-a", ContractVersion: 1, DateTimeOffset.UnixEpoch);

    private static readonly InstalledTrainingRuntimeState Next = new("0.2.0", "digest-b", "3.12.2", "lock-b", ContractVersion: 2, DateTimeOffset.UnixEpoch);

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteAsync_OnUnix_ReplacesAWorldReadableRecordWithAnOwnerOnlyOne()
    {
        using var temp = new TempDirectory("xe-training-store");
        var path = temp.FilePath("installed-training-runtime.json");
        var store = new InstalledTrainingRuntimeStore(path);
        await store.WriteAsync(Previous, CancellationToken.None);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await store.WriteAsync(Next, CancellationToken.None);

        // The rename carries the temp sibling's mode, so the final file's mode is the mode the temp was created with.
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        AssertEx.Equal(Next, await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(expected: 1, Directory.GetFiles(temp.Path).Length);
    }

    [Test]
    public async Task WriteAsync_WhenCancelled_KeepsThePreviousRecordAndNoTempSibling()
    {
        using var temp = new TempDirectory("xe-training-store");
        var store = new InstalledTrainingRuntimeStore(temp.FilePath("installed-training-runtime.json"));
        await store.WriteAsync(Previous, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await store.WriteAsync(Next, cts.Token));

        AssertEx.Equal(Previous, await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(expected: 1, Directory.GetFiles(temp.Path).Length);
    }
}
