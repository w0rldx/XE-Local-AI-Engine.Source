namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The draft-model classifier. Every input below is a REAL file/repo name observed on the Hub during the
///     2026-07-31 live evaluation: <c>unsloth/gemma-4-12b-it-GGUF</c> ships its drafters under <c>MTP/</c>, while
///     <c>unsloth/Qwen3.6-27B-MTP-GGUF</c> and <c>s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF</c> are ordinary chat repos
///     whose NAMES mention MTP — the classifier must separate the two without a false positive in either direction.
/// </summary>
public sealed class GgufDraftModelTests
{
    [Test]
    [Arguments("MTP/mtp-gemma-4-12b-it-Q8_0.gguf")]
    [Arguments("MTP/mtp-gemma-4-12b-it-BF16.gguf")]
    [Arguments("MTP/mtp-gemma-4-12b-it-F16.gguf")]
    [Arguments("mtp/mtp-gemma-4-12b-it-Q8_0.gguf")]
    [Arguments("mtp-gemma-4-12b-it-Q8_0.gguf")]
    [Arguments(@"MTP\mtp-gemma-4-12b-it-Q8_0.gguf")]
    public void IsDraftFile_ForPublishedDrafters_ReturnsTrue(string fileName)
    {
        AssertEx.True(GgufDraftModel.IsDraftFile(fileName), $"'{fileName}' is a speculative-decoding drafter.");
    }

    [Test]
    // The base weights of the very repo that ships the drafters — the pair the picker showed under one label.
    [Arguments("gemma-4-12b-it-Q8_0.gguf")]
    [Arguments("gemma-4-12b-it-BF16.gguf")]
    [Arguments("gemma-4-12b-it-UD-Q4_K_XL.gguf")]
    // A real 21 GB chat model whose repo/file name merely advertises MTP layers. Classifying it as a drafter would
    // erase the highest-scoring model in the live matrix from the picker.
    [Arguments("Qwen3.6-27B-MTP-Q6_K.gguf")]
    [Arguments("Ornith-1.0-9B-NVFP4-MTP.gguf")]
    // A projector companion is a different family entirely (dropped outright, not marked).
    [Arguments("mmproj-F16.gguf")]
    public void IsDraftFile_ForBaseWeights_ReturnsFalse(string fileName)
    {
        AssertEx.False(GgufDraftModel.IsDraftFile(fileName), $"'{fileName}' is a base-model file, not a drafter.");
    }

    [Test]
    public void MarkQuant_GivesTheDrafterADistinctIdentity_AndIsIdempotent()
    {
        var marked = GgufDraftModel.MarkQuant("Q8_0");

        AssertEx.Equal("MTP-Q8_0", marked);
        AssertEx.True(GgufDraftModel.IsDraftQuant(marked), "A marked quant must be recognized as a draft quant.");
        AssertEx.False(GgufDraftModel.IsDraftQuant("Q8_0"), "A bare base quant must never read as a draft quant.");
        AssertEx.Equal(marked, GgufDraftModel.MarkQuant(marked));
        AssertEx.Equal("Q8_0", GgufDraftModel.StripQuantPrefix(marked));
    }

    [Test]
    public void IsDraftModelName_SeparatesTheDrafterKeyFromTheRealModelKey()
    {
        // The two registry keys the collision produced: without the marker BOTH would be "…-GGUF:Q8_0".
        AssertEx.True(GgufDraftModel.IsDraftModelName("unsloth/gemma-4-12b-it-GGUF:MTP-Q8_0"),
            "The drafter's registry key must be recognized as a draft.");
        AssertEx.False(GgufDraftModel.IsDraftModelName("unsloth/gemma-4-12b-it-GGUF:Q8_0"),
            "The real model's registry key must never be recognized as a draft.");

        // A repo NAME containing MTP is not a marker — only the quant segment is.
        AssertEx.False(GgufDraftModel.IsDraftModelName("unsloth/Qwen3.6-27B-MTP-GGUF:Q6_K"),
            "An MTP-named base repo must never be recognized as a draft.");
        AssertEx.False(GgufDraftModel.IsDraftModelName("unsloth/gemma-4-12b-it-GGUF"), "A bare repo id carries no quant.");
    }
}
