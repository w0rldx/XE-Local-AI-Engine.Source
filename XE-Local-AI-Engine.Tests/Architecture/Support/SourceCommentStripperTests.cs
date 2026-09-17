namespace XE_Local_AI_Engine.Tests.Architecture.Support;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The stripper's own check, in the guard-tests-itself shape the rest of <c>Architecture/</c> uses. Every case
///     below is one line of the form <c>&lt;construct&gt;; ForbiddenReference();</c>, so a scanner that mistakes
///     string content for a comment start does not merely mangle the construct — it erases the reference that
///     follows it, which is exactly the silent pass this helper exists to prevent.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SourceCommentStripperTests
{
    /// <summary>
    ///     Constructs whose content LOOKS like a comment. The reference after them must survive intact.
    /// </summary>
    private static readonly (string Case, string Source)[] ReferenceMustSurvive =
    [
        ("regular string holding a URL",
            """var url = "https://localhost"; ForbiddenReference();"""),
        ("verbatim string holding a URL",
            """var url = @"https://localhost"; ForbiddenReference();"""),
        ("verbatim string with an escaped quote",
            """var url = @"say ""https://localhost"" now"; ForbiddenReference();"""),
        ("raw string holding a URL",
            """"var url = """https://localhost"""; ForbiddenReference();""""),
        ("raw string whose content holds a shorter quote run",
            """"var url = """say ""x"" at https://localhost"""; ForbiddenReference();""""),
        ("block-comment opener inside a string",
            """var pattern = "/* not a comment"; ForbiddenReference();"""),
        ("interpolation hole containing the reference",
            """var text = $"{ForbiddenReference()}";"""),
        ("format specifier containing a double slash",
            """var text = $"{value://not-a-comment}"; ForbiddenReference();"""),
        ("nested string inside an interpolation hole",
            """var text = $"{SomeCall("//not-a-comment")}"; ForbiddenReference();"""),
        ("double-dollar raw interpolation with a lone brace and a slash pair",
            """"var text = $$"""a {{Inner()}} b { not a hole and // not a comment"""; ForbiddenReference();""""),
        ("char literal holding a quote",
            """var quote = '"'; ForbiddenReference();"""),
        ("char literal holding a slash",
            """var slash = '/'; ForbiddenReference();"""),
        ("two adjacent slash char literals",
            """var a = '/'; var b = '/'; ForbiddenReference();"""),
        ("escaped quote in a regular string",
            """var text = "he said \"https://localhost\""; ForbiddenReference();""")
    ];

    /// <summary>Real comments. The reference inside them must be gone.</summary>
    private static readonly (string Case, string Source)[] ReferenceMustBeStripped =
    [
        ("line comment", "KeepMe(); // ForbiddenReference() is banned here"),
        ("XML doc comment", "/// <summary>Do not call ForbiddenReference().</summary>\nKeepMe();"),
        ("block comment", "/* ForbiddenReference(); */ KeepMe();"),
        ("multi-line block comment", "/*\n ForbiddenReference();\n*/ KeepMe();"),
        ("comment after a string on the same line", """var url = "https://localhost"; KeepMe(); // ForbiddenReference()"""),
        // A hole is the one part of a literal that is real code, so a comment inside it is a real comment.
        ("comment inside an interpolation hole", """var text = $"{Inner(/* ForbiddenReference() */)}"; KeepMe();"""),
        // @ and raw literals cannot co-occur, so the @ decides the form. Reading the leading quote RUN first makes
        // this a raw literal whose closing run never arrives: the rest of the file becomes string content, the
        // comment below is never recognised, and the guard reports prose as a real reference.
        ("comment after a verbatim string whose content opens with an escaped quote",
            """"var url = @"""quoted"" text"; KeepMe(); // ForbiddenReference()""""),
        // A lone apostrophe in text that is not code opens a char literal that never closes. It must end at the
        // newline, the way an unterminated "…" already does, or every line after it is read as its content.
        ("comment after an unterminated char literal", "#warning don't do that\nKeepMe(); // ForbiddenReference()")
    ];

    [Test]
    public void TheStripper_KeepsStringContentOpaqueAndOnlyStripsRealComments()
    {
        foreach (var (name, source) in ReferenceMustSurvive)
        {
            var stripped = SourceCommentStripper.StripComments(source);

            AssertEx.Contains(stripped, "ForbiddenReference",
                message: $"The '{name}' case lost its reference. A comment delimiter inside a literal was treated as a "
                         + $"real comment, which is how a scan silently stops seeing code: {stripped}");
        }

        foreach (var (name, source) in ReferenceMustBeStripped)
        {
            var stripped = SourceCommentStripper.StripComments(source);

            AssertEx.False(stripped.Contains("ForbiddenReference", StringComparison.Ordinal),
                $"The '{name}' case kept its reference. Prose naming a banned symbol documents the rule rather than "
                + $"breaking it, and must not trip a guard: {stripped}");
            AssertEx.Contains(stripped, "KeepMe", message: $"The '{name}' case ate the code around the comment: {stripped}");
        }
    }

    [Test]
    public void TheStripper_ReplacesEachCommentWithASingleSpace()
    {
        // The replacement is one space, not an empty string: the scans that call this join a stripped line back
        // together, and two identifiers either side of a removed comment must not fuse into one token.
        AssertEx.Equal("a  \nb", SourceCommentStripper.StripComments("a // c\nb"));
        AssertEx.Equal("a b", SourceCommentStripper.StripComments("a/* c */b"));
        AssertEx.Equal("a ", SourceCommentStripper.StripComments("a/* unterminated"));
        AssertEx.Equal(string.Empty, SourceCommentStripper.StripComments(string.Empty));
    }

    [Test]
    public void TheStripper_LeavesRealCodeByteForByte()
    {
        const string Source = """
                              namespace Sample;

                              internal sealed class Thing(IStore store)
                              {
                                  public string Route => $"/{Prefix}/{Suffix}";

                                  public void Configure() => Get(Routes.Thing.List);
                              }
                              """;

        // Non-vacuity for the two tests above: if the scanner ever started dropping ordinary code, every "was the
        // reference stripped" assertion would keep passing for the wrong reason.
        AssertEx.Equal(Source, SourceCommentStripper.StripComments(Source));
    }
}
