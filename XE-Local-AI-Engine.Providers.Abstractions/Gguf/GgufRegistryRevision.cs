namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Globalization;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Deterministic value-derived revision for a GGUF registry entry.</summary>
public static class GgufRegistryRevision
{
    /// <summary>Computes the V1 token from all stable material registry fields.</summary>
    public static string ComputeV1(GgufModelRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var buffer = new MemoryStream();
        Write("gguf-registry-v1");
        Write(entry.ModelName);
        Write(entry.RepoId);
        Write(entry.FileName);
        Write(entry.Quant);
        Write(NormalizeStoredPath(entry.LocalPath, entry.FileName));
        Write(entry.SizeBytes.ToString(CultureInfo.InvariantCulture));
        WriteNullable(entry.Sha256);
        Write(entry.SourceRevision);
        Write(((int)entry.Role).ToString(CultureInfo.InvariantCulture));
        WriteNullable(entry.ProjectorFileName);
        WriteNullable(entry.ProjectorLocalPath is null ? null : NormalizeStoredPath(entry.ProjectorLocalPath, entry.ProjectorFileName));
        WriteNullable(entry.ProjectorSizeBytes?.ToString(CultureInfo.InvariantCulture));
        WriteNullable(entry.ProjectorSha256);
        WriteNullable(entry.Origin is null ? null : SerializeOrigin(entry.Origin.Value));
        WriteNullable(entry.SourceDisplayName);
        WriteNullable(entry.MetadataSchemaVersion?.ToString(CultureInfo.InvariantCulture));
        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));

        void Write(string value) => GgufModelContentFingerprint.WriteField(buffer, value);
        void WriteNullable(string? value)
        {
            Write(value is null ? "null" : "value");
            if (value is not null)
            {
                Write(value);
            }
        }
    }

    /// <summary>Returns whether a token has the supported exact V1 shape.</summary>
    public static bool IsCanonical(string? value)
    {
        return value is { Length: 67 }
               && value.StartsWith("v1:", StringComparison.Ordinal)
               && GgufMemberFingerprint.IsLowercaseSha256(value[3..]);
    }

    private static string NormalizeStoredPath(string path, string? expectedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathRooted(path))
        {
            return GgufModelContentFingerprint.NormalizeRelativePath(path);
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(path));
        if (string.Equals(parent, "projectors", StringComparison.Ordinal))
        {
            return $"projectors/{Path.GetFileName(path)}";
        }

        if (string.IsNullOrWhiteSpace(expectedFileName)
            || !string.Equals(Path.GetFileName(path), Path.GetFileName(expectedFileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("The stored path cannot be reduced to a contained relative path.", nameof(path));
        }

        return Path.GetFileName(path);
    }

    private static string SerializeOrigin(LocalModelOrigin origin)
    {
        return origin switch
        {
            LocalModelOrigin.HuggingFace => "huggingface",
            LocalModelOrigin.Imported => "imported",
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
    }
}
