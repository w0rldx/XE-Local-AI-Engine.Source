namespace XE_Local_AI_Engine.Tests.Architecture.Support;

using System.Text;

/// <summary>
///     Removes C# line and block comments from source text so that an architecture scan reads code and not prose.
///     <para>
///         This exists because the obvious implementation is wrong. Replacing <c>//[^\r\n]*</c> with a regex also
///         erases everything after a <c>//</c> that lives INSIDE a string literal — a single
///         <c>"https://localhost"</c> silently swallows the rest of that line, and a banned reference that happened
///         to sit after it on the same line passes a guard that never saw it. The scanner below recognises a comment
///         delimiter only while it is in code, so a string's content can never start a comment.
///     </para>
///     <para>
///         String content stays VISIBLE in the output, deliberately. The house policy the scans in this folder share
///         (see <c>ContainerBridgeLayeringArchitectureTests</c>) is that a banned name inside a string literal should
///         still trip the guard: erring toward scanning less never turns a real reference into a pass. This helper
///         fixes comment recognition only; only <see cref="StripCommentsAndLiterals" /> makes a caller string-blind.
///     </para>
///     <para>
///         Recognised forms: regular <c>"..."</c>, char <c>'...'</c>, verbatim <c>@"..."</c>, raw <c>"""..."""</c>
///         with any delimiter length, and their interpolated variants. An interpolation hole is the one part of a
///         string that is not opaque — it is real code, so the scanner recurses into it and strips comments there
///         too — up to the first top-level <c>:</c>, after which the rest of the hole is a format specifier and is
///         copied through untouched (<c>$"{value://x}"</c> contains no comment).
///     </para>
/// </summary>
internal static class SourceCommentStripper
{
    /// <summary>
    ///     Returns <paramref name="source" /> with every real comment replaced by a single space. Everything else,
    ///     including all string and char literal content, is copied through byte for byte.
    /// </summary>
    internal static string StripComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var output = new StringBuilder(source.Length);
        new Scanner(source, output, blankLiterals: false).Run();
        return output.ToString();
    }

    /// <summary>
    ///     Returns <paramref name="source" /> with every comment stripped and every string or char literal blanked to
    ///     spaces, delimiters and interpolation holes included, keeping line breaks so reported line numbers still hold.
    /// </summary>
    /// <remarks>
    ///     For a scan that reads DECLARATIONS rather than references: a C# snippet quoted as test data is not a
    ///     declaration, and the house default of leaving literal content visible would report it as one.
    /// </remarks>
    internal static string StripCommentsAndLiterals(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var output = new StringBuilder(source.Length);
        new Scanner(source, output, blankLiterals: true).Run();
        return output.ToString();
    }

    /// <summary>
    ///     Returns every real comment's span, in source order, for a caller that reads the comments rather than the
    ///     code around them — the same recognition <see cref="StripComments" /> uses, reported instead of discarded.
    /// </summary>
    internal static IReadOnlyList<Range> CommentSpans(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var spans = new List<Range>();
        new Scanner(source, new StringBuilder(source.Length), blankLiterals: false, spans).Run();
        return spans;
    }

    /// <summary>
    ///     A single left-to-right pass. Every position belongs to exactly one state: code, a line comment, a block
    ///     comment, the content of a string or char literal, or the format text of an interpolation hole. Comment
    ///     delimiters are recognised in the code state only.
    /// </summary>
    private sealed class Scanner
    {
        private readonly string _text;
        private readonly StringBuilder _output;
        private readonly bool _blankLiterals;
        private readonly List<Range>? _spans;
        private int index;

        public Scanner(string text, StringBuilder output, bool blankLiterals, List<Range>? spans = null)
        {
            _text = text;
            _output = output;
            _blankLiterals = blankLiterals;
            _spans = spans;
        }

        internal void Run()
        {
            ScanCode(holeBraces: 0);
        }

        /// <summary>
        ///     Copies code, stripping comments and stepping over literals. With <paramref name="holeBraces" /> above
        ///     zero the scan is inside an interpolation hole opened by that many braces: it returns with
        ///     <see cref="index" /> parked on the closing brace run (the caller emits it), and a top-level <c>:</c>
        ///     hands the rest of the hole to the format-text state first.
        /// </summary>
        private void ScanCode(int holeBraces)
        {
            var depth = 0;

            while (index < _text.Length)
            {
                var current = _text[index];

                if (holeBraces > 0 && depth == 0)
                {
                    if (current == '}' && RunLength('}') >= holeBraces)
                    {
                        return;
                    }

                    if (current == ':')
                    {
                        CopyFormatText(holeBraces);
                        return;
                    }
                }

                switch (current)
                {
                    case '/' when Peek(1) == '/':
                        SkipLineComment();
                        continue;
                    case '/' when Peek(1) == '*':
                        SkipBlockComment();
                        continue;
                    case '\'':
                        Blanked(CopyCharLiteral);
                        continue;
                    case '"':
                        Blanked(() => CopyString(dollars: 0, verbatim: false));
                        continue;
                    case '$' or '@' when Blanked(TryCopyPrefixedString):
                        continue;
                    case '(' or '[' or '{':
                        depth++;
                        break;
                    case ')' or ']' or '}':
                        if (depth > 0)
                        {
                            depth--;
                        }

                        break;
                    default:
                        break;
                }

                _output.Append(current);
                index++;
            }
        }

        /// <summary>Runs a literal copy, then overwrites what it appended with spaces when blanking is on.</summary>
        private void Blanked(Action copy)
        {
            _ = Blanked(() =>
            {
                copy();
                return true;
            });
        }

        /// <summary>
        ///     Line breaks survive the blanking so a caller's line numbers still match the file; everything else in the
        ///     literal, delimiters and interpolated code included, becomes a space.
        /// </summary>
        private bool Blanked(Func<bool> copy)
        {
            var from = _output.Length;
            var copied = copy();

            if (!_blankLiterals)
            {
                return copied;
            }

            for (var position = from; position < _output.Length; position++)
            {
                if (_output[position] is not ('\r' or '\n'))
                {
                    _output[position] = ' ';
                }
            }

            return copied;
        }

        /// <summary>
        ///     Handles a <c>$</c>/<c>@</c> prefix run. Returns false when the run is not followed by a quote (a
        ///     verbatim identifier such as <c>@class</c>), leaving the character to be copied as ordinary code.
        /// </summary>
        private bool TryCopyPrefixedString()
        {
            var start = index;
            var dollars = 0;

            while (start < _text.Length && (_text[start] == '$' || _text[start] == '@'))
            {
                if (_text[start] == '$')
                {
                    dollars++;
                }

                start++;
            }

            if (start >= _text.Length || _text[start] != '"')
            {
                return false;
            }

            var verbatim = _text.AsSpan(index, start - index).Contains('@');
            _output.Append(_text, index, start - index);
            index = start;
            CopyString(dollars, verbatim);
            return true;
        }

        private void CopyString(int dollars, bool verbatim)
        {
            var quotes = RunLength('"');

            // Verbatim wins over the quote run: @ and raw literals are mutually exclusive in C#, so @"""a"" b" is a
            // verbatim string whose content opens with an escaped quote, not a raw one. Testing the run first reads
            // it as a raw literal with no closing run and swallows the rest of the file.
            if (!verbatim && quotes >= 3)
            {
                CopyRawString(dollars, quotes);
            }
            else if (verbatim)
            {
                CopyVerbatimString(dollars);
            }
            else
            {
                CopyRegularString(dollars);
            }
        }

        private void CopyRegularString(int dollars)
        {
            Take(1);

            while (index < _text.Length)
            {
                var current = _text[index];

                if (current == '\\')
                {
                    Take(2);
                    continue;
                }

                if (current == '"')
                {
                    Take(1);
                    return;
                }

                // A newline can only mean the literal was never closed; stop rather than consume the whole file.
                if (current is '\r' or '\n')
                {
                    return;
                }

                if (CopyDoubledBraceOrHole(dollars))
                {
                    continue;
                }

                Take(1);
            }
        }

        private void CopyVerbatimString(int dollars)
        {
            Take(1);

            while (index < _text.Length)
            {
                if (_text[index] == '"')
                {
                    // An inner "" is an escaped quote, not the terminator.
                    if (Peek(1) == '"')
                    {
                        Take(2);
                        continue;
                    }

                    Take(1);
                    return;
                }

                if (CopyDoubledBraceOrHole(dollars))
                {
                    continue;
                }

                Take(1);
            }
        }

        /// <summary>
        ///     A raw literal ends at the first run of at least <paramref name="openQuotes" /> quotes; a shorter run
        ///     inside is content. Holes are delimited by runs of exactly <paramref name="dollars" /> braces, so a
        ///     lone <c>{</c> inside a <c>$$"""…"""</c> literal is text, not a hole.
        /// </summary>
        private void CopyRawString(int dollars, int openQuotes)
        {
            Take(openQuotes);

            while (index < _text.Length)
            {
                if (_text[index] == '"')
                {
                    var run = RunLength('"');

                    if (run >= openQuotes)
                    {
                        Take(run);
                        return;
                    }

                    Take(run);
                    continue;
                }

                if (dollars > 0 && _text[index] == '{')
                {
                    var run = RunLength('{');

                    if (run >= dollars)
                    {
                        CopyHole(dollars);
                        continue;
                    }

                    Take(run);
                    continue;
                }

                Take(1);
            }
        }

        /// <summary>
        ///     The non-raw brace rule: <c>{{</c> and <c>}}</c> are escaped literal braces, a single <c>{</c> opens a
        ///     hole. Returns false when the current character is neither, so the caller copies it as content.
        /// </summary>
        private bool CopyDoubledBraceOrHole(int dollars)
        {
            if (dollars == 0)
            {
                return false;
            }

            var current = _text[index];

            if (current is '{' or '}' && Peek(1) == current)
            {
                Take(2);
                return true;
            }

            if (current != '{')
            {
                return false;
            }

            CopyHole(1);
            return true;
        }

        private void CopyHole(int holeBraces)
        {
            Take(holeBraces);
            ScanCode(holeBraces);

            if (index < _text.Length)
            {
                Take(Math.Min(holeBraces, RunLength('}')));
            }
        }

        /// <summary>
        ///     Everything from a hole's top-level <c>:</c> to its closing braces is a format specifier: opaque text,
        ///     never code, so a <c>//</c> in it starts nothing.
        /// </summary>
        private void CopyFormatText(int holeBraces)
        {
            while (index < _text.Length)
            {
                if (_text[index] == '}' && RunLength('}') >= holeBraces)
                {
                    return;
                }

                Take(1);
            }
        }

        private void CopyCharLiteral()
        {
            Take(1);

            while (index < _text.Length)
            {
                var current = _text[index];

                if (current == '\\')
                {
                    Take(2);
                    continue;
                }

                // Same bail-out as CopyRegularString: an apostrophe in text that is not code (a preprocessor line,
                // a disabled region) opens a literal that never closes, and without this the rest of the file is
                // consumed as its content.
                if (current is '\r' or '\n')
                {
                    return;
                }

                Take(1);

                // The content character is consumed before the terminator is looked for, so '"' and '/' close here
                // and never on their own content.
                if (current == '\'')
                {
                    return;
                }
            }
        }

        private void SkipLineComment()
        {
            var start = index;

            while (index < _text.Length && _text[index] is not ('\r' or '\n'))
            {
                index++;
            }

            _spans?.Add(start..index);
            _output.Append(' ');
        }

        private void SkipBlockComment()
        {
            var start = index;
            var end = _text.IndexOf("*/", index + 2, StringComparison.Ordinal);
            index = end < 0 ? _text.Length : end + 2;
            _spans?.Add(start..index);
            _output.Append(' ');
        }

        private int RunLength(char character)
        {
            var end = index;

            while (end < _text.Length && _text[end] == character)
            {
                end++;
            }

            return end - index;
        }

        private char Peek(int offset)
        {
            return index + offset < _text.Length ? _text[index + offset] : '\0';
        }

        private void Take(int count)
        {
            var take = Math.Min(count, _text.Length - index);
            _output.Append(_text, index, take);
            index += take;
        }
    }
}
