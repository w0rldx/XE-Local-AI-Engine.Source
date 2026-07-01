namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Asymmetric embedding models distinguish a stored passage from a search query by an instruction prefix. The prefixer
///     prepends <c>search_document: </c> for ingestion and <c>search_query: </c> for retrieval. These tests pin each
///     prefix, confirm the two intents never bleed into one another, and confirm a single call applies the prefix once.
/// </summary>
public sealed class KnowledgeEmbeddingPrefixerTests
{
    private readonly KnowledgeEmbeddingPrefixer _prefixer = new();

    [Test]
    public void ForDocument_WhenCalled_PrependsTheDocumentIntentPrefix()
    {
        AssertEx.Equal("search_document: hello world", _prefixer.ForDocument("hello world"));
    }

    [Test]
    public void ForQuery_WhenCalled_PrependsTheQueryIntentPrefix()
    {
        AssertEx.Equal("search_query: hello world", _prefixer.ForQuery("hello world"));
    }

    [Test]
    public void ForDocument_WhenCalledOnce_AppliesTheDocumentPrefixExactlyOnce()
    {
        var result = _prefixer.ForDocument("some chunk text");

        var occurrences = CountOccurrences(result, "search_document: ");
        AssertEx.Equal(expected: 1, occurrences);
    }

    [Test]
    public void ForQuery_WhenCalled_DoesNotApplyTheDocumentPrefix()
    {
        var result = _prefixer.ForQuery("some query text");

        AssertEx.False(result.Contains("search_document:", StringComparison.Ordinal),
            "The query prefix must never carry the document intent.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
