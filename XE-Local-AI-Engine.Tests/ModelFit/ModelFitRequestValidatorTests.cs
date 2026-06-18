namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitRequestValidator" /> tests: the mandatory server-side intent validation (llmfit does
///     no input validation of its own) rejects unknown use cases, out-of-range limits, unsupported providers, and bad
///     benchmark model names, and accepts the valid recommend/benchmark shapes.
/// </summary>
public sealed class ModelFitRequestValidatorTests
{
    private readonly ModelFitRequestValidator _validator;

    public ModelFitRequestValidatorTests()
    {
        var securityOptions = Options.Create(new SecurityOptions
        {
            AllowedModelNamePattern = "^[a-zA-Z0-9._:-]+$"
        });
        _validator = new ModelFitRequestValidator(new ModelNameValidator(securityOptions));
    }

    [Test]
    public void Validate_ValidRecommend_IsValid()
    {
        AssertEx.True(_validator.IsValid(ModelFitOperation.Recommend, "coding", 5, "ollama", null));
    }

    [Test]
    public void Validate_RecommendWithoutUseCase_IsValid()
    {
        // use-case is optional for recommend; only a supplied value must be allowlisted.
        AssertEx.True(_validator.IsValid(ModelFitOperation.Recommend, null, 1, "ollama", null));
    }

    [Test]
    [Arguments("unknown")]
    [Arguments("Coding")]
    [Arguments("rag")]
    public void Validate_RecommendWithUnknownUseCase_IsInvalid(string useCase)
    {
        AssertEx.False(_validator.IsValid(ModelFitOperation.Recommend, useCase, 5, "ollama", null));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(51)]
    [Arguments(500)]
    [Arguments(1000)]
    public void Validate_RecommendWithOutOfRangeLimit_IsInvalid(int limit)
    {
        AssertEx.False(_validator.IsValid(ModelFitOperation.Recommend, "coding", limit, "ollama", null));
    }

    [Test]
    [Arguments(1)]
    [Arguments(12)]
    [Arguments(50)]
    public void Validate_RecommendWithHighButInRangeLimit_IsValid(int limit)
    {
        // The advisor only inspects a small fixed window of repos, so the limit ceiling is 50 (matching the handler's
        // JSON-schema maximum). The full inclusive 1..50 range validates.
        AssertEx.True(_validator.IsValid(ModelFitOperation.Recommend, "coding", limit, "ollama", null));
    }

    [Test]
    [Arguments("openai")]
    [Arguments("")]
    [Arguments("OLLAMA")]
    public void Validate_WithUnsupportedProvider_IsInvalid(string provider)
    {
        AssertEx.False(_validator.IsValid(ModelFitOperation.Recommend, "coding", 5, provider, null));
    }

    [Test]
    public void Validate_WithLlamaCppProvider_IsValid()
    {
        // The local advisor (Lane C) targets llama.cpp in-process; the validator now allowlists it alongside ollama.
        AssertEx.True(_validator.IsValid(ModelFitOperation.Recommend, "coding", 5, "llama.cpp", null));
    }

    [Test]
    public void Validate_ValidBenchmark_IsValid()
    {
        AssertEx.True(_validator.IsValid(ModelFitOperation.Benchmark, null, 0, "ollama", "llama3.1:8b"));
    }

    [Test]
    public void Validate_BenchmarkWithoutModelName_IsInvalid()
    {
        AssertEx.False(_validator.IsValid(ModelFitOperation.Benchmark, null, 0, "ollama", null));
    }

    [Test]
    [Arguments("../etc/passwd")]
    [Arguments("a/b")]
    [Arguments("bad name!")]
    public void Validate_BenchmarkWithInvalidModelName_IsInvalid(string modelName)
    {
        AssertEx.False(_validator.IsValid(ModelFitOperation.Benchmark, null, 0, "ollama", modelName));
    }
}
