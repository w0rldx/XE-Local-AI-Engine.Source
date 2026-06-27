namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for <see cref="LlamaParamsFitParser" />. The fit-banner format is ASSUMED (see the parser remarks) and
///     cannot be reproduced in a no-GPU environment, so these assert against representative fixture strings: the parser
///     pulls each field with its own tolerant regex, and returns <see langword="null" /> when the required context-size
///     anchor is absent (so the caller keeps the live <c>--fit</c> result rather than freezing a bad draft).
/// </summary>
public sealed class LlamaParamsFitParserTests
{
    [Test]
    public void FitParser_ParsesFittedArgs_FromFixture()
    {
        string[] startupOutput =
        [
            "ggml_cuda_init: found 1 CUDA devices:",
            "llama_params_fit: n_ctx = 8192",
            "llama_params_fit: n_gpu_layers = 32",
            "llama_params_fit: tensor_split = 0.6,0.4",
            "llama_params_fit: override_tensor = exps=CPU"
        ];

        var resolved = LlamaParamsFitParser.TryParseFittedArgs(startupOutput);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.False(draft.ExploreMode, "A parsed fit draft is a replay profile, not explore mode.");
        AssertEx.Equal(expected: 8192, draft.CtxSize);
        AssertEx.Equal(expected: 32, draft.NGpuLayers!.Value);
        AssertEx.Equal("0.6,0.4", draft.TensorSplit);
        AssertEx.Equal("exps=CPU", draft.OverrideTensor);
    }

    [Test]
    public void FitParser_ParsesContextOnly_WhenOnlyContextPresent()
    {
        // Tolerant: a missing optional field is simply left unset; only the context size is required.
        string[] startupOutput =
        [
            "some unrelated banner line",
            "context size: 4096"
        ];

        var resolved = LlamaParamsFitParser.TryParseFittedArgs(startupOutput);

        var draft = AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 4096, draft.CtxSize);
        AssertEx.Null(draft.NGpuLayers);
        AssertEx.Null(draft.TensorSplit);
        AssertEx.Null(draft.OverrideTensor);
    }

    [Test]
    public void FitParser_ReturnsNull_WhenNoContextFound()
    {
        string[] startupOutput =
        [
            "ggml_cuda_init: found 1 CUDA devices:",
            "llama_params_fit: n_gpu_layers = 32"
        ];

        var resolved = LlamaParamsFitParser.TryParseFittedArgs(startupOutput);

        AssertEx.Null(resolved);
    }
}
