namespace XE_Local_AI_Engine.Tests.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ModelNameValidatorTests
{
    private readonly ModelNameValidator _validator = new(Options.Create(new SecurityOptions()));

    [Test]
    public void IsValid_WhenInputIsNull_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid(null));
    }

    [Test]
    public void IsValid_WhenInputIsEmpty_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid(string.Empty));
    }

    [Test]
    public void IsValid_WhenInputIsWhitespace_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid("  "));
    }

    [Test]
    public void IsValid_WhenNameIsSimple_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid("llama3"));
    }

    [Test]
    public void IsValid_WhenNameContainsColon_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid("qwen3.5:0.8b"));
    }

    [Test]
    public void IsValid_WhenNameContainsDotAndColon_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid("ollama3.2:latest"));
    }

    [Test]
    public void IsValid_WhenNameExceedsMaxLength_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid(new string(c: 'a', count: 151)));
    }

    [Test]
    [Arguments("hf.co/unsloth/gemma-3-12b-it-GGUF:Q8_0")]
    [Arguments("huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF:Q4_K_M")]
    [Arguments("hf.co/org/repo")]
    // Bare org/repo[:quant] — what first-run provisioning and GGUF pulls produce (the hf.co/ prefix is optional).
    [Arguments("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M")]
    [Arguments("foo/bar")]
    [Arguments("org/repo:Q4_K_M")]
    public void IsValid_WhenNameIsHuggingFaceGgufReference_ReturnsTrue(string modelName)
    {
        AssertEx.True(_validator.IsValid(modelName));
    }

    [Test]
    [Arguments("hf.co/a/b/c")]
    [Arguments("a/b/c")]
    [Arguments("hf.co/../secret")]
    public void IsValid_WhenNameContainsDisallowedSlashForm_ReturnsFalse(string modelName)
    {
        AssertEx.False(_validator.IsValid(modelName));
    }

    [Test]
    public void IsValid_WhenNameContainsBackslash_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid("foo\\bar"));
    }

    [Test]
    public void IsValid_WhenNameContainsPathTraversal_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid("../etc/passwd"));
    }

    [Test]
    public void IsValid_WhenNameContainsRemoteUri_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid("http://evil.com/model"));
    }

    [Test]
    public void IsValid_WhenNameContainsFileUri_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid("file:///etc/passwd"));
    }

    [Test]
    public void IsValid_WhenNameContainsSpaces_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid("model name"));
    }

    [Test]
    [Arguments("ext:unsloth-box/qwen3")]
    // A wire id may carry the slash and colon real remote ids use — the exact shapes the general allow-pattern rejects.
    [Arguments("ext:box/unsloth/Qwen3.8-27B-GGUF")]
    [Arguments("ext:box/llama3:8b")]
    [Arguments("ext:b/org/model:Q4_K_M")]
    public void IsValid_WhenNameIsAnExternalModelId_ReturnsTrue(string modelName)
    {
        AssertEx.True(_validator.IsValid(modelName));
    }

    [Test]
    [Arguments("ext:")]
    [Arguments("ext:box")]
    [Arguments("ext:box/")]
    [Arguments("ext:BAD_SLUG/model")]
    [Arguments("ext:box/../etc/passwd")]
    [Arguments("ext:box/has space")]
    [Arguments("ext:box/back\\slash")]
    [Arguments("ext:box/http://evil.example/model")]
    public void IsValid_WhenExternalIdViolatesTheNamespacedGrammar_ReturnsFalse(string modelName)
    {
        AssertEx.False(_validator.IsValid(modelName));
    }

    [Test]
    public void IsValid_ExternalIdLengthBound_IsRaisedForExtIdsOnly()
    {
        // The 165-char ceiling exists because ext: ids are structurally longer than any other provider's; it must NOT
        // leak into the general bound, or the raised limit would be a hole rather than a scoped widening.
        var external = "ext:" + new string(c: 'a', count: 32) + "/" + new string(c: 'b', count: 128);
        AssertEx.Equal(165, external.Length);

        AssertEx.True(_validator.IsValid(external));
        AssertEx.False(_validator.IsValid(external + "b"));
        AssertEx.False(_validator.IsValid(new string(c: 'a', count: 151)));
    }

    [Test]
    public void GetValidationError_WhenNameIsValid_ReturnsNull()
    {
        AssertEx.Null(_validator.GetValidationError("qwen3.5:0.8b"));
    }

    [Test]
    public void GetValidationError_WhenNameIsNull_ReturnsNull()
    {
        AssertEx.Null(_validator.GetValidationError(null));
    }

    [Test]
    public void GetValidationError_WhenNameIsInvalid_ReturnsMessage()
    {
        AssertEx.NotNull(_validator.GetValidationError("../etc"));
    }
}
