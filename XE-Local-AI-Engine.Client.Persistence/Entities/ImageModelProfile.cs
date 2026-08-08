namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Per-machine default generation parameters for an image model, mirroring <c>InferenceProfile</c>. Natural key is
///     <c>(MachineKey, ModelName, Backend)</c>. Node-scoped; no encrypted columns.
/// </summary>
internal sealed record class ImageModelProfile
{
    /// <summary>Surrogate identity (PK).</summary>
    public Guid Id { get; set; }

    /// <summary>Local-only stable machine id; never leaves the device. Part of the natural key.</summary>
    public string MachineKey { get; set; } = string.Empty;

    /// <summary>Canonical image-model name. Part of the natural key.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Resolved backend (<c>cuda</c> | <c>vulkan</c> | <c>cpu</c>). Part of the natural key.</summary>
    public string Backend { get; set; } = string.Empty;

    /// <summary>Default sampling steps.</summary>
    public int DefaultSteps { get; set; }

    /// <summary>Default sampler / sampling method.</summary>
    public string DefaultSampler { get; set; } = string.Empty;

    /// <summary>Default classifier-free-guidance scale.</summary>
    public double DefaultCfg { get; set; }

    /// <summary>Default output width in pixels.</summary>
    public int DefaultWidth { get; set; }

    /// <summary>Default output height in pixels.</summary>
    public int DefaultHeight { get; set; }

    /// <summary>Provenance of the defaults.</summary>
    public ImageModelProfileStatus Status { get; set; }

    /// <summary>When the profile was created (unix ms UTC).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>When the profile was last updated (unix ms UTC).</summary>
    public long UpdatedAtUtc { get; set; }
}
