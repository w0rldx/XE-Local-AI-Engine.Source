namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Globalization;

/// <summary>Canonical per-file GGUF content fingerprint.</summary>
public static class GgufMemberFingerprint
{
    /// <summary>Computes <c>sha256:&lt;lowercase-hash&gt;:&lt;invariant-size&gt;</c>.</summary>
    public static string Compute(string sha256, long sizeBytes)
    {
        ValidateHash(sha256);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        return $"sha256:{sha256}:{sizeBytes.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Returns whether a persisted fingerprint is exactly canonical.</summary>
    public static bool IsCanonical(string? value)
    {
        if (value is null || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        var lastSeparator = value.LastIndexOf(':');
        if (lastSeparator != 71
            || !long.TryParse(value.AsSpan(lastSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || size < 0)
        {
            return false;
        }

        var hash = value.Substring(startIndex: 7, length: 64);
        return IsLowercaseSha256(hash) && string.Equals(value, Compute(hash, size), StringComparison.Ordinal);
    }

    /// <summary>Returns whether a raw SHA-256 is exactly 64 lowercase hexadecimal characters.</summary>
    public static bool IsCanonicalSha256(string? value) =>
        value is not null && IsLowercaseSha256(value);

    internal static void ValidateHash(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (!IsLowercaseSha256(sha256))
        {
            throw new ArgumentException("The SHA-256 must contain exactly 64 lowercase hexadecimal characters.", nameof(sha256));
        }
    }

    internal static bool IsLowercaseSha256(string value)
    {
        return value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
