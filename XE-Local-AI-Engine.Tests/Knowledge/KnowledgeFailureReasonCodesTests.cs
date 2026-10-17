namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Reflection;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Implementation;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class KnowledgeFailureReasonCodesTests
{
    [Test]
    [Arguments(DocumentTextExtractor.UnreadableReason, "unreadable-file")]
    [Arguments("Extraction failed (FileFormatException).", "unreadable-file")]
    [Arguments(KnowledgeIngestionService.TimedOutReason, "timed-out")]
    [Arguments(KnowledgeChunkEmbedder.EmbeddingUnavailableReason, "embedding-unavailable")]
    [Arguments(KnowledgeDocumentCatalogService.InterruptedRepeatedlyReason, "interrupted-repeatedly")]
    public void CodeOf_FixedReason_ReturnsItsTranslatableCode(string reason, string expected)
    {
        AssertEx.Equal(expected, KnowledgeFailureReasonCodes.CodeOf(reason));
    }

    // Every fixed reason a document row can carry: a reworded constant that drops out of the map fails here, not in the UI.
    public static IEnumerable<string> FixedReasons() =>
        new[]
            {
                typeof(KnowledgeIngestionService),
                typeof(KnowledgeChunkEmbedder),
                typeof(KnowledgeEmbeddingVectorPolicy),
                typeof(KnowledgeDocumentCatalogService),
                typeof(DocumentTextExtractor)
            }
            .SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(static field => field is { IsLiteral: true } && field.FieldType == typeof(string) && field.Name.EndsWith("Reason", StringComparison.Ordinal))
            .Select(static field => (string)field.GetRawConstantValue()!);

    [Test]
    public void FixedReasons_AreDiscovered()
    {
        AssertEx.Equal(16, FixedReasons().Count());
    }

    [Test]
    [MethodDataSource(nameof(FixedReasons))]
    public void CodeOf_EveryFixedReasonConstant_HasACode(string reason)
    {
        _ = AssertEx.NotNull(KnowledgeFailureReasonCodes.CodeOf(reason), reason);
    }

    [Test]
    [Arguments(null)]
    [Arguments("Document text exceeds the maximum extractable size.")]
    public void CodeOf_UnmappedReason_HasNoCode(string? reason)
    {
        AssertEx.Null(KnowledgeFailureReasonCodes.CodeOf(reason));
    }
}
