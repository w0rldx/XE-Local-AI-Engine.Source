namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>Physical role of an installed GGUF acquisition member.</summary>
public enum InstalledModelPhysicalMemberRole
{
    /// <summary>Primary model weights.</summary>
    Weight = 0,

    /// <summary>Optional multimodal projector.</summary>
    Projector = 1,

    /// <summary>Recovery sidecar; not model content.</summary>
    Sidecar = 2
}

/// <summary>Canonical facts used to compute an aggregate model-content fingerprint.</summary>
public sealed record GgufModelContentMember(
    string RelativePath,
    InstalledModelPhysicalMemberRole Role,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<string> OwningAliases);

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

/// <summary>Canonical aggregate fingerprint across weight and projector members.</summary>
public static class GgufModelContentFingerprint
{
    /// <summary>Computes the V1 aggregate digest over a deterministic, length-prefixed canonical encoding.</summary>
    public static string ComputeV1(IEnumerable<GgufModelContentMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var canonicalMembers = members
                               .Where(static member => member.Role is InstalledModelPhysicalMemberRole.Weight or InstalledModelPhysicalMemberRole.Projector)
                               .Select(Normalize)
                               .OrderBy(static member => member.RelativePath, StringComparer.Ordinal)
                               .ThenBy(static member => member.Role)
                               .ToArray();

        if (canonicalMembers.Length == 0 || canonicalMembers.All(static member => member.Role != InstalledModelPhysicalMemberRole.Weight))
        {
            throw new ArgumentException("At least one weight member is required.", nameof(members));
        }

        using var buffer = new MemoryStream();
        WriteField(buffer, "gguf-model-content-v1");
        foreach (var member in canonicalMembers)
        {
            WriteField(buffer, member.Role == InstalledModelPhysicalMemberRole.Weight ? "weight" : "projector");
            WriteField(buffer, member.RelativePath);
            WriteField(buffer, member.SizeBytes.ToString(CultureInfo.InvariantCulture));
            WriteField(buffer, member.Sha256);
            WriteField(buffer, member.OwningAliases.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var alias in member.OwningAliases)
            {
                WriteField(buffer, alias);
            }
        }

        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static GgufModelContentMember Normalize(GgufModelContentMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Role == InstalledModelPhysicalMemberRole.Sidecar)
        {
            return member;
        }

        if (member.SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(member), "The member size cannot be negative.");
        }

        GgufMemberFingerprint.ValidateHash(member.Sha256);
        var path = NormalizeRelativePath(member.RelativePath);
        var aliases = member.OwningAliases
                            .Select(static alias => alias.Normalize(NormalizationForm.FormC))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(static alias => alias, StringComparer.Ordinal)
                            .ToArray();
        return member with
        {
            RelativePath = path,
            OwningAliases = aliases
        };
    }

    internal static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!GgufFilePath.IsSafeRelativePath(path))
        {
            throw new ArgumentException("The member path must be a contained relative path.", nameof(path));
        }

        return string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormC);
    }

    internal static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        var prefix = Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
        stream.Write(prefix);
        stream.Write(bytes);
        stream.WriteByte((byte)'|');
    }
}
