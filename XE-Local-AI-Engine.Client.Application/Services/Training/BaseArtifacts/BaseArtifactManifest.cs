namespace XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     Serializes the two encrypted columns on <c>training_base_artifacts</c> and derives an artifact's directory.
/// </summary>
/// <remarks>
///     There is no path column on the entity by design: the directory is a pure function of the artifact id, and the
///     per-file paths live inside the manifest — the same shape the image lane keeps in its parts list. Deriving the
///     root rather than storing it means a data-directory move cannot leave the table pointing at the old location.
/// </remarks>
internal static class BaseArtifactManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public static string ResolveDirectory(INodeDataDirectory dataDirectory, Guid artifactId)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Path.Combine(dataDirectory.Root, "training", "base", artifactId.ToString());
    }

    public static byte[] SerializeFiles(IReadOnlyList<BaseCheckpointFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var manifest = files.Select(static file => new BaseArtifactFileView(file.Role.ToString(),
                                file.FileName,
                                file.LocalPath,
                                file.SizeBytes,
                                file.Sha256))
                            .ToArray();

        return JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
    }

    public static IReadOnlyList<BaseArtifactFileView> DeserializeFiles(ReadOnlyMemory<byte> filesJson)
    {
        if (filesJson.IsEmpty)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<BaseArtifactFileView[]>(filesJson.Span, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            // A manifest that cannot be read means the artifact's files cannot be trusted to be the ones recorded; an
            // empty list makes the row visibly incomplete instead of silently claiming files it cannot name.
            return [];
        }
    }

    public static byte[] SerializeLicense(BaseArtifactLicenseView license)
    {
        ArgumentNullException.ThrowIfNull(license);
        return JsonSerializer.SerializeToUtf8Bytes(license, SerializerOptions);
    }

    public static BaseArtifactLicenseView? DeserializeLicense(ReadOnlyMemory<byte>? licenseJson)
    {
        if (licenseJson is not { } json || json.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BaseArtifactLicenseView>(json.Span, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
