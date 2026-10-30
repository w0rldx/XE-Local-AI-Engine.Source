namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Asset resolution: the pinned table picks the CUDA zip on an NVIDIA Windows box, the CPU zip on other Windows
///     boxes, and the Ubuntu tarball on Linux, even for a CUDA request (no Linux CUDA prebuilt exists). Pure lookups.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WhisperCppReleasePinsTests
{
    [Test]
    public void Resolve_WindowsCuda_ReturnsCudaZip()
    {
        var pin = AssertEx.NotNull(WhisperCppReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, WhisperBackend.Cuda));

        AssertEx.Equal("whisper-bin-win-cuda-12.4.0-x64.zip", pin.AssetName);
        AssertEx.Equal("Release/whisper-server.exe", pin.ServerRelativePath);
        AssertEx.Equal(WhisperArchiveKind.Zip, pin.ArchiveKind);
    }

    [Test]
    public void Resolve_WindowsNonNvidia_ReturnsCpuZip()
    {
        var pin = AssertEx.NotNull(WhisperCppReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, WhisperBackend.Cpu));

        AssertEx.Equal("whisper-bin-x64.zip", pin.AssetName);
        AssertEx.Equal(WhisperArchiveKind.Zip, pin.ArchiveKind);
    }

    [Test]
    public void Resolve_LinuxCuda_FallsBackToUbuntuCpuTarball()
    {
        // whisper.cpp ships no Linux CUDA asset. A Linux CUDA request must degrade to the CPU floor rather than
        // returning null — the CUDA lane on Linux is the managed source build or the bring-your-own override.
        var pin = AssertEx.NotNull(WhisperCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cuda));

        AssertEx.Equal("whisper-bin-ubuntu-x64.tar.gz", pin.AssetName);
        AssertEx.Equal(WhisperArchiveKind.TarGz, pin.ArchiveKind);
    }

    [Test]
    public void Resolve_LinuxCpu_ReturnsUbuntuTarball()
    {
        var pin = AssertEx.NotNull(WhisperCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cpu));

        AssertEx.Equal("whisper-bin-ubuntu-x64.tar.gz", pin.AssetName);
        AssertEx.Equal("whisper-bin-ubuntu-x64/whisper-server", pin.ServerRelativePath);
    }

    [Test]
    public void ResolveExact_LinuxCuda_ReturnsNull()
    {
        // The exact lookup must never substitute the CPU floor, so resolved bytes can never contradict an explicitly
        // requested GPU backend.
        AssertEx.Null(WhisperCppReleasePins.ResolveExact(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cuda));
    }

    [Test]
    public void Resolve_UnsupportedArch_ReturnsNull()
    {
        // No arm64 pin exists in the frozen set, and there is no arm64 CPU floor to fall back to either.
        AssertEx.Null(WhisperCppReleasePins.Resolve(OSPlatform.Windows, Architecture.Arm64, WhisperBackend.Cpu));
        AssertEx.Null(WhisperCppReleasePins.Resolve(OSPlatform.OSX, Architecture.Arm64, WhisperBackend.Cpu));
    }

    [Test]
    public void DownloadUri_BuildsGgmlOrgReleaseAssetUrl()
    {
        var uri = WhisperCppReleasePins.DownloadUri(WhisperCppReleasePins.PinnedTag, "whisper-bin-ubuntu-x64.tar.gz");

        AssertEx.Equal("https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-ubuntu-x64.tar.gz",
            uri.ToString());
    }

    [Test]
    public void EveryPin_CarriesA64HexDigestAndArchiveKind()
    {
        var pins = WhisperCppReleasePins.All.ToArray();

        AssertEx.True(pins.Length >= 3,
            $"Expected at least the three pinned assets (two Windows, one Linux); found {pins.Length}. The table is broken.");

        foreach (var pin in pins)
        {
            AssertEx.Equal(expected: 64, pin.Sha256.Length, $"'{pin.AssetName}' must carry a full SHA256 digest.");
            AssertEx.True(pin.Sha256.All(Uri.IsHexDigit), $"'{pin.AssetName}' digest must be hexadecimal.");
            AssertEx.False(pin.Sha256.Any(char.IsAsciiLetterUpper), $"'{pin.AssetName}' digest must be lowercase hex.");
            AssertEx.True(Enum.IsDefined(pin.ArchiveKind), $"'{pin.AssetName}' must declare a known archive kind.");
            AssertEx.NotEmpty(pin.ServerRelativePath);
        }
    }

    [Test]
    public void PinnedSourceCommitSha_IsAPeeledCommitNotTheAnnotatedTagObject()
    {
        // v1.9.5 is an ANNOTATED tag: the refs API returns d2a764c6…, a tag object that cannot be fetched or checked
        // out. Storing that value would ship a source build whose revision does not exist.
        AssertEx.Equal("d1be6fde11ac6e0407606b4e42fe72d34add8037", WhisperCppReleasePins.PinnedSourceCommitSha);
        AssertEx.NotEqual("d2a764c648779feb36d44363f212a29b00aad1af", WhisperCppReleasePins.PinnedSourceCommitSha);
    }
}
