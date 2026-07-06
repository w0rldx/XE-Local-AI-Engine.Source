namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class StableDiffusionReleasePinsTests
{
    [Test]
    public void Resolve_WindowsCuda_ReturnsCudaAssetWithCudartCompanion()
    {
        var pin = AssertEx.NotNull(StableDiffusionReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Cuda));

        AssertEx.Equal("sd-master-1a13107-bin-win-cuda12-x64.zip", pin.AssetName);
        AssertEx.Equal("sd-server.exe", pin.ServerRelativePath);
        AssertEx.Equal(expected: 64, pin.Sha256.Length);
        AssertEx.NotNullOrEmpty(pin.CudartAssetName);
        AssertEx.NotNullOrEmpty(pin.CudartSha256);
    }

    [Test]
    public void Resolve_LinuxCuda_FallsBackToCpuFloor_NoLinuxCudaPrebuilt()
    {
        // stable-diffusion.cpp ships no Linux CUDA asset — a Linux CUDA request must degrade to the CPU floor, never null.
        var pin = AssertEx.NotNull(StableDiffusionReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Cuda));

        AssertEx.Equal("sd-master-1a13107-bin-Linux-Ubuntu-24.04-x86_64.zip", pin.AssetName);
        AssertEx.Equal("sd-server", pin.ServerRelativePath);
        AssertEx.Null(pin.CudartAssetName);
    }

    [Test]
    [Arguments("win-vulkan-x64")]
    public void Resolve_LinuxVulkan_ReturnsVulkanAsset(string _)
    {
        var pin = AssertEx.NotNull(StableDiffusionReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Vulkan));

        AssertEx.Contains(pin.AssetName, "vulkan", StringComparison.Ordinal);
        AssertEx.Equal(expected: 64, pin.Sha256.Length);
    }

    [Test]
    public void Resolve_UnsupportedArch_ReturnsNull()
    {
        // No Windows arm64 pin exists in the frozen set and there is no arm64 CPU floor for Windows.
        AssertEx.Null(StableDiffusionReleasePins.Resolve(OSPlatform.Windows, Architecture.Arm64, SdGpuBackend.Cpu));
    }

    [Test]
    public void DownloadUri_BuildsLeejetReleaseAssetUrl()
    {
        var uri = StableDiffusionReleasePins.DownloadUri(StableDiffusionReleasePins.PinnedTag, "sd-master-1a13107-bin-win-cpu-x64.zip");

        AssertEx.Equal("https://github.com/leejet/stable-diffusion.cpp/releases/download/master-742-1a13107/sd-master-1a13107-bin-win-cpu-x64.zip",
            uri.ToString());
    }
}
