namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Quant-token extraction from GGUF filenames, including Unsloth "Dynamic" (UD) awareness: the UD- marker is
///     preserved as part of the canonical token (UD-Q4_K_XL) so a Dynamic quant is a distinct, selectable identity,
///     while plain quants are unaffected.
/// </summary>
public sealed class GgufQuantParserTests
{
    [Test]
    [Arguments("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", "Q4_K_M")]
    [Arguments("Demo-Model-Q8_0.gguf", "Q8_0")]
    [Arguments("model-q4_k_m.gguf", "Q4_K_M")] // casing normalized to upper
    [Arguments("weights-IQ2_XXS.gguf", "IQ2_XXS")]
    [Arguments("weights-BF16.gguf", "BF16")]
    [Arguments("Qwen2.5-7B-Q3_K_XL.gguf", "Q3_K_XL")]
    [Arguments("gpt-oss-20b-MXFP4.gguf", "MXFP4")] // gpt-oss native format — recognized so it is not skipped
    [Arguments("openai_gpt-oss-120b-mxfp4-00001-of-00002.gguf", "MXFP4")] // sharded + lowercase
    // NVFP4 real filenames, sampled live 2026-07-31. An unrecognized token makes the file fail IsUsableGgufFile, which
    // makes an NVFP4-only repo vanish from search entirely — so recognition here is a discovery concern, not just sizing.
    [Arguments("Qwen3.6-27B-NVFP4.gguf", "NVFP4")] // tngtech/Qwen3.6-27B-NVFP4-GGUF
    [Arguments("ornith-1.0-9b-NVFP4-MTP.gguf", "NVFP4")] // s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF
    [Arguments("gpt-oss-20b-nvfp4.gguf", "NVFP4")] // FreedomAISVR/gpt-oss-20B-NVFP4-GGUF — lowercase
    public void TryParse_ExtractsPlainQuant(string fileName, string expected)
    {
        AssertEx.Equal(expected, GgufQuantParser.TryParse(fileName));
    }

    [Test]
    [Arguments("README.md")]
    [Arguments("model.safetensors")]
    [Arguments("no-recognizable-token.gguf")]
    public void TryParse_ReturnsNull_WhenNoQuantToken(string fileName)
    {
        AssertEx.Null(GgufQuantParser.TryParse(fileName));
    }

    [Test]
    [Arguments("gemma-3-12b-it-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [Arguments("gemma-3-12b-it-UD-Q4_K_M.gguf", "UD-Q4_K_M")]
    [Arguments("gemma-3-12b-it-UD-IQ2_M.gguf", "UD-IQ2_M")]
    [Arguments("model-ud-q4_k_xl.gguf", "UD-Q4_K_XL")] // lowercase marker + token both normalized
    [Arguments("model-UD_Q4_K_M.gguf", "UD-Q4_K_M")] // underscore-separated marker still recognized
    public void TryParse_PreservesUnslothDynamicMarker(string fileName, string expected)
    {
        AssertEx.Equal(expected, GgufQuantParser.TryParse(fileName));
    }

    [Test]
    public void TryParse_DoesNotTreatTrailingUdLetters_AsDynamicMarker()
    {
        // "HUD-" is not the Unsloth marker (the U is glued to an alphanumeric), so the base quant is returned.
        AssertEx.Equal("Q4_K_M", GgufQuantParser.TryParse("some-HUD-Q4_K_M.gguf"));
    }

    [Test]
    public void TryParse_HandlesShardedDynamicFile_TakingTheQuantToken()
    {
        AssertEx.Equal("UD-Q4_K_XL", GgufQuantParser.TryParse("gemma-3-27b-it-UD-Q4_K_XL-00001-of-00002.gguf"));
    }

    [Test]
    [Arguments("UD-Q4_K_XL", true)]
    [Arguments("UD_Q4_K_M", true)]
    [Arguments("ud-q4_k_m", true)]
    [Arguments("Q4_K_M", false)]
    [Arguments("IQ2_XXS", false)]
    public void IsDynamic_DetectsTheMarker(string quant, bool expected)
    {
        AssertEx.Equal(expected, GgufQuantParser.IsDynamic(quant));
    }

    [Test]
    [Arguments("UD-Q4_K_XL", "Q4_K_XL")]
    [Arguments("UD_Q4_K_M", "Q4_K_M")]
    [Arguments("Q4_K_M", "Q4_K_M")] // no marker → unchanged
    public void StripDynamicPrefix_RemovesTheMarker(string quant, string expected)
    {
        AssertEx.Equal(expected, GgufQuantParser.StripDynamicPrefix(quant));
    }
}
