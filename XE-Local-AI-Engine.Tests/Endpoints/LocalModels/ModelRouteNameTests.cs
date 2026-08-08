namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ModelRouteNameTests
{
    [Test]
    public void Decode_WhenValueIsNull_ReturnsNull()
    {
        AssertEx.Null(ModelRouteName.Decode(null));
    }

    [Test]
    public void Decode_WhenValueContainsEncodedSlash_RestoresHuggingFaceReference()
    {
        // The bound route value still carries literal %2F (Kestrel leaves encoded slashes encoded by design), while
        // %3A has already been decoded to ':' by route binding — both forms must round-trip to the canonical name.
        AssertEx.Equal("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL",
            ModelRouteName.Decode("hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF:UD-Q4_K_XL"));
        AssertEx.Equal("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL",
            ModelRouteName.Decode("hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL"));
    }

    [Test]
    [Arguments("llama3:8b")]
    [Arguments("qwen3.5:0.8b")]
    public void Decode_WhenValueIsPlainTag_IsIdempotent(string modelName)
    {
        AssertEx.Equal(modelName, ModelRouteName.Decode(modelName));
    }

    [Test]
    public void Decode_DoesNotTurnPlusIntoSpace()
    {
        // Uri.UnescapeDataString (not WebUtility.UrlDecode) is used precisely so a literal '+' survives.
        AssertEx.Equal("a+b", ModelRouteName.Decode("a+b"));
    }

    [Test]
    [Arguments("..%2F..%2Fetc", "../../etc")]
    [Arguments("hf.co%2F..%2Fsecret", "hf.co/../secret")]
    [Arguments("foo%5Cbar", "foo\\bar")]
    public void Decode_DoesNotSmuggleTraversalPastTheGuard(string encoded, string expected)
    {
        // Decoding restores the dangerous characters so ModelNameValidator's "..", "\\" and "://" guards can reject them.
        AssertEx.Equal(expected, ModelRouteName.Decode(encoded));
    }
}
