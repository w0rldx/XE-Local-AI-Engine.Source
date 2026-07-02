namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     The diffusion-model family an image model belongs to. Governs which weight parts a model needs (single-file
///     for <see cref="Sd15" />; a diffusion + VAE + text-encoder file-set for <see cref="Flux" />/<see cref="Sd3" />).
/// </summary>
public enum ImageModelFamily
{
    /// <summary>Family not yet classified.</summary>
    Unknown = 0,

    /// <summary>Stable Diffusion 1.x — single-file weights, no external VAE/text-encoder parts required.</summary>
    Sd15 = 1,

    /// <summary>Stable Diffusion XL.</summary>
    Sdxl = 2,

    /// <summary>Stable Diffusion 3 / 3.5 — diffusion + VAE + CLIP + T5 file-set.</summary>
    Sd3 = 3,

    /// <summary>FLUX.1 — diffusion + VAE + CLIP-L + T5 file-set.</summary>
    Flux = 4
}

/// <summary>
///     The generation task an image model supports. Step 1 ships <see cref="Txt2Img" /> only; <see cref="Edit" />
///     (FLUX-Kontext / Qwen-Edit) is admitted by the contract for a later phase.
/// </summary>
public enum ImageModelKind
{
    /// <summary>Text-to-image generation.</summary>
    Txt2Img = 0,

    /// <summary>Image editing (edit/inpaint-capable models). Deferred past step 1.</summary>
    Edit = 1
}

/// <summary>
///     The role one weight file plays inside a diffusion model's file-set. A single-file model (SD1.5) is a set of one
///     <see cref="Diffusion" /> part; a FLUX/SD3 model additionally carries <see cref="Vae" /> and text-encoder parts.
/// </summary>
public enum ImageModelPartRole
{
    /// <summary>The core diffusion (UNet/transformer) weights — always present.</summary>
    Diffusion = 0,

    /// <summary>The variational auto-encoder weights.</summary>
    Vae = 1,

    /// <summary>The CLIP-L text encoder.</summary>
    ClipL = 2,

    /// <summary>The CLIP-G text encoder (SDXL/SD3).</summary>
    ClipG = 3,

    /// <summary>The T5-XXL text encoder (FLUX/SD3).</summary>
    T5 = 4
}

/// <summary>
///     One requested weight file inside a model's file-set: which <see cref="Role" /> it fills and the repo-relative
///     file name to download. <see cref="Sha256" /> pins the file when the source exposed an integrity digest.
/// </summary>
public sealed record ImageModelPartRequest
{
    /// <summary>The role this part fills in the file-set.</summary>
    public required ImageModelPartRole Role { get; init; }

    /// <summary>Repo-relative <c>.gguf</c>/<c>.safetensors</c> file name to download for this part.</summary>
    public required string FileName { get; init; }

    /// <summary>Expected sha256 (hash-pin) when the source exposed it; otherwise <see langword="null" />.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>
///     Caller request to ensure a whole image-model file-set is present locally. A model is a <b>set</b> of weight
///     parts — one for SD1.5, several for FLUX/SD3 (diffusion + vae + clip + t5) — so all parts download together and
///     the model is only usable once every part is present.
/// </summary>
public sealed record ImageModelRequest
{
    /// <summary>Registry key — the canonical model name (for example <c>leejet/FLUX.1-schnell-gguf</c>).</summary>
    public required string ModelName { get; init; }

    /// <summary>Hugging Face repository id the parts are pulled from.</summary>
    public required string RepoId { get; init; }

    /// <summary>The diffusion family (drives which parts are expected).</summary>
    public required ImageModelFamily Family { get; init; }

    /// <summary>The generation kind the model supports.</summary>
    public ImageModelKind Kind { get; init; } = ImageModelKind.Txt2Img;

    /// <summary>The file-set to download — at least one <see cref="ImageModelPartRole.Diffusion" /> part.</summary>
    public required IReadOnlyList<ImageModelPartRequest> Parts { get; init; }

    /// <summary>Git revision (commit SHA or branch) to pin; when <see langword="null" />, the default branch is resolved.</summary>
    public string? Revision { get; init; }
}

/// <summary>
///     One present (downloaded-and-verified) weight file of an installed model's file-set: role, source file name,
///     resolved local path, verified sha256 (when exposed) and size.
/// </summary>
public sealed record ImageModelPart
{
    /// <summary>The role this part fills in the file-set.</summary>
    public required ImageModelPartRole Role { get; init; }

    /// <summary>The downloaded source file name.</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute path to the verified local file.</summary>
    public required string LocalPath { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Verified sha256 (hash-pin) when the source exposed it; otherwise <see langword="null" />.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>
///     On-disk registry manifest entry for one present image model (a file-set). The store is the only writer; the
///     registry reads it. Mirrors <c>GgufModelRegistryEntry</c> but models a file <b>set</b>, not a single file.
/// </summary>
public sealed record ImageModelRegistryEntry
{
    /// <summary>Registry key — the canonical model name.</summary>
    public required string ModelName { get; init; }

    /// <summary>Source Hugging Face repository id.</summary>
    public required string RepoId { get; init; }

    /// <summary>The diffusion family.</summary>
    public required ImageModelFamily Family { get; init; }

    /// <summary>The generation kind the model supports.</summary>
    public required ImageModelKind Kind { get; init; }

    /// <summary>The resolved weight parts that make up the model (all present on disk).</summary>
    public required IReadOnlyList<ImageModelPart> Parts { get; init; }

    /// <summary>Total size of every part in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Resolved HF commit SHA the parts were pulled at (revision-pin).</summary>
    public required string SourceRevision { get; init; }

    /// <summary>When the file-set completed downloading and verification.</summary>
    public required DateTimeOffset DownloadedAtUtc { get; init; }
}

/// <summary>
///     A present, resolved image model — the local part paths plus the family/kind the runtime needs to build its
///     launch args. Every <see cref="Parts" /> path is verified present when this handle is returned.
/// </summary>
public sealed record ImageModelHandle(
    string ModelName,
    ImageModelFamily Family,
    ImageModelKind Kind,
    IReadOnlyList<ImageModelPart> Parts);
