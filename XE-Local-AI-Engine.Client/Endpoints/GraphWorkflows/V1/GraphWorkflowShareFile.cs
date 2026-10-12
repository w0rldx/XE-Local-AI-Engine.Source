namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using System.Globalization;
using System.Text;

/// <summary>The export and import envelope's identity and the download name an export suggests.</summary>
internal static class GraphWorkflowShareFile
{
    public const string Format = "xe-graph-workflow";

    public const int FormatVersion = 1;

    private const string FallbackSlug = "graph-workflow";

    private const int MaxSlugLength = 64;

    /// <summary>The definition name as lowercase ASCII words joined by hyphens, plus the share-file suffix.</summary>
    /// <remarks>
    ///     Accents are folded rather than dropped, so "Übersicht für" reads "ubersicht-fur". The result is safe to
    ///     quote in a <c>Content-Disposition</c> header as is, and a name with no ASCII letter or digit falls back.
    /// </remarks>
    public static string FileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var slug = new StringBuilder(MaxSlugLength);
        foreach (var character in name.Normalize(NormalizationForm.FormD))
        {
            if (slug.Length == MaxSlugLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        return $"{(text.Length == 0 ? FallbackSlug : text)}.graph-workflow.json";
    }
}
