namespace XE_Local_AI_Engine.Tests.ModelFit.Catalog;

using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelCatalogValidator" /> accept/reject cases: the schema-version gate, the per-field checks, and the
///     tolerant-parse-failure path every bundled and remote-refreshed catalog must pass through.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ModelCatalogValidatorTests
{
    private const string ValidEntryJson =
        """
        {
          "id": "test-model",
          "family": "Test",
          "displayName": "Test Model",
          "publisher": "Test Org",
          "ggufRepo": "org/test-GGUF",
          "license": "mit",
          "tier": "A",
          "useCases": ["general"],
          "totalParamsB": 7.0,
          "activeParamsB": null,
          "moe": false,
          "contextLength": 8192,
          "minLlamaCppTag": "b9692",
          "releaseDate": "2026-01-01"
        }
        """;

    [Test]
    public void Validate_WhenDocumentIsWellFormed_Succeeds()
    {
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "updatedAt": "2026-01-01", "models": [{{ValidEntryJson}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.True(result.IsValid, string.Join("; ", result.Errors));
        AssertEx.NotNull(result.Document);
        AssertEx.Equal(expected: 1, result.Document!.Models.Count);
        // A schema-v1 entry without the optional "tested" key reads as untested.
        AssertEx.False(result.Document.Models[0].Tested);
    }

    [Test]
    public void Validate_WhenTestedIsTrue_CarriesTheFlag()
    {
        var entry = WithTestedFields("\"tested\": true, \"testedQuant\": \"UD-Q4_K_M\", \"testedSizeBytes\": 2099502400");
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.True(result.IsValid, string.Join("; ", result.Errors));
        AssertEx.True(result.Document!.Models[0].Tested);
        AssertEx.Equal("UD-Q4_K_M", result.Document.Models[0].TestedQuant);
        AssertEx.Equal(2099502400L, result.Document.Models[0].TestedSizeBytes);
    }

    [Test]
    [Arguments("\"tested\": true", "models[0].testedQuant")]
    [Arguments("\"tested\": true, \"testedQuant\": \" \", \"testedSizeBytes\": 1", "models[0].testedQuant")]
    [Arguments("\"tested\": true, \"testedQuant\": \"Q4_K_M\"", "models[0].testedSizeBytes")]
    [Arguments("\"tested\": true, \"testedQuant\": \"Q4_K_M\", \"testedSizeBytes\": 0", "models[0].testedSizeBytes")]
    public void Validate_WhenTestedLacksQuantOrSize_Fails(string fields, string expectedPath)
    {
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{WithTestedFields(fields)}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.StartsWith(expectedPath, StringComparison.Ordinal)), string.Join("; ", result.Errors));
    }

    [Test]
    [Arguments("\"testedQuant\": \"Q4_K_M\"")]
    [Arguments("\"testedSizeBytes\": 2099502400")]
    [Arguments("\"tested\": false, \"testedQuant\": \"Q4_K_M\", \"testedSizeBytes\": 2099502400")]
    public void Validate_WhenNotTestedButCarriesTestedFields_Fails(string fields)
    {
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{WithTestedFields(fields)}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.StartsWith("models[0].testedQuant and testedSizeBytes", StringComparison.Ordinal)),
            string.Join("; ", result.Errors));
    }

    [Test]
    public void Validate_WhenSchemaVersionUnsupported_Fails()
    {
        var json = $$"""{ "schemaVersion": 2, "catalogVersion": "1.0.0", "models": [{{ValidEntryJson}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("schemaVersion", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenJsonIsMalformed_FailsWithoutThrowing()
    {
        var result = ModelCatalogValidator.Validate("{ not json");

        AssertEx.False(result.IsValid);
        AssertEx.Null(result.Document);
    }

    [Test]
    public void Validate_WhenJsonIsEmpty_Fails()
    {
        var result = ModelCatalogValidator.Validate(rawJson: null);

        AssertEx.False(result.IsValid);
    }

    [Test]
    public void Validate_WhenDuplicateIds_Fails()
    {
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{ValidEntryJson}}, {{ValidEntryJson}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("duplicate", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenGgufRepoMissingSlash_Fails()
    {
        var entry = ValidEntryJson.Replace("\"org/test-GGUF\"", "\"not-a-repo-id\"", StringComparison.Ordinal);
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("ggufRepo", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenTierUnknown_Fails()
    {
        var entry = ValidEntryJson.Replace("\"A\"", "\"Z\"", StringComparison.Ordinal);
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("tier", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenUseCaseNotAllowlisted_Fails()
    {
        var entry = ValidEntryJson.Replace("\"general\"", "\"astrology\"", StringComparison.Ordinal);
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("useCases", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenMoeTrueButActiveParamsMissing_Fails()
    {
        var entry = ValidEntryJson.Replace("\"moe\": false", "\"moe\": true", StringComparison.Ordinal);
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("activeParamsB", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenActiveParamsExceedsTotal_Fails()
    {
        var entry = ValidEntryJson
                    .Replace("\"moe\": false", "\"moe\": true", StringComparison.Ordinal)
                    .Replace("\"activeParamsB\": null", "\"activeParamsB\": 99.0", StringComparison.Ordinal);
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("cannot exceed", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenMinLlamaCppTagMalformed_Fails()
    {
        var entry = ValidEntryJson.Replace("\"b9692\"", "\"not-a-tag\"", StringComparison.Ordinal);
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("minLlamaCppTag", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenReleaseDateNotIso_Fails()
    {
        var entry = ValidEntryJson.Replace("\"2026-01-01\"", "\"Jan 2026\"", StringComparison.Ordinal);
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [{{entry}}] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(e => e.Contains("releaseDate", StringComparison.Ordinal)));
    }

    [Test]
    public void Validate_WhenModelsArrayEmpty_Succeeds()
    {
        var json = """{ "schemaVersion": 1, "catalogVersion": "1.0.0", "models": [] }""";

        var result = ModelCatalogValidator.Validate(json);

        AssertEx.True(result.IsValid, string.Join("; ", result.Errors));
        AssertEx.Empty(result.Document!.Models);
    }

    private static string WithTestedFields(string fields)
    {
        return ValidEntryJson.Replace("\"releaseDate\": \"2026-01-01\"", $"\"releaseDate\": \"2026-01-01\", {fields}", StringComparison.Ordinal);
    }
}
