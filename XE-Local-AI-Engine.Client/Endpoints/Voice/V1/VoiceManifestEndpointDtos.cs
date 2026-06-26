namespace XE_Local_AI_Engine.Client.Endpoints.Voice.V1;

using XE_Local_AI_Engine.Client.Services.Voice;

/// <summary>
///     Response for <c>GET api/local/v1/voice/manifest</c>. Config-only: the client AI voice runtime reads this to decide
///     whether voice is enabled, which TTS models it may download (with integrity metadata), which voice profiles it may
///     offer, and the default voice. The backend serves no audio.
/// </summary>
public sealed record VoiceManifestResponse
{
    /// <summary>Node-level master flag. When <see langword="false" /> the client hides all voice UI.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The downloadable TTS models the client is allowed to load.</summary>
    public required IReadOnlyList<VoiceManifestModelDto> Models { get; init; }

    /// <summary>The selectable voice profiles (Kokoro English voices in M1).</summary>
    public required IReadOnlyList<VoiceManifestVoiceDto> Voices { get; init; }

    /// <summary>The default voice profile id the client pre-selects.</summary>
    public required string DefaultVoiceId { get; init; }

    /// <summary>Remote-TTS fallback config. <see langword="null" /> in M1 (remote TTS deferred); kept for forward-compat.</summary>
    public VoiceManifestRemoteFallbackDto? RemoteFallback { get; init; }
}

/// <summary>A downloadable TTS model and its per-dtype files.</summary>
public sealed record VoiceManifestModelDto
{
    /// <summary>The Hugging Face model id.</summary>
    public required string Id { get; init; }

    /// <summary>A human-friendly model name for the UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The primary IETF language short code the model serves (e.g. <c>en</c>).</summary>
    public required string Language { get; init; }

    /// <summary>The model version, used as part of the client cache key for eviction on bump.</summary>
    public required string Version { get; init; }

    /// <summary>The downloadable ONNX files, one per supported dtype.</summary>
    public required IReadOnlyList<VoiceManifestModelFileDto> Files { get; init; }
}

/// <summary>One downloadable ONNX weight file for a given dtype, with integrity + size metadata.</summary>
public sealed record VoiceManifestModelFileDto
{
    /// <summary>The dtype this file encodes (e.g. <c>fp32</c>, <c>q8</c>).</summary>
    public required string Dtype { get; init; }

    /// <summary>The on-disk ONNX filename (e.g. <c>model.onnx</c>, <c>model_quantized.onnx</c>).</summary>
    public required string File { get; init; }

    /// <summary>The file size in bytes (drives the client download-progress UI).</summary>
    public required long ByteSize { get; init; }

    /// <summary>The expected SHA-256 (HF LFS oid) the client verifies before caching/use.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The fully-resolved download URL for this file.</summary>
    public required string DownloadUrl { get; init; }
}

/// <summary>A selectable TTS voice profile.</summary>
public sealed record VoiceManifestVoiceDto
{
    /// <summary>The Kokoro voice id (e.g. <c>af_heart</c>).</summary>
    public required string Id { get; init; }

    /// <summary>A human-friendly voice name for the UI.</summary>
    public required string Name { get; init; }

    /// <summary>The IETF language short code (<c>en</c> for all Kokoro voices in M1).</summary>
    public required string Language { get; init; }

    /// <summary>The voice gender (<c>female</c> or <c>male</c>).</summary>
    public required string Gender { get; init; }
}

/// <summary>Remote-TTS fallback config. Deferred in M1 (always <see langword="null" /> on the manifest); kept for forward-compat.</summary>
public sealed record VoiceManifestRemoteFallbackDto
{
    /// <summary>Whether remote TTS fallback is enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The remote TTS endpoint, when enabled.</summary>
    public string? Endpoint { get; init; }
}

internal static class VoiceManifestEndpointDtoMapper
{
    public static VoiceManifestResponse ToResponse(this VoiceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new VoiceManifestResponse
        {
            Enabled = manifest.Enabled,
            Models = manifest.Models.Select(ToDto).ToList(),
            Voices = manifest.Voices.Select(ToDto).ToList(),
            DefaultVoiceId = manifest.DefaultVoiceId,
            RemoteFallback = manifest.RemoteFallback is null
                ? null
                : new VoiceManifestRemoteFallbackDto
                {
                    Enabled = manifest.RemoteFallback.Enabled,
                    Endpoint = manifest.RemoteFallback.Endpoint
                }
        };
    }

    private static VoiceManifestModelDto ToDto(VoiceModel model)
    {
        return new VoiceManifestModelDto
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            Language = model.Language,
            Version = model.Version,
            Files = model.Files.Select(ToDto).ToList()
        };
    }

    private static VoiceManifestModelFileDto ToDto(VoiceModelFile file)
    {
        return new VoiceManifestModelFileDto
        {
            Dtype = file.Dtype,
            File = file.File,
            ByteSize = file.ByteSize,
            Sha256 = file.Sha256,
            DownloadUrl = file.DownloadUrl
        };
    }

    private static VoiceManifestVoiceDto ToDto(VoiceProfile voice)
    {
        return new VoiceManifestVoiceDto
        {
            Id = voice.Id,
            Name = voice.Name,
            Language = voice.Language,
            Gender = voice.Gender
        };
    }
}
