namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     Startup reaper for stale GGUF acquisition artifacts: operation-owned <c>.part</c> staging files and orphaned
///     final <c>.xe-model.json</c> sidecars with no adjacent GGUF. No network; a temp models directory with
///     hand-aged files stands in for a crashed import/download.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GgufAcquisitionArtifactStartupReaperTests
{
    [Test]
    public async Task Reaper_DeletesStalePartFile_ButKeepsFreshOne()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var stalePart = dir.FilePath("Model-Q4_K_M.gguf.abc123.part");
        var freshPart = dir.FilePath("Other-Q4_K_M.gguf.def456.part");
        await File.WriteAllTextAsync(stalePart, "stale-bytes");
        await File.WriteAllTextAsync(freshPart, "fresh-bytes");
        File.SetLastWriteTimeUtc(stalePart, DateTime.UtcNow - GgufAcquisitionArtifactStartupReaper.StaleArtifactAge - TimeSpan.FromMinutes(1));

        await RunReaperAsync(options);

        AssertEx.False(File.Exists(stalePart), "a stale .part file must be reaped");
        AssertEx.True(File.Exists(freshPart), "a fresh .part file must survive the sweep");
    }

    [Test]
    public async Task Reaper_DeletesFreshDownloadLeftoversOfAnInstalledFile_ButKeepsAResumablePartial()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var installed = dir.FilePath("Installed-Q4_K_M.gguf");
        var installedSidecar = installed + ".xe-model.json";
        await File.WriteAllTextAsync(installed, "fake-gguf");
        await File.WriteAllTextAsync(installedSidecar, "{}");
        // Leftovers of an earlier attempt at the SAME file — fresh, so only the installed final can condemn them.
        string[] leftovers =
        [
            installed + ".part",
            installed + ".part.ranges.part",
            installed + ".0123456789abcdef0123456789abcdef.part.part",
            installed + ".0123456789abcdef0123456789abcdef.part.part.ranges.part",
            installed + ".0123456789abcdef0123456789abcdef.part"
        ];
        foreach (var leftover in leftovers)
        {
            await File.WriteAllTextAsync(leftover, "partial");
        }

        // A download in progress (or waiting to resume) has no final file yet.
        var resumable = dir.FilePath("Downloading-Q4_K_M.gguf.part");
        await File.WriteAllTextAsync(resumable, "partial");
        await File.WriteAllTextAsync(resumable + ".ranges.part", "cursors");
        // A sidecar temp belongs to a commit or repair, not to a download: the installed final does not condemn it.
        var sidecarTemp = installedSidecar + ".repair.part";
        await File.WriteAllTextAsync(sidecarTemp, "{}");

        await RunReaperAsync(options);

        foreach (var leftover in leftovers)
        {
            AssertEx.False(File.Exists(leftover), $"{Path.GetFileName(leftover)} must be reaped once its final file is installed");
        }

        AssertEx.True(File.Exists(installed), "the installed .gguf must never be reaped");
        AssertEx.True(File.Exists(installedSidecar), "the installed sidecar must survive");
        AssertEx.True(File.Exists(resumable), "a fresh partial with no final file is resumable and must survive");
        AssertEx.True(File.Exists(resumable + ".ranges.part"), "the resumable partial's cursors must survive");
        AssertEx.True(File.Exists(sidecarTemp), "a fresh sidecar temp is judged by age only");
    }

    [Test]
    public async Task Reaper_DeletesStaleOrphanSidecar_ButKeepsSidecarWithAdjacentGguf()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);

        var orphanSidecar = dir.FilePath("Orphan-Q4_K_M.gguf.xe-model.json");
        await File.WriteAllTextAsync(orphanSidecar, "{}");
        File.SetLastWriteTimeUtc(orphanSidecar, DateTime.UtcNow - GgufAcquisitionArtifactStartupReaper.StaleArtifactAge - TimeSpan.FromMinutes(1));

        var pairedGguf = dir.FilePath("Paired-Q4_K_M.gguf");
        var pairedSidecar = dir.FilePath("Paired-Q4_K_M.gguf.xe-model.json");
        await File.WriteAllTextAsync(pairedGguf, "fake-gguf");
        await File.WriteAllTextAsync(pairedSidecar, "{}");
        // Aged the same as the orphan — proves survival is decided by the adjacent GGUF, not by age.
        File.SetLastWriteTimeUtc(pairedGguf, DateTime.UtcNow - GgufAcquisitionArtifactStartupReaper.StaleArtifactAge - TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(pairedSidecar, DateTime.UtcNow - GgufAcquisitionArtifactStartupReaper.StaleArtifactAge - TimeSpan.FromMinutes(1));

        await RunReaperAsync(options);

        AssertEx.False(File.Exists(orphanSidecar), "an orphan sidecar past the age threshold must be reaped");
        AssertEx.True(File.Exists(pairedSidecar), "a sidecar with an adjacent GGUF must never be reaped");
        AssertEx.True(File.Exists(pairedGguf), "a .gguf file must never be reaped");
    }

    [Test]
    public async Task Reaper_KeepsFreshOrphanSidecar()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var freshOrphanSidecar = dir.FilePath("Fresh-Orphan-Q4_K_M.gguf.xe-model.json");
        await File.WriteAllTextAsync(freshOrphanSidecar, "{}");

        await RunReaperAsync(options);

        AssertEx.True(File.Exists(freshOrphanSidecar), "an orphan sidecar younger than the age threshold must survive the sweep");
    }

    [Test]
    public async Task Reaper_NeverDeletesGgufFiles_EvenWhenVeryOld()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var oldGguf = dir.FilePath("Ancient-Q4_K_M.gguf");
        await File.WriteAllTextAsync(oldGguf, "fake-gguf");
        File.SetLastWriteTimeUtc(oldGguf, DateTime.UtcNow - TimeSpan.FromDays(365));

        await RunReaperAsync(options);

        AssertEx.True(File.Exists(oldGguf), "a .gguf file must never be reaped regardless of age");
    }

    [Test]
    public async Task Reaper_MissingModelsDirectory_NoThrow()
    {
        var options = Infra.Options(Path.Combine(Path.GetTempPath(), "xe-hf-reaper-missing-" + Guid.NewGuid().ToString("N")));

        await RunReaperAsync(options);
    }

    private static Task RunReaperAsync(HuggingFaceOptions options)
    {
        var reaper = new GgufAcquisitionArtifactStartupReaper(options, TimeProvider.System, NullLogger<GgufAcquisitionArtifactStartupReaper>.Instance);
        return reaper.StartAsync(CancellationToken.None);
    }
}
