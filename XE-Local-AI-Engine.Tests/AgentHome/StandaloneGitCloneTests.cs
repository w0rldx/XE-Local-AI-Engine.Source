namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class StandaloneGitCloneTests
{
    [Test]
    public async Task TryClearReadOnly_ClearsTheReadOnlyFlag()
    {
        using var directory = new TempDirectory("standalone-clone");
        var path = directory.FilePath("pack.idx");
        await File.WriteAllTextAsync(path, "pack");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        StandaloneGitClone.TryClearReadOnly(new FileInfo(path));

        AssertEx.False(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly), "A read-only file must be writable after the clear.");
    }

    [Test]
    public async Task TryClearReadOnly_WhenTheEntryVanishedAfterTheEnumeration_DoesNotThrow()
    {
        using var directory = new TempDirectory("standalone-clone");
        var path = directory.FilePath("index.lock");
        await File.WriteAllTextAsync(path, "lock");
        var entry = new FileInfo(path);
        File.Delete(path);
        entry.Refresh();

        // A vanished entry reads its attributes as -1; writing back "-1 without ReadOnly" threw ArgumentException and,
        // through StandaloneGitClone.TryDelete, replaced the failure the cleanup ran for.
        StandaloneGitClone.TryClearReadOnly(entry);

        AssertEx.False(File.Exists(path), "The entry stays gone.");
    }
}
