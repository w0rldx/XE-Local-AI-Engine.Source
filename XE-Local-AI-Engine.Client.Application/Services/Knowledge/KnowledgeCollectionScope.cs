namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Canonical lightweight collection/project namespace used by every knowledge operation. The default preserves the
///     former node-wide corpus for existing clients while explicit project ids isolate repository and document indexes.
/// </summary>
public static class KnowledgeCollectionScope
{
    public const string DefaultId = "DEFAULT";
    public const int MaxLength = 128;

    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? collectionId)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? DefaultId : value.Trim();
        if (candidate.Length > MaxLength || candidate.Any(static ch => !IsAllowed(ch)))
        {
            collectionId = null;
            return false;
        }

        collectionId = candidate.ToUpperInvariant();
        return true;
    }

    public static string NormalizeOrDefault(string? value)
    {
        return TryNormalize(value, out var normalized) ? normalized : DefaultId;
    }

    private static bool IsAllowed(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.';
    }
}
