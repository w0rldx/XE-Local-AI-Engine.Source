namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="FtsSearch.EscapeMatchQuery" /> turns an untrusted search string into a single safe FTS5 <c>MATCH</c>
///     phrase: the whole string is wrapped in double quotes and every embedded double quote is doubled, so operator
///     characters (<c>- * : ( ) ^</c>) are read as ordinary text and can never inject query syntax or trip a parse error.
/// </summary>
public sealed class FtsSearchEscapeMatchQueryTests
{
    [Test]
    public void EscapeMatchQuery_WhenQueryHoldsOperatorChars_WrapsThemInOneQuotedPhrase()
    {
        var result = FtsSearch.EscapeMatchQuery("foo* -bar:(baz)^2");

        AssertEx.Equal("\"foo* -bar:(baz)^2\"", result);
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
    public void EscapeMatchQuery_WhenQueryHasAnEmbeddedDoubleQuote_DoublesTheQuote()
    {
        var result = FtsSearch.EscapeMatchQuery("foo \"bar");

        AssertEx.Equal("\"foo \"\"bar\"", result);
    }

    [Test]
    public void EscapeMatchQuery_WhenQueryIsBlank_ReturnsAnEmptyQuotedPhrase()
    {
        var result = FtsSearch.EscapeMatchQuery(string.Empty);

        AssertEx.Equal("\"\"", result);
    }
}
