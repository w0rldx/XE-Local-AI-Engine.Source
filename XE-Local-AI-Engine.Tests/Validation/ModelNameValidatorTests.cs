namespace XE_Local_AI_Engine.Tests.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Services.Validation;
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
        AssertEx.True(_validator.IsValid("qwen3.5:9b"));
    }

    [Test]
    public void IsValid_WhenNameContainsDotAndColon_ReturnsTrue()
    {
        AssertEx.True(_validator.IsValid("ollama3.2:latest"));
    }

    [Test]
    public void IsValid_WhenNameExceedsMaxLength_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid(new string('a', 101)));
    }

    [Test]
    public void IsValid_WhenNameContainsForwardSlash_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid("foo/bar"));
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
    public void IsValid_WhenNameContainsSpaces_ReturnsFalse()
    {
        AssertEx.False(_validator.IsValid("model name"));
    }

    [Test]
    public void GetValidationError_WhenNameIsValid_ReturnsNull()
    {
        AssertEx.Null(_validator.GetValidationError("qwen3.5:9b"));
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
