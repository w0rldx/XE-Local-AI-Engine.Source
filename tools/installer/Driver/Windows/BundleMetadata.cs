namespace XE_Local_AI_Engine.Installer.Driver.Windows;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     The bundle identity/contract file (<c>payload/bundle-metadata.json</c>) describing the Windows
///     install bundle. Field names match the camelCase keys the packaging step emits. The
///     <c>*ScriptSha256</c> values pin the in-distro scripts: the driver recomputes each script's
///     SHA-256 and verifies it against these BEFORE feeding the script to <c>bash -s</c> (mirrors
///     <c>Wsl2Driver.VerifyScriptHash</c>).
/// </summary>
public sealed record BundleMetadata
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("imageTag")]
    public string ImageTag { get; init; } = string.Empty;

    [JsonPropertyName("XE_EXPECTED_IMAGE_ID")]
    public string ExpectedImageId { get; init; } = string.Empty;

    [JsonPropertyName("bootstrapModel")]
    public string BootstrapModel { get; init; } = string.Empty;

    [JsonPropertyName("stageImageScriptSha256")]
    public string StageImageScriptSha256 { get; init; } = string.Empty;

    [JsonPropertyName("loadImageScriptSha256")]
    public string LoadImageScriptSha256 { get; init; } = string.Empty;

    [JsonPropertyName("pullModelScriptSha256")]
    public string PullModelScriptSha256 { get; init; } = string.Empty;

    /// <summary>SHA-256 of the in-distro <c>write-manifest.sh</c> (HIGH-1 manifest delivery). Owned by the packaging lane.</summary>
    [JsonPropertyName("writeManifestScriptSha256")]
    public string WriteManifestScriptSha256 { get; init; } = string.Empty;

    [JsonPropertyName("minimumFreeDiskBytes")]
    public long MinimumFreeDiskBytes { get; init; }

    public static async Task<BundleMetadata> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Bundle is missing bundle-metadata.json.", path);
        }

        await using var stream = File.OpenRead(path);
        var metadata = await JsonSerializer.DeserializeAsync<BundleMetadata>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("bundle-metadata.json deserialized to null.");
        return metadata;
    }
}
