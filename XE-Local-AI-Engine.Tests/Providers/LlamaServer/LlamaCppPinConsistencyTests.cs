namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards the llama.cpp version pin against a PARTIALLY applied bump. The pin is spread over a tag, a source commit
///     SHA, and ten per-platform asset rows; editing the tag but leaving one asset row behind produces a 404 at download
///     time on exactly one platform — the one nobody tested on. Pure constant inspection: no network, no file system.
/// </summary>
public sealed class LlamaCppPinConsistencyTests
{
    // Every (os, arch, variant) row the pin table is expected to carry. Enumerated explicitly rather than reflected, so
    // that DELETING a pin row is also a failure here rather than a silently shorter loop.
    private static readonly (OSPlatform Os, Architecture Arch, GpuVariant Variant)[] PinnedCombinations =
    [
        (OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda),
        (OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan),
        (OSPlatform.Windows, Architecture.X64, GpuVariant.Cpu),
        (OSPlatform.Windows, Architecture.Arm64, GpuVariant.Cpu),
        (OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan),
        (OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu),
        (OSPlatform.Linux, Architecture.Arm64, GpuVariant.Vulkan),
        (OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cpu),
        (OSPlatform.OSX, Architecture.Arm64, GpuVariant.Cpu),
        (OSPlatform.OSX, Architecture.X64, GpuVariant.Cpu)
    ];

    [Test]
    public void DefaultRecommendedTag_IsAliasedToThePinnedTag_NotReLiteralled()
    {
        // The UI's "Recommended" value comes from StoredNodeSettings, not from the pin table. It was once an
        // independent literal, and it drifted (F-007). It is now a const alias, so this cannot fail by drift — it fails
        // if someone re-introduces a literal, which is the actual regression to prevent.
        AssertEx.Equal(
            LlamaCppReleasePins.PinnedTag,
            StoredNodeSettings.DefaultRecommendedLlamaCppTag,
            "StoredNodeSettings.DefaultRecommendedLlamaCppTag must stay aliased to LlamaCppReleasePins.PinnedTag. "
            + "Do not re-hardcode it: a hand-maintained second literal is what let the UI advertise a stale tag.");
    }

    [Test]
    public void EveryPinnedAssetName_EmbedsThePinnedTag()
    {
        // Upstream asset names are tag-prefixed (llama-<tag>-bin-...). The cudart companion deliberately is NOT, and is
        // checked separately below.
        foreach (var (os, arch, variant) in PinnedCombinations)
        {
            var pin = AssertEx.NotNull(
                LlamaCppReleasePins.TryResolveExact(os, arch, variant),
                $"Expected a pin row for ({os}, {arch}, {variant}).");

            AssertEx.Contains(
                pin.AssetName,
                LlamaCppReleasePins.PinnedTag,
                StringComparison.Ordinal,
                $"Asset name '{pin.AssetName}' for ({os}, {arch}, {variant}) does not carry the pinned tag "
                + $"'{LlamaCppReleasePins.PinnedTag}' — the pin bump was only partly applied.");
        }
    }

    [Test]
    public void WindowsCudaPin_CarriesAnUnprefixedCudartCompanion()
    {
        // A CUDA build without its cudart companion silently degrades to CPU-only, so the companion is part of the pin
        // rather than an optional extra. Its name is NOT tag-prefixed upstream — asserting that keeps a well-meaning
        // "make it consistent" edit from inventing an asset that does not exist.
        var pin = AssertEx.NotNull(
            LlamaCppReleasePins.TryResolveExact(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda),
            "Windows x64 CUDA must have a pin row.");

        var cudartName = AssertEx.NotNull(pin.CudartAssetName, "Windows x64 CUDA pin must carry a cudart companion.");
        AssertEx.NotNull(pin.CudartSha256, "The cudart companion must carry its own digest.");
        AssertEx.True(
            !cudartName.Contains(LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal),
            $"The cudart asset name is not tag-prefixed upstream, but '{cudartName}' embeds the pinned tag.");
    }

    [Test]
    public void EveryPinnedDigest_IsA64CharHexSha256()
    {
        // A truncated or placeholder digest fails closed at verification time, but only on the platform that downloads
        // it. Checking the shape here turns a per-platform runtime failure into a build-time one.
        foreach (var (os, arch, variant) in PinnedCombinations)
        {
            var pin = AssertEx.NotNull(
                LlamaCppReleasePins.TryResolveExact(os, arch, variant),
                $"Expected a pin row for ({os}, {arch}, {variant}).");

            AssertEx.True(
                IsSha256Hex(pin.Sha256),
                $"Sha256 for ({os}, {arch}, {variant}) is not 64 hex characters: '{pin.Sha256}'.");

            if (pin.CudartSha256 is not null)
            {
                AssertEx.True(
                    IsSha256Hex(pin.CudartSha256),
                    $"CudartSha256 for ({os}, {arch}, {variant}) is not 64 hex characters: '{pin.CudartSha256}'.");
            }
        }
    }

    [Test]
    public void PinnedSourceCommitSha_IsAFullyQualified40CharSha()
    {
        // The in-app source build hard-fails unless the freshly-cloned HEAD equals this exactly, so an abbreviated SHA
        // would brick the only Linux GPU path rather than degrade gracefully. [secHIGH-1]
        AssertEx.Equal(40, LlamaCppReleasePins.PinnedCudaSourceCommitSha.Length,
            "PinnedCudaSourceCommitSha must be a full 40-character commit SHA, never abbreviated.");
        AssertEx.True(
            LlamaCppReleasePins.PinnedCudaSourceCommitSha.All(Uri.IsHexDigit),
            "PinnedCudaSourceCommitSha must be hex.");
        AssertEx.Equal(LlamaCppReleasePins.PinnedCudaSourceCommitSha, LlamaCppReleasePins.PinnedSourceCommitSha,
            "PinnedSourceCommitSha is the backend-neutral alias and must track the CUDA source pin.");
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
