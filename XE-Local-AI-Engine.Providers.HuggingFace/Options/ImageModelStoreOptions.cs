namespace XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Configuration for the Hugging Face image-model file-set store. Kept separate from
///     <see cref="HuggingFaceOptions" /> so image weights live under their own directory, isolated from the GGUF text
///     models.
/// </summary>
public sealed class ImageModelStoreOptions
{
    public const string SectionName = "HuggingFaceImageModels";

    /// <summary>
    ///     Absolute directory under which downloaded image-model weight file-sets and the <c>image-models.json</c>
    ///     manifest live. Defaulted by the DI extension when unset.
    /// </summary>
    public string ModelsDirectory { get; set; } = string.Empty;
}
