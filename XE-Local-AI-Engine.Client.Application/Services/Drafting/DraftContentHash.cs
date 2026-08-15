namespace XE_Local_AI_Engine.Client.Services.Drafting;

using System.Security.Cryptography;
using System.Text;

/// <summary>
///     The ONE canonical hash over drafted content. Two call sites must agree byte-for-byte or the provenance
///     <c>wasEdited</c> flag is meaningless:
///     <list type="number">
///         <item>the draft service stamps <see cref="ConfigDraft.ContentHash" /> over the normalized draft it returns;</item>
///         <item>the save path recomputes it over the fields the operator actually submitted and compares.</item>
///     </list>
///     <para>
///         <b>Canonical form</b> — each of the three content fields is taken as <c>null</c> ⇒ empty, CRLF/CR line endings
///         folded to LF, then trimmed of leading/trailing whitespace; the three normalized values are joined with the
///         ASCII unit separator (<c>U+001F</c>, which cannot occur in submitted text) in the fixed order
///         name, description, content; the UTF-8 bytes of that string are SHA-256'd and rendered as lowercase hex.
///         Line-ending folding is load-bearing: a browser textarea round-trips LF content back as CRLF, which would
///         otherwise read as an operator edit.
///     </para>
///     <para>
///         The hash is provenance, NOT a security control — this is a single-operator local node, so an operator can
///         trivially forge it against themselves (locked decision 9: informational provenance, no signed receipts).
///     </para>
/// </summary>
public static class DraftContentHash
{
    private const char FieldSeparator = '\u001F';

    /// <summary>
    ///     Computes the canonical hash for a drafted agent (name / description / instructions) or skill
    ///     (name / description / body). <paramref name="content" /> is whichever of the two the surface uses.
    /// </summary>
    public static string Compute(string? name, string? description, string? content)
    {
        var canonical = new StringBuilder()
                        .Append(Canonicalize(name))
                        .Append(FieldSeparator)
                        .Append(Canonicalize(description))
                        .Append(FieldSeparator)
                        .Append(Canonicalize(content))
                        .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Folds CRLF/CR to LF and trims — the per-field normalization the canonical form is defined over.</summary>
    private static string Canonicalize(string? value)
    {
        return value is null
            ? string.Empty
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    }
}
