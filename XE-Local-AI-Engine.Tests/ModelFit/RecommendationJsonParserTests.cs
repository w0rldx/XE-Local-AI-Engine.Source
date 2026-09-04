namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="RecommendationJsonParser" /> tests: the Context column must reflect the model's advertised
///     maximum window (<c>context_length</c>), not llmfit's <c>effective_context_length</c> memory-estimation cap (which
///     defaults to 8192 and previously masked every model's real context). The parser falls back to
///     <c>effective_context_length</c> only when <c>context_length</c> is absent.
/// </summary>
public sealed class RecommendationJsonParserTests
{
    [Test]
    public void Parse_WhenBothContextFieldsPresent_PrefersRealContextLength()
    {
        const string json = """
                            { "models": [ { "name": "Qwen/Qwen3-Coder-30B", "context_length": 131072, "effective_context_length": 8192 } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        AssertEx.NotEmpty(result.Recommendations);
        AssertEx.Equal(expected: 131072, result.Recommendations[0].ContextTokens);
    }

    [Test]
    public void Parse_WhenOnlyEffectiveContextPresent_FallsBackToEffective()
    {
        const string json = """
                            { "models": [ { "name": "tiny", "effective_context_length": 4096 } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        AssertEx.Equal(expected: 4096, result.Recommendations[0].ContextTokens);
    }

    [Test]
    public void Parse_WhenNeitherContextFieldPresent_ContextIsNull()
    {
        const string json = """
                            { "models": [ { "name": "no-context" } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        AssertEx.Null(result.Recommendations[0].ContextTokens);
    }

    [Test]
    public void RecommendationParser_Adapted_MapsAdvisorJson()
    {
        // The advisor emits the same {models:[],system:{}} shape with vram_required_gb + repo_id/file_name; the reused
        // scaffold maps name→PullModelName, best_quant→Quantization, vram_required_gb→RequiredVramMb (was always null).
        const string json = """
                            {
                              "models": [
                                {
                                  "name": "org/qwen-GGUF:Q4_K_M",
                                  "best_quant": "Q4_K_M",
                                  "fit_level": "GPU",
                                  "run_mode": "Gpu",
                                  "score": 12.5,
                                  "memory_required_gb": 8.0,
                                  "vram_required_gb": 8.0,
                                  "repo_id": "org/qwen-GGUF",
                                  "file_name": "qwen.Q4_K_M.gguf",
                                  "installed": false
                                }
                              ],
                              "system": { "gpu_accel": true, "gpu_vendor": "Nvidia", "vram_known": true }
                            }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        var row = result.Recommendations.Single();
        AssertEx.Equal("org/qwen-GGUF:Q4_K_M", row.ModelName);
        AssertEx.Equal("org/qwen-GGUF:Q4_K_M", row.PullModelName!);
        AssertEx.Equal("Q4_K_M", row.Quantization!);
        AssertEx.Equal(8.0 * 1024d, row.RequiredVramMb!.Value);
        AssertEx.Equal(8.0 * 1024d, row.RequiredRamMb!.Value);
        AssertEx.NotNull(result.SystemDiagnosticsJson);
    }

    [Test]
    public void Parse_WhenReleaseDatePresent_CarriesItIntoDiagnostics()
    {
        // release_date rides the existing diagnostics blob (no new column) so the read mapper can surface it.
        const string json = """
                            { "models": [ { "name": "qwen3-coder", "release_date": "2026-01-15" } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        var diagnostics = AssertEx.NotNull(result.Recommendations[0].DiagnosticsJson);
        AssertEx.Contains(diagnostics, "release_date");
        AssertEx.Contains(diagnostics, "2026-01-15");
    }

    [Test]
    public void Parse_WhenIsTrustedPublisherPresent_CarriesItIntoDiagnostics()
    {
        // is_trusted_publisher rides the existing diagnostics blob (no new column) so the read mapper can surface it.
        const string json = """
                            { "models": [ { "name": "qwen3-coder", "is_trusted_publisher": false } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        var diagnostics = AssertEx.NotNull(result.Recommendations[0].DiagnosticsJson);
        AssertEx.Contains(diagnostics, "is_trusted_publisher");
    }

    [Test]
    public void Parse_WhenBothSignalsPresent_CarriesBothIntoDiagnostics()
    {
        // Both signals ride the same blob; confirms they coexist without clobbering each other.
        const string json = """
                            { "models": [ { "name": "qwen3-coder", "release_date": "2026-06-01", "is_trusted_publisher": true } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        var diagnostics = AssertEx.NotNull(result.Recommendations[0].DiagnosticsJson);
        AssertEx.Contains(diagnostics, "release_date");
        AssertEx.Contains(diagnostics, "is_trusted_publisher");
    }

    [Test]
    public void Parse_WhenCatalogFieldsPresent_RoundTripsThemIntoDiagnostics()
    {
        // Catalog-lane fields (section/tier/catalog metadata/MoE-offload split) ride the same diagnostics extensibility
        // seam as release_date/is_trusted_publisher — additive, no new columns.
        const string json = """
                            {
                              "models": [
                                {
                                  "name": "org/moe-model:Q4_K_M",
                                  "section": "recommended",
                                  "tier": "S",
                                  "catalog_id": "moe-model",
                                  "catalog_display_name": "MoE Model 30B-A3B",
                                  "catalog_notes": "Runs with experts offloaded to system RAM.",
                                  "expert_offload": true,
                                  "gpu_gb": 10.5,
                                  "cpu_gb": 6.25
                                }
                              ]
                            }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        var diagnostics = AssertEx.NotNull(result.Recommendations[0].DiagnosticsJson);
        AssertEx.Contains(diagnostics, "\"section\":\"recommended\"");
        AssertEx.Contains(diagnostics, "\"tier\":\"S\"");
        AssertEx.Contains(diagnostics, "\"catalog_id\":\"moe-model\"");
        AssertEx.Contains(diagnostics, "\"expert_offload\":true");
        AssertEx.Contains(diagnostics, "\"gpu_gb\":10.5");
        AssertEx.Contains(diagnostics, "\"cpu_gb\":6.25");
    }

    [Test]
    public void Parse_WhenKvPerTokenFieldsPresent_RoundTripsThemIntoDiagnostics()
    {
        // The KV cost per token, its element size and the attention tag ride the same diagnostics seam. The quant label
        // must survive with the number: alone, the byte count is ambiguous by a factor of two.
        const string json = """
                            {
                              "models": [
                                {
                                  "name": "org/mla-model:Q4_K_M",
                                  "kv_bytes_per_token": 640,
                                  "kv_bytes_per_token_quant": "Q8_0",
                                  "attention_arch": "mla"
                                }
                              ]
                            }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        var diagnostics = AssertEx.NotNull(result.Recommendations[0].DiagnosticsJson);
        AssertEx.Contains(diagnostics, "\"kv_bytes_per_token\":640");
        AssertEx.Contains(diagnostics, "\"kv_bytes_per_token_quant\":\"Q8_0\"");
        AssertEx.Contains(diagnostics, "\"attention_arch\":\"mla\"");
    }

    [Test]
    public void Parse_WhenSectionAbsent_RowStillParsesSuccessfully()
    {
        // A pre-existing (pre-catalog-lane) snapshot row has no "section" key — the parser must not require it (the
        // read mapper, not the parser, supplies the "explore" default).
        const string json = """{ "models": [ { "name": "org/model:Q4_K_M" } ] }""";

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        AssertEx.Equal(expected: 1, result.Recommendations.Count);
    }
}
