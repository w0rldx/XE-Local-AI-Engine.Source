namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The C-quote decoder the patch parser reads every quoted path through, table-tested against the grammar
///     <c>git</c> 2.53 was measured to emit and accept.
/// </summary>
/// <remarks>
///     The refusals matter as much as the decodings: git answers a literal its own <c>unquote_c_style</c> rejects
///     by taking the raw text, quotes included, as the name. Anything decoded here that git would refuse would
///     therefore validate one path and write another, so the decoder is deliberately no more permissive than git.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class GitQuotedPathTests
{
    [Test]
    [Arguments("a name needing no escape", "\"plain.txt\"", "plain.txt")]
    [Arguments("an escaped backslash", "\"x\\\\y.txt\"", "x\\y.txt")]
    [Arguments("an escaped quote", "\"x\\\"y.txt\"", "x\"y.txt")]
    [Arguments("a bell", "\"x\\ay.txt\"", "x\u0007y.txt")]
    [Arguments("a backspace", "\"x\\by.txt\"", "x\by.txt")]
    [Arguments("a form feed", "\"x\\fy.txt\"", "x\fy.txt")]
    [Arguments("a newline", "\"x\\ny.txt\"", "x\ny.txt")]
    [Arguments("a carriage return", "\"x\\ry.txt\"", "x\ry.txt")]
    [Arguments("a tab", "\"x\\ty.txt\"", "x\ty.txt")]
    [Arguments("a vertical tab", "\"x\\vy.txt\"", "x\vy.txt")]
    [Arguments("an octal ASCII byte", "\"x\\056y.txt\"", "x.y.txt")]
    [Arguments("an octal NUL", "\"x\\000y.txt\"", "x\0y.txt")]
    [Arguments("a two-byte sequence in octal", "\"caf\\303\\251.txt\"", "café.txt")]
    [Arguments("a four-byte sequence in octal", "\"\\360\\237\\230\\200.txt\"", "\U0001F600.txt")]
    [Arguments("a raw code point beside an escape", "\"caf\\\"é.txt\"", "caf\"é.txt")]
    [Arguments("only escapes", "\"\\\\\\\"\"", "\\\"")]
    public void TryDecode_ReadsTheGrammarGitEmits(string shape, string quoted, string expected)
    {
        var decoded = GitQuotedPath.TryDecode(quoted);

        AssertEx.Equal(expected, decoded, $"{shape}: {quoted}");
    }

    [Test]
    [Arguments("an unquoted path", "plain.txt")]
    [Arguments("a lone quote", "\"")]
    [Arguments("no closing quote", "\"x.txt")]
    [Arguments("a dangling backslash", "\"x.txt\\")]
    [Arguments("an escaped closing quote and nothing after it", "\"x\\\"")]
    [Arguments("text after the closing quote", "\"x\"y.txt\"")]
    [Arguments("an unescaped interior quote", "\"x\"y\"")]
    [Arguments("a one-digit octal escape", "\"x\\6y.txt\"")]
    [Arguments("a two-digit octal escape", "\"x\\56y.txt\"")]
    [Arguments("an octal escape over one byte", "\"x\\400y.txt\"")]
    [Arguments("an octal escape running off the end", "\"x\\05\"")]
    [Arguments("a hex escape, which git has none of", "\"x\\x41y.txt\"")]
    [Arguments("an unknown escape letter", "\"x\\zy.txt\"")]
    [Arguments("an escaped space, which git leaves bare", "\"x\\ y.txt\"")]
    [Arguments("a byte that is not UTF-8 at all", "\"x\\377y.txt\"")]
    [Arguments("a lone continuation byte", "\"\\251.txt\"")]
    [Arguments("a truncated two-byte sequence", "\"caf\\303.txt\"")]
    [Arguments("an overlong encoding", "\"\\300\\256.txt\"")]
    [Arguments("a surrogate half in octal", "\"\\355\\240\\200.txt\"")]
    public void TryDecode_RefusesWhatGitWouldNotRead(string shape, string quoted)
    {
        var decoded = GitQuotedPath.TryDecode(quoted);

        AssertEx.Null(decoded, $"{shape} must not decode: {quoted} -> {decoded}");
    }

    /// <summary>An empty literal decodes to an empty name; the alias split is what refuses it afterwards.</summary>
    [Test]
    public void TryDecode_WithAnEmptyLiteral_DecodesToTheEmptyName()
    {
        AssertEx.Equal(string.Empty, GitQuotedPath.TryDecode("\"\""));
    }

    [Test]
    [Arguments("a bare path", "a/x.txt", false)]
    [Arguments("a quoted path", "\"a/x.txt\"", true)]
    public void IsQuoted_AnswersOnTheLeadingQuoteAlone(string shape, string path, bool expected)
    {
        AssertEx.Equal(expected, GitQuotedPath.IsQuoted(path), shape);
    }

    /// <summary>
    ///     The header carries two independently quoted paths, so the a-side ends at its own closing quote — the
    ///     first one an escape did not spend.
    /// </summary>
    [Test]
    [Arguments("an escaped quote inside the name", "\"a/we\\\"ird.txt\" \"b/we\\\"ird.txt\"", 14)]
    [Arguments("no closing quote at all", "\"a/x.txt", -1)]
    public void FindClosingQuote_StopsAtTheFirstUnescapedQuote(string shape, string header, int expected)
    {
        AssertEx.Equal(expected, GitQuotedPath.FindClosingQuote(header), shape);
    }
}
