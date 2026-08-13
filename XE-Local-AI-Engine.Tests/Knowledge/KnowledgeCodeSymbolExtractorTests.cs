namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class KnowledgeCodeSymbolExtractorTests
{
    [Arguments("public sealed class KnowledgeSearchService", "KnowledgeSearchService")]
    [Arguments("def retrieve_context(query):", "retrieve_context")]
    [Arguments("export async function searchKnowledge(query: string) {", "searchKnowledge")]
    [Arguments("fn reciprocal_rank_fusion(results: Vec<Result>) {", "reciprocal_rank_fusion")]
    [Test]
    public void ExtractPrimary_CommonDeclarations_ReturnsSearchableSymbol(string content, string expected)
    {
        AssertEx.Equal(expected, KnowledgeCodeSymbolExtractor.ExtractPrimary(content));
    }

    [Test]
    public void ExtractPrimary_ControlFlow_DoesNotInventSymbol()
    {
        AssertEx.Null(KnowledgeCodeSymbolExtractor.ExtractPrimary("if (results.Count == 0)\n{\n    return;\n}"));
    }
}
