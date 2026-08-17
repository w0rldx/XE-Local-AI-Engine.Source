namespace XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1;

using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The group key is the BASE model. It used to be the model content fingerprint, which is the exact opposite of
///     what an operator comparing quants needs: two quants of one model have different content by definition, so every
///     quant became its own group and "which quant of this model is best" could never be asked.
/// </summary>
public sealed class BenchmarkModelGroupKeyTests
{
    [Test]
    public void From_ForHuggingFaceModels_StripsTheQuantTagAndLowercases()
    {
        // Repo ids are case-insensitive, so the same repo referenced with two casings must not split into two groups.
        AssertEx.Equal("unsloth/qwen3.8-27b-gguf", BenchmarkModelGroupKey.From("unsloth/Qwen3.8-27B-GGUF:Q4_K_M", LocalModelOrigin.HuggingFace));
        AssertEx.Equal(BenchmarkModelGroupKey.From("unsloth/Qwen3.8-27B-GGUF:Q4_K_M", LocalModelOrigin.HuggingFace),
            BenchmarkModelGroupKey.From("UNSLOTH/qwen3.8-27b-gguf:Q8_0", LocalModelOrigin.HuggingFace),
            "Two quants of one repo, however capitalized, are one group.");
    }

    [Test]
    [Arguments(LocalModelOrigin.Imported)]
    [Arguments(LocalModelOrigin.Trained)]
    public void From_ForLocalModels_StripsTheTagButKeepsTheOperatorsCasing(LocalModelOrigin origin)
    {
        // An imported or trained name is the identity the operator chose; folding its case would rename it in the UI.
        AssertEx.Equal("My-Tuned-Model", BenchmarkModelGroupKey.From("My-Tuned-Model:Q5_K_M", origin));
        AssertEx.Equal("My-Tuned-Model", BenchmarkModelGroupKey.From("My-Tuned-Model", origin));
    }

    [Test]
    [Arguments("model")]
    [Arguments("model:")]
    [Arguments(":model")]
    public void From_ForANameWithNoUsableTag_ReturnsItUnchanged(string modelName) =>

        // Truncating any of these to the empty string would collapse every such run into one meaningless group.
        AssertEx.Equal(modelName, BenchmarkModelGroupKey.From(modelName, LocalModelOrigin.Imported));

    [Test]
    public void QuantTag_ReadsWhatFromStripped()
    {
        AssertEx.Equal("Q4_K_M", BenchmarkModelGroupKey.QuantTag("unsloth/Qwen3.8-27B-GGUF:Q4_K_M"));
        AssertEx.Equal(string.Empty, BenchmarkModelGroupKey.QuantTag("model"));
        AssertEx.Equal(string.Empty, BenchmarkModelGroupKey.QuantTag("model:"), "A bare trailing colon is not a tag.");
    }

    [Test]
    public void From_ForAnUnknownOrigin_KeepsTheCasing()
    {
        // A legacy row carries no origin. Lower-casing it on a guess would rename models nobody asked about.
        AssertEx.Equal("Legacy-Model", BenchmarkModelGroupKey.From("Legacy-Model:Q4_K_M", origin: null));
    }
}
