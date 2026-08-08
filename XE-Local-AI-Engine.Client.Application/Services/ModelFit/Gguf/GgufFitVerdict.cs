namespace XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

/// <summary>
///     How a GGUF file's on-disk size compares to the host's currently-free GPU VRAM, with a runtime headroom margin for
///     KV cache / overhead the raw file size does not include.
/// </summary>
public enum GgufFitVerdict
{
    /// <summary>Free VRAM could not be probed (no GPU, CPU backend, or unknown) — fit is undeterminable.</summary>
    Unknown = 0,

    /// <summary>The file alone is larger than free VRAM — it will not fit on the GPU.</summary>
    WontFit = 1,

    /// <summary>The file fits in free VRAM but the runtime-headroom margin eats into the remainder — fit is tight.</summary>
    Tight = 2,

    /// <summary>The file plus the runtime-headroom margin fits comfortably in free VRAM.</summary>
    Fits = 3
}
