namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Brief test 1, asset-resolution half: the pinned table must pick the cuBLAS zip on an NVIDIA Windows box, the CPU
///     zip on any other Windows box, and the Ubuntu tarball on Linux — including for a Linux CUDA request, because
///     whisper.cpp ships no Linux CUDA prebuilt at all. Pure table lookups, so every case is exercised on any host.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WhisperCppReleasePinsTests
{
    [Test]
    public void Resolve_WindowsCuda_ReturnsCublasZip()
    {
        var pin = AssertEx.NotNull(WhisperCppReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, WhisperBackend.Cuda));

        AssertEx.Equal("whisper-cublas-12.4.0-bin-x64.zip", pin.AssetName);
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

        AssertEx.Equal("https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-ubuntu-x64.tar.gz",
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
        // v1.9.4 is an ANNOTATED tag: the refs API returns 7d75b149…, a tag object that cannot be fetched or checked
        // out. Storing that value would ship a source build whose revision does not exist.
        AssertEx.Equal("927cfce34f31707e17f2bff35c349632fb9e2c3a", WhisperCppReleasePins.PinnedSourceCommitSha);
        AssertEx.NotEqual("7d75b14994ae7f59623e2471445e2355fe506ed2", WhisperCppReleasePins.PinnedSourceCommitSha);
    }
}
