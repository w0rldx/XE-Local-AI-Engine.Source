namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Lane E tests for <see cref="RecommendationJsonParser" />: the Context column must reflect the model's advertised
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
        AssertEx.Equal(131072, result.Recommendations[0].ContextTokens);
    }

    [Test]
    public void Parse_WhenOnlyEffectiveContextPresent_FallsBackToEffective()
    {
        const string json = """
                            { "models": [ { "name": "tiny", "effective_context_length": 4096 } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        AssertEx.Equal(4096, result.Recommendations[0].ContextTokens);
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
        // Lane H3: release_date rides the existing diagnostics blob (no new column) so the read mapper can surface it.
        const string json = """
                            { "models": [ { "name": "qwen3-coder", "release_date": "2026-01-15" } ] }
                            """;

        var result = RecommendationJsonParser.Parse(json);

        AssertEx.True(result.IsSuccess);
        var diagnostics = AssertEx.NotNull(result.Recommendations[0].DiagnosticsJson);
        AssertEx.Contains(diagnostics, "release_date");
        AssertEx.Contains(diagnostics, "2026-01-15");
    }
}
