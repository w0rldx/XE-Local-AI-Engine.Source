namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

/// <summary>
///     Reduces a client-supplied upload file name to a single safe path segment.
/// </summary>
/// <remarks>
///     The result is kept only as encrypted display metadata — the store generates the actual storage path from the
///     server-assigned file id — but reducing to a leaf here defends the display name against traversal, control and
///     reserved values at the boundary.
/// </remarks>
public static class UploadFileNameSanitizer
{
    /// <summary>
    ///     Returns the safe leaf name, or <see langword="null" /> when the input is empty, a traversal or reserved
    ///     value (<c>.</c> / <c>..</c>), or carries invalid filename or control characters once the directory part is
    ///     stripped.
    /// </summary>
    /// <remarks>Both <c>/</c> and <c>\</c> are treated as separators, so the guard holds cross-platform.</remarks>
    public static string? ToSafeLeafFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var trimmed = fileName.Trim();
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        var leaf = (lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed).Trim();

        if (leaf.Length == 0 || string.Equals(leaf, ".", StringComparison.Ordinal) || string.Equals(leaf, "..", StringComparison.Ordinal))
        {
            return null;
        }

        if (leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || leaf.Any(char.IsControl))
        {
            return null;
        }

        return leaf;
    }
}
