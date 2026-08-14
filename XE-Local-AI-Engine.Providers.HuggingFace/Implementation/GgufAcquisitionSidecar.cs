namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal static class GgufAcquisitionSidecar
{
    internal const string Suffix = ".xe-model.json";

    internal static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync(string path, GgufAcquisitionMetadata metadata, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, metadata, SerializerOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        FlushToDisk(stream);
    }

    // FlushAsync drains managed buffers but exposes no flush-to-disk overload. This short synchronous durability
    // boundary runs only after the async flush and before the sidecar is eligible for an atomic commit rename.
    private static void FlushToDisk(FileStream stream) => stream.Flush(flushToDisk: true);

    public static async Task<GgufAcquisitionMetadata?> ReadValidAsync(string sidecarPath,
        string weightPath,
        string modelsDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metadata = await JsonSerializer.DeserializeAsync<GgufAcquisitionMetadata>(stream, SerializerOptions, cancellationToken)
                                               .ConfigureAwait(false);
            if (metadata is null || !ValidateShape(metadata, weightPath, modelsDirectory))
            {
                return null;
            }

            var weightHash = await ComputeSha256Async(weightPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(weightHash, metadata.WeightContentSha256, StringComparison.Ordinal)
                || new FileInfo(weightPath).Length != metadata.WeightSizeBytes
                || !string.Equals(GgufMemberFingerprint.Compute(weightHash, metadata.WeightSizeBytes), metadata.WeightMemberFingerprint,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var members = new List<GgufModelContentMember>
            {
                new(metadata.LocalFileName, InstalledModelPhysicalMemberRole.Weight, metadata.WeightSizeBytes, weightHash, [metadata.ModelName])
            };

            if (metadata.ProjectorRelativePath is not null)
            {
                var projectorPath = GgufFilePath.ResolveContainedPath(modelsDirectory, metadata.ProjectorRelativePath);
                if (!File.Exists(projectorPath)
                    || metadata.ProjectorContentSizeBytes is not { } projectorSize
                    || metadata.ProjectorContentSha256 is not { } projectorExpectedHash
                    || metadata.ProjectorMemberFingerprint is not { } projectorFingerprint)
                {
                    return null;
                }

                var projectorHash = await ComputeSha256Async(projectorPath, cancellationToken).ConfigureAwait(false);
                if (new FileInfo(projectorPath).Length != projectorSize
                    || !string.Equals(projectorHash, projectorExpectedHash, StringComparison.Ordinal)
                    || !string.Equals(GgufMemberFingerprint.Compute(projectorHash, projectorSize), projectorFingerprint, StringComparison.Ordinal))
                {
                    return null;
                }

                members.Add(new GgufModelContentMember(metadata.ProjectorRelativePath,
                    InstalledModelPhysicalMemberRole.Projector,
                    projectorSize,
                    projectorHash,
                    [metadata.ModelName]));
            }

            return string.Equals(GgufModelContentFingerprint.ComputeV1(members), metadata.ModelContentFingerprint, StringComparison.Ordinal)
                ? metadata
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static GgufModelRegistryEntry ToRegistryEntry(GgufAcquisitionMetadata metadata, string weightPath, string modelsDirectory)
    {
        var projectorPath = metadata.ProjectorRelativePath is null
            ? null
            : GgufFilePath.ResolveContainedPath(modelsDirectory, metadata.ProjectorRelativePath);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = metadata.ModelName,
            RepoId = metadata.RegistryRepoId,
            FileName = metadata.LocalFileName,
            Quant = metadata.Quantization,
            LocalPath = weightPath,
            SizeBytes = metadata.WeightSizeBytes,
            Sha256 = metadata.WeightContentSha256,
            SourceRevision = metadata.RegistrySourceRevision,
            DownloadedAtUtc = metadata.AcquiredAtUtc,
            Role = metadata.Role,
            ProjectorFileName = metadata.ProjectorSourceDisplayName,
            ProjectorLocalPath = projectorPath,
            ProjectorSizeBytes = metadata.ProjectorContentSizeBytes,
            ProjectorSha256 = metadata.ProjectorContentSha256,
            Origin = metadata.Origin,
            SourceDisplayName = metadata.SourceDisplayName,
            MetadataSchemaVersion = metadata.SchemaVersion,
            ModelContentFingerprint = metadata.ModelContentFingerprint
        };
        return entry with { RegistryRevision = GgufRegistryRevision.ComputeV1(entry, modelsDirectory) };
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool ValidateShape(GgufAcquisitionMetadata metadata, string weightPath, string modelsDirectory)
    {
        if (metadata.SchemaVersion != GgufAcquisitionMetadata.CurrentSchemaVersion
            || !GgufRegistryRevision.IsCanonical(metadata.RegistryRevision)
            || string.IsNullOrWhiteSpace(metadata.ModelName)
            || Path.GetFileName(metadata.LocalFileName) != metadata.LocalFileName
            || !string.Equals(Path.GetFileName(weightPath), metadata.LocalFileName, StringComparison.Ordinal)
            || !GgufMemberFingerprint.IsCanonical(metadata.WeightMemberFingerprint)
            || !GgufRegistryRevision.IsCanonical(metadata.ModelContentFingerprint)
            || !GgufMemberFingerprint.IsCanonicalSha256(metadata.WeightContentSha256)
            || metadata.WeightSizeBytes < 0
            || metadata.Role == GgufRole.Unknown
            || metadata.SourceDisplayName != Path.GetFileName(metadata.SourceDisplayName)
            || !GgufQuantDetector.IsCanonical(metadata.Quantization))
        {
            return false;
        }

        if (metadata.Origin == LocalModelOrigin.Imported)
        {
            return metadata.RegistryRepoId == metadata.ModelName
                   && metadata.RegistrySourceRevision == $"sha256:{metadata.WeightContentSha256}"
                   && metadata.Role == GgufRole.Chat
                   && metadata.ProjectorRelativePath is null
                   && metadata.ProjectorSourceDisplayName is null
                   && metadata.ProjectorSourceSha256 is null
                   && metadata.ProjectorSourceSizeBytes is null
                   && metadata.ProjectorContentSha256 is null
                   && metadata.ProjectorContentSizeBytes is null
                   && metadata.ProjectorMemberFingerprint is null;
        }

        if (metadata.ProjectorRelativePath is not null)
        {
            _ = GgufFilePath.ResolveContainedPath(modelsDirectory, metadata.ProjectorRelativePath);
            return metadata.ProjectorSourceDisplayName is not null
                   && metadata.ProjectorSourceDisplayName == Path.GetFileName(metadata.ProjectorSourceDisplayName)
                   && metadata.ProjectorContentSha256 is not null
                   && GgufMemberFingerprint.IsCanonicalSha256(metadata.ProjectorContentSha256)
                   && metadata.ProjectorContentSizeBytes is >= 0
                   && (metadata.ProjectorSourceSha256 is null || GgufMemberFingerprint.IsCanonicalSha256(metadata.ProjectorSourceSha256))
                   && metadata.ProjectorSourceSizeBytes is null or >= 0
                   && GgufMemberFingerprint.IsCanonical(metadata.ProjectorMemberFingerprint);
        }

        return metadata.ProjectorSourceDisplayName is null
               && metadata.ProjectorSourceSha256 is null
               && metadata.ProjectorSourceSizeBytes is null
               && metadata.ProjectorContentSha256 is null
               && metadata.ProjectorContentSizeBytes is null
               && metadata.ProjectorMemberFingerprint is null;
    }
}
