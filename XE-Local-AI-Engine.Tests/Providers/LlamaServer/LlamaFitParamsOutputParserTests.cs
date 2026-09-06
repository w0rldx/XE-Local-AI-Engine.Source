namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for the stdout grammar emitted by llama.cpp b9692's <c>llama-fit-params</c> tool, including the captured
///     unchanged-sentinel output and the verbose server evidence required to turn automatic full offload into a concrete
///     replay vector.
/// </summary>
public sealed class LlamaFitParamsOutputParserTests
{
    [Test]
    public void TryParseFittedArgs_ConcreteOutputWithOverride_ReturnsCompleteReplay()
    {
        // Representative concrete b9692 grammar, reduced to one tensor override for a focused assertion.
        string[] fitParamsOutput =
        [
            "ggml_cuda_init: found 1 CUDA devices:",
            @"-c 4096 -ngl 48 -ot ""blk\.14\.ffn_(up|down|gate)_(ch|)exps=CPU"""
        ];

        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(fitParamsOutput);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.False(draft.ExploreMode);
        AssertEx.Equal(expected: 4096, draft.CtxSize);
        AssertEx.Equal(expected: 48, draft.NGpuLayers!.Value);
        AssertEx.Null(draft.TensorSplit);
        AssertEx.Equal(@"blk\.14\.ffn_(up|down|gate)_(ch|)exps=CPU", draft.OverrideTensor);
    }

    [Test]
    public void TryParseFittedArgs_ExpertOffloadSpawn_FreezesTheEquivalentOverrideTensor()
    {
        // The helper is handed --cpu-moe, fits against it, and echoes the override back verbatim (upstream
        // tools/fit-params/fit-params.cpp prints every tensor_buft_override as -ot). That -ot IS the frozen expert
        // placement, so the replay reproduces what the admission ledger booked without ever emitting --cpu-moe.
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs([@"-c 8192 -ngl 48 -ot ""\.ffn_(up|down|gate|gate_up)_(ch|)exps=CPU"""],
            startupOutput: [],
            successfulLaunchArguments: ["-m", "/models/moe.gguf", "--fit", "on", "--cpu-moe"]);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Equal(@"\.ffn_(up|down|gate|gate_up)_(ch|)exps=CPU", draft.OverrideTensor);
        AssertEx.Equal(expected: 48, draft.NGpuLayers!.Value);
    }

    [Test]
    [Arguments("--cpu-moe")]
    [Arguments("-cmoe")]
    public void TryParseFittedArgs_ExpertOffloadSpawnWithoutAnOverride_ProvesNoReplay(string flag)
    {
        // A fit line with no -ot cannot express the placement the spawn ran under. Drafting it would freeze a replay
        // that launches the experts back onto the GPU, outside the footprint admission reserved, so no draft is made.
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl 48"],
            startupOutput: [],
            successfulLaunchArguments: ["-m", "/models/moe.gguf", "--fit", "on", flag]);

        AssertEx.Null(resolved);
    }

    [Test]
    public void TryParseFittedArgs_ResidentSpawn_IsUnchangedByTheExpertOffloadRule()
    {
        // The byte-identical pin: a dense/fully-resident explore names no expert flag, so the draft is what it always
        // was -- an absent -ot stays absent and still yields a complete replay.
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl 48"],
            startupOutput: [],
            successfulLaunchArguments: ["-m", "/models/dense.gguf", "--fit", "on"]);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 8192, draft.CtxSize);
        AssertEx.Equal(expected: 48, draft.NGpuLayers!.Value);
        AssertEx.Null(draft.OverrideTensor);
    }

    [Test]
    public void TryParseFittedArgs_MultiDeviceGrammar_PreservesTensorSplit()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl 32 -ts 7,3"]);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 8192, draft.CtxSize);
        AssertEx.Equal(expected: 32, draft.NGpuLayers!.Value);
        AssertEx.Equal("7,3", draft.TensorSplit);
        AssertEx.Null(draft.OverrideTensor);
    }

    [Test]
    public void TryParseFittedArgs_OptimizedSuccessfulLaunch_PreservesKvTypesAndFlashAttention()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl 32"],
            startupOutput: [],
            successfulLaunchArguments: ["-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0"]);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Equal("q8_0", draft.KvTypeK);
        AssertEx.Equal("q8_0", draft.KvTypeV);
        AssertEx.True(draft.FlashAttn);
    }

    [Test]
    public void TryParseFittedArgs_SafeSuccessfulLaunch_PreservesAbsentKvAndFlashAttention()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl 32"],
            startupOutput: [],
            successfulLaunchArguments: ["--fit", "on", "-c", "8192"]);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Null(draft.KvTypeK);
        AssertEx.Null(draft.KvTypeV);
        AssertEx.False(draft.FlashAttn);
    }

    [Test]
    [Arguments("-ctk q8_0 -ctv q8_0")]
    [Arguments("-fa on")]
    [Arguments("-fa on -ctk q8_0")]
    [Arguments("-fa on -ctk q8_0 -ctv q4_0")]
    [Arguments("-fa off -ctk q8_0 -ctv q8_0")]
    public void TryParseFittedArgs_InconsistentSuccessfulLaunchPolicy_ReturnsNull(string launchArguments)
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl 32"],
            startupOutput: [],
            successfulLaunchArguments: launchArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        AssertEx.Null(resolved);
    }

    [Test]
    public void TryParseFittedArgs_CapturedUnresolvedDefaults_ReturnsNull()
    {
        // Captured from the real b9692 helper. These are llama.cpp sentinels, not frozen values:
        // context 0 means the model-trained context and GPU layers -1 means automatic placement.
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 0 -ngl -1"],
            ["load_tensors: offloaded 25/25 layers to GPU"]);

        AssertEx.Null(resolved);
    }

    [Test]
    public void TryParseFittedArgs_AutomaticLayersWithFullOffloadEvidence_NormalizesToExplicitAllLayers()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl -1"],
            ["load_tensors: offloaded 25/25 layers to GPU"]);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 8192, draft.CtxSize);
        AssertEx.Equal(expected: -2, draft.NGpuLayers!.Value);
    }

    [Test]
    public void TryParseFittedArgs_AutomaticLayersWithoutFullOffloadEvidence_ReturnsNull()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl -1"],
            ["load_tensors: offloaded 24/25 layers to GPU"]);

        AssertEx.Null(resolved);
    }

    [Test]
    public void TryParseFittedArgs_AutomaticLayersWithMixedStartupPlacements_ReturnsNull()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl -1"],
        [
            "load_tensors: offloaded 25/25 layers to GPU",
            "load_tensors: offloaded 4/10 layers to GPU"
        ]);

        AssertEx.Null(resolved);
    }

    [Test]
    public void TryParseFittedArgs_ExplicitAllLayers_PreservesConcreteAllLayersValue()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 8192 -ngl -2"]);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 8192, draft.CtxSize);
        AssertEx.Equal(expected: -2, draft.NGpuLayers!.Value);
    }

    [Test]
    public void TryParseFittedArgs_ContextWithoutGpuPlacement_ReturnsNull()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs(["-c 4096"]);

        AssertEx.Null(resolved);
    }

    [Test]
    public void TryParseFittedArgs_StartupLogLookalikes_ReturnsNull()
    {
        var resolved = LlamaFitParamsOutputParser.TryParseFittedArgs([
            "llama_context: n_ctx = 4096",
            "load_tensors: offloaded 32/32 layers to GPU",
            "common_params_fit: successfully fit params to free device memory"
        ]);

        AssertEx.Null(resolved);
    }
}
