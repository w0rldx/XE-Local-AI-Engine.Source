namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards the wire projection of the two runtime-honesty signals the hardware card renders beside the backend
///     indicator: measured GPU layer placement, and an inference backend nobody could determine.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ModelFitMapperLayerPlacementTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Test]
    public void ToResponse_PartialLayerPlacement_ReachesTheWireWithItsOwningModel()
    {
        var response = Profile().ToResponse(new RuntimeDeviceAuditState
        {
            InferenceBackend = "cuda",
            GpuExpected = true,
            CpuFallback = false,
            LayerPlacement = new LlamaLayerPlacement
            {
                ModelName = "qwen3-14b",
                Role = ModelRole.Chat,
                OffloadedLayers = 38,
                TotalLayers = 49
            }
        });

        AssertEx.Equal(expected: 38, response.GpuOffloadedLayers!.Value);
        AssertEx.Equal(expected: 49, response.GpuTotalLayers!.Value);
        // The figures must name what they describe, or a multi-model node attributes them to the wrong model.
        AssertEx.Equal("qwen3-14b", response.GpuOffloadModelName);
        AssertEx.Equal("chat", response.GpuOffloadRole);
        // Partial offload is not a fallback: the GPU IS in use.
        AssertEx.False(response.CpuFallback);
        AssertEx.Equal("cuda", response.InferenceBackend);
    }

    [Test]
    public void ToResponse_ExpertOffloadWithEveryLayerCounted_SaysTheExpertsAreInSystemRam()
    {
        var response = Profile().ToResponse(new RuntimeDeviceAuditState
        {
            InferenceBackend = "cuda",
            GpuExpected = true,
            CpuFallback = false,
            LayerPlacement = new LlamaLayerPlacement
            {
                ModelName = "qwen3.6-35b-a3b",
                Role = ModelRole.Chat,
                OffloadedLayers = 41,
                TotalLayers = 41,
                ExpertsOffloaded = true
            }
        });

        AssertEx.Equal(expected: 41, response.GpuOffloadedLayers!.Value);
        AssertEx.True(response.GpuExpertsOffloaded);
    }

    [Test]
    public void ToResponse_RunningProcess_CarriesItsWindowAndPlacement()
    {
        var response = new LlamaServerProcessHealth
        {
            ModelName = "qwen3.6-35b-a3b",
            Role = ModelRole.Chat,
            IsResponsive = true,
            Detail = "Responsive.",
            EffectiveContextTokens = 65536,
            ExpertsOffloaded = true
        }.ToResponse();

        AssertEx.Equal(expected: 65536, response.EffectiveContextTokens!.Value);
        AssertEx.True(response.ExpertsOffloaded);
    }

    [Test]
    public void ToResponse_NoObservedLoad_LeavesEveryPlacementFieldNull()
    {
        var response = Profile().ToResponse(new RuntimeDeviceAuditState
        {
            InferenceBackend = "cuda",
            GpuExpected = true,
            CpuFallback = false
        });

        AssertEx.Null(response.GpuOffloadedLayers);
        AssertEx.Null(response.GpuTotalLayers);
        AssertEx.Null(response.GpuOffloadModelName);
        AssertEx.Null(response.GpuOffloadRole);
        AssertEx.False(response.GpuExpertsOffloaded);
        AssertEx.Null(response.BackendUndeterminedReason);
    }

    [Test]
    public void ToResponse_UndeterminedBackend_CarriesItsReasonWithoutClaimingAFallback()
    {
        var response = Profile().ToResponse(new RuntimeDeviceAuditState
        {
            InferenceBackend = "unknown",
            GpuExpected = true,
            CpuFallback = false,
            BackendUndeterminedReason = "The CUDA llama.cpp runtime is selected, but listing its GPU devices did not complete."
        });

        AssertEx.Equal("The CUDA llama.cpp runtime is selected, but listing its GPU devices did not complete.",
            response.BackendUndeterminedReason);
        AssertEx.False(response.CpuFallback);
        AssertEx.Null(response.CpuFallbackReason);
    }

    private static HardwareProfile Profile()
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = 16 * Gb,
            AvailableVramBytes = 15 * Gb,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }
}
