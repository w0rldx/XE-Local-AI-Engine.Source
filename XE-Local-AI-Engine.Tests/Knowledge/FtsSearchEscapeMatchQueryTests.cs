namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="FtsSearch.EscapeMatchQuery" /> turns an untrusted search string into a safe FTS5 <c>MATCH</c>
///     expression: it splits on whitespace, wraps each token in its own double-quoted literal (embedded double quotes
///     doubled), and joins the tokens with <c>OR</c>. Per-token quoting keeps operator characters (<c>- * : ( ) ^</c>)
///     and bare keywords (<c>OR AND NEAR</c>) as ordinary text — they can never inject query syntax or trip a parse
///     error — while OR fusion restores multi-word BM25 recall for the RRF pipeline.
/// </summary>
public sealed class FtsSearchEscapeMatchQueryTests
{
    [Test]
    public void EscapeMatchQuery_WhenQueryHasMultipleWords_QuotesEachTokenAndJoinsWithOr()
    {
        var result = FtsSearch.EscapeMatchQuery("embedding model configuration");

        AssertEx.Equal("\"embedding\" OR \"model\" OR \"configuration\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenQueryIsASingleWord_ReturnsOneQuotedToken()
    {
        var result = FtsSearch.EscapeMatchQuery("embedding");

        AssertEx.Equal("\"embedding\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenTokensHoldOperatorChars_QuotesEachTokenLiterally()
    {
        var result = FtsSearch.EscapeMatchQuery("foo* -bar:(baz)^2");

        AssertEx.Equal("\"foo*\" OR \"-bar:(baz)^2\"", result);
    }

    [Test]
    [Arguments("*")]
    [Arguments("-")]
    [Arguments(":")]
    [Arguments("(")]
    [Arguments(")")]
    [Arguments("^")]
    public void EscapeMatchQuery_WhenQueryIsASingleOperatorChar_ReturnsItAsAQuotedLiteral(string op)
    {
        var result = FtsSearch.EscapeMatchQuery(op);

        AssertEx.Equal($"\"{op}\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenTokenHasAnEmbeddedDoubleQuote_DoublesTheQuoteWithinItsToken()
    {
        var result = FtsSearch.EscapeMatchQuery("foo \"bar");

        AssertEx.Equal("\"foo\" OR \"\"\"bar\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenQueryLooksLikeSqlInjection_QuotesEveryTokenSoOperatorsStayLiteral()
    {
        // A bare OR keyword and the injected fragment become quoted literals; only the joiners are real OR operators.
        var result = FtsSearch.EscapeMatchQuery("\" OR 1=1");

        AssertEx.Equal("\"\"\"\" OR \"OR\" OR \"1=1\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenTokenIsAnFtsFunctionCall_TreatsItAsALiteral()
    {
        var result = FtsSearch.EscapeMatchQuery("NEAR(");

        AssertEx.Equal("\"NEAR(\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenQueryIsBlank_ReturnsAnEmptyQuotedPhrase()
    {
        var result = FtsSearch.EscapeMatchQuery(string.Empty);

        AssertEx.Equal("\"\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenQueryIsWhitespaceOnly_ReturnsAnEmptyQuotedPhrase()
    {
        var result = FtsSearch.EscapeMatchQuery("   \t  ");

        AssertEx.Equal("\"\"", result);
    }
}
