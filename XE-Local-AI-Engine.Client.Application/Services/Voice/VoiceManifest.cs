namespace XE_Local_AI_Engine.Client.Services.Voice;

/// <summary>
///     The composed, config-only voice manifest the node serves to the client AI runtime. It carries no audio — only the
///     feature flag, the allowed downloadable TTS models (with integrity metadata), the selectable voice profiles, the
///     default voice, and the (deferred) remote-fallback config. Composed from <c>StoredNodeSettings</c> + the static
///     <see cref="KokoroVoiceCatalog" />.
/// </summary>
public sealed record VoiceManifest
{
    /// <summary>Node-level master flag. When <see langword="false" /> the client hides all voice UI.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The allowed TTS models (catalog entries intersected with the node allow-list).</summary>
    public required IReadOnlyList<VoiceModel> Models { get; init; }

    /// <summary>The selectable voice profiles (Kokoro English voices in M1; German routes to Web Speech client-side).</summary>
    public required IReadOnlyList<VoiceProfile> Voices { get; init; }

    /// <summary>The default voice profile id the client pre-selects.</summary>
    public required string DefaultVoiceId { get; init; }

    /// <summary>Remote-TTS fallback config. <see langword="null" /> in M1 (remote TTS deferred); kept for forward-compat.</summary>
    public VoiceRemoteFallback? RemoteFallback { get; init; }
}

/// <summary>A downloadable TTS model and the per-dtype files the client can fetch + integrity-check.</summary>
public sealed record VoiceModel
{
    /// <summary>The Hugging Face model id (e.g. <c>onnx-community/Kokoro-82M-v1.0-ONNX</c>).</summary>
    public required string Id { get; init; }

    /// <summary>A human-friendly model name for the UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The primary IETF language short code the model serves (e.g. <c>en</c>).</summary>
    public required string Language { get; init; }

    /// <summary>The model version used as part of the client cache key for eviction on bump.</summary>
    public required string Version { get; init; }

    /// <summary>The downloadable ONNX files, one per supported dtype.</summary>
    public required IReadOnlyList<VoiceModelFile> Files { get; init; }
}

/// <summary>One downloadable ONNX weight file for a given dtype, with integrity + size metadata.</summary>
public sealed record VoiceModelFile
{
    /// <summary>The dtype this file encodes (e.g. <c>fp32</c>, <c>q8</c>).</summary>
    public required string Dtype { get; init; }

    /// <summary>The on-disk ONNX filename (e.g. <c>model.onnx</c>, <c>model_quantized.onnx</c>).</summary>
    public required string File { get; init; }

    /// <summary>The file size in bytes (drives the client download-progress UI).</summary>
    public required long ByteSize { get; init; }

    /// <summary>The expected SHA-256 (HF LFS oid) the client verifies before caching/use. May be empty if unknown.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The fully-resolved download URL for this file.</summary>
    public required string DownloadUrl { get; init; }
}

/// <summary>A selectable TTS voice profile.</summary>
public sealed record VoiceProfile
{
    /// <summary>The Kokoro voice id (e.g. <c>af_heart</c>).</summary>
    public required string Id { get; init; }

    /// <summary>A human-friendly voice name for the UI.</summary>
    public required string Name { get; init; }

    /// <summary>The IETF language short code (<c>en</c> for all Kokoro voices in M1).</summary>
    public required string Language { get; init; }

    /// <summary>The voice gender (<c>female</c> or <c>male</c>), derived from the Kokoro id prefix.</summary>
    public required string Gender { get; init; }
}

/// <summary>Remote-TTS fallback config. Deferred in M1 (always <see langword="null" /> on the manifest); kept for forward-compat.</summary>
public sealed record VoiceRemoteFallback
{
    /// <summary>Whether remote TTS fallback is enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The remote TTS endpoint, when enabled.</summary>
    public string? Endpoint { get; init; }
}
