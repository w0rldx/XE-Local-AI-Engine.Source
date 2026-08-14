namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Globalization;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Deterministic value-derived revision for a GGUF registry entry.</summary>
public static class GgufRegistryRevision
{
    /// <summary>Computes the V1 token from all stable material registry fields.</summary>
    public static string ComputeV1(GgufModelRegistryEntry entry, string modelsDirectory)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        using var buffer = new MemoryStream();
        Write("gguf-registry-v1");
        Write(entry.ModelName);
        Write(entry.RepoId);
        Write(entry.FileName);
        Write(entry.Quant);
        Write(GgufFilePath.GetRelativeContainedPath(modelsDirectory, entry.LocalPath));
        Write(entry.SizeBytes.ToString(CultureInfo.InvariantCulture));
        WriteNullable(entry.Sha256);
        Write(entry.SourceRevision);
        Write(((int)entry.Role).ToString(CultureInfo.InvariantCulture));
        WriteNullable(entry.ProjectorFileName);
        WriteNullable(entry.ProjectorLocalPath is null ? null : GgufFilePath.GetRelativeContainedPath(modelsDirectory, entry.ProjectorLocalPath));
        WriteNullable(entry.ProjectorSizeBytes?.ToString(CultureInfo.InvariantCulture));
        WriteNullable(entry.ProjectorSha256);
        WriteNullable(entry.Origin is null ? null : SerializeOrigin(entry.Origin.Value));
        WriteNullable(entry.SourceDisplayName);
        WriteNullable(entry.MetadataSchemaVersion?.ToString(CultureInfo.InvariantCulture));
        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));

        void Write(string value) =>
            GgufModelContentFingerprint.WriteField(buffer, value);

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
