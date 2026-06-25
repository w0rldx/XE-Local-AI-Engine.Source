namespace XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Configuration for the Hugging Face GGUF discovery + store. Bound via the Options pattern under the
///     <see cref="SectionName" /> configuration section.
/// </summary>
public sealed class HuggingFaceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HuggingFace";

    /// <summary>Base URL for the Hub REST API (model listing + tree).</summary>
    public string HubBaseUrl { get; set; } = "https://huggingface.co";

    /// <summary>Base URL for file downloads (the <c>/{repo}/resolve/{rev}/{file}</c> surface).</summary>
    public string DownloadBaseUrl { get; set; } = "https://huggingface.co";

    /// <summary>Absolute directory under which downloaded GGUF files and the registry manifest live.</summary>
    public string ModelsDirectory { get; set; } = string.Empty;

    /// <summary>Hard disk-guard safety margin in bytes required on top of the file size before a download starts.</summary>
    public long DiskMarginBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Quant selected when a request does not specify one.</summary>
    public string DefaultQuant { get; set; } = "Q4_K_M";

    /// <summary>Maximum bytes range-requested to read a GGUF header during repo inspection (re-requested if larger).</summary>
    public long HeaderProbeBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Maximum transient-failure retry attempts for a download before surfacing a network failure.</summary>
    public int MaxDownloadRetries { get; set; } = 4;
}
