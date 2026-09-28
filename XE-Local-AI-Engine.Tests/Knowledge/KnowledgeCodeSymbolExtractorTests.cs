namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
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

    [Arguments("namespace XE_Local_AI_Engine.Knowledge;\n\npublic sealed class KnowledgeSearchService\n{", "KnowledgeSearchService")]
    [Arguments("namespace XE_Local_AI_Engine.Knowledge\n{\n    internal static class SymbolTools\n    {", "SymbolTools")]
    [Arguments("namespace A.B;\n\npublic sealed record SearchHit(string Id);", "SearchHit")]
    [Arguments("namespace A.B;\n\npublic interface IKnowledgeSearch\n{", "IKnowledgeSearch")]
    [Arguments("namespace A.B;\n\npublic enum ScoreKind\n{", "ScoreKind")]
    [Test]
    public void ExtractPrimary_NamespaceThenType_ReturnsTheType(string content, string expected)
    {
        AssertEx.Equal(expected, KnowledgeCodeSymbolExtractor.ExtractPrimary(content));
    }

    [Test]
    public void ExtractPrimary_NamespaceWithOnlyMethods_ReturnsTheMethod()
    {
        const string content = "namespace A.B;\n\n    public Task<int> CountAsync(CancellationToken token)\n    {";

        AssertEx.Equal("CountAsync", KnowledgeCodeSymbolExtractor.ExtractPrimary(content));
    }

    [Test]
    public void ExtractPrimary_DocstringMentioningType_KeepsTheFunction()
    {
        const string content = "def describe(x):\n    \"\"\"Return the type of x.\"\"\"\n    return type(x)";

        AssertEx.Equal("describe", KnowledgeCodeSymbolExtractor.ExtractPrimary(content));
    }

    [Test]
    public void ExtractPrimary_NamespaceOnly_FallsBackToTheNamespace()
    {
        AssertEx.Equal("Knowledge", KnowledgeCodeSymbolExtractor.ExtractPrimary("// header\nnamespace Knowledge;\n"));
    }

    [Test]
    public void ExtractPrimary_ControlFlow_DoesNotInventSymbol()
    {
        AssertEx.Null(KnowledgeCodeSymbolExtractor.ExtractPrimary("if (results.Count == 0)\n{\n    return;\n}"));
    }
}
