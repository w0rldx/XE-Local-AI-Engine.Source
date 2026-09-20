namespace XE_Local_AI_Engine.Client.Services.Drafting;

using System.Security.Cryptography;
using System.Text;

/// <summary>
///     The ONE canonical hash over drafted content, which the draft service stamps on
///     <see cref="ConfigDraft.ContentHash" /> and the save path recomputes over what the operator submitted.
/// </summary>
/// <remarks>
///     Both sides must agree byte-for-byte or the provenance <c>wasEdited</c> flag is meaningless. Canonically, each
///     of the three fields is null-to-empty, CRLF and CR folded to LF, then trimmed; the three are joined with
///     <c>U+001F</c>, which cannot occur in submitted text, in the order name, description, content, and SHA-256'd to
///     lowercase hex. The line-ending fold is load-bearing: a browser textarea returns LF content as CRLF, which
///     would otherwise read as an edit. The hash is provenance, NOT a security control, on a single-operator node.
/// </remarks>
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
