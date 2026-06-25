namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pure cudart-companion derivation (<see cref="LlamaCppReleasePins.DeriveCudartAssetName" />) and the
///     Windows-CUDA pin carrying its companion runtime archive metadata. No network and no file system.
/// </summary>
public sealed class CudartCompanionAssetTests
{
    [Test]
    public void DeriveCudartAssetName_FromWindowsCudaMainAsset_DropsTagAndReprefixes()
    {
        // The cudart asset name is NOT tag-prefixed; it carries only the CUDA version + arch from the main asset.
        var derived = LlamaCppReleasePins.DeriveCudartAssetName("llama-b9692-bin-win-cuda-12.4-x64.zip");

        AssertEx.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", derived);
    }

    [Test]
    public void DeriveCudartAssetName_DifferentTagAndCudaVersion_PreservesCudaVersion()
    {
        // A live/dynamic tag with a drifted CUDA version still derives the matching cudart name (version preserved).
        var derived = LlamaCppReleasePins.DeriveCudartAssetName("llama-b9999-bin-win-cuda-13.0-x64.zip");

        AssertEx.Equal("cudart-llama-bin-win-cuda-13.0-x64.zip", derived);
    }

    [Test]
    public void DeriveCudartAssetName_NonWindowsCudaAssets_ReturnNull()
    {
        // Only a Windows-CUDA main asset has a companion; every other variant/OS must derive nothing.
        AssertEx.Null(LlamaCppReleasePins.DeriveCudartAssetName("llama-b9692-bin-win-vulkan-x64.zip"));
        AssertEx.Null(LlamaCppReleasePins.DeriveCudartAssetName("llama-b9692-bin-win-cpu-x64.zip"));
        AssertEx.Null(LlamaCppReleasePins.DeriveCudartAssetName("llama-b9692-bin-ubuntu-x64.tar.gz"));
        AssertEx.Null(LlamaCppReleasePins.DeriveCudartAssetName(null));
        AssertEx.Null(LlamaCppReleasePins.DeriveCudartAssetName(""));
    }

    [Test]
    public void WindowsCudaPin_CarriesCompanionRuntimeArchiveMetadata()
    {
        var pin = AssertEx.NotNull(LlamaCppReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda));

        AssertEx.NotNullOrEmpty(pin.CudartAssetName);
        AssertEx.NotNullOrEmpty(pin.CudartSha256);
        // The pinned companion name must match what the derivation produces from the pinned main asset.
        var derived = AssertEx.NotNull(LlamaCppReleasePins.DeriveCudartAssetName(pin.AssetName));
        AssertEx.Equal(derived, pin.CudartAssetName);
    }

    [Test]
    public void NonWindowsCudaPins_HaveNoCompanionRuntimeArchive()
    {
        // The cudart pairing is Windows-CUDA-scoped: no other pin row carries companion metadata.
        var vulkan = AssertEx.NotNull(LlamaCppReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan));
        var cpu = AssertEx.NotNull(LlamaCppReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, GpuVariant.Cpu));
        var linuxVulkan = AssertEx.NotNull(LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan));

        AssertEx.Null(vulkan.CudartAssetName);
        AssertEx.Null(cpu.CudartAssetName);
        AssertEx.Null(linuxVulkan.CudartAssetName);
    }
}
