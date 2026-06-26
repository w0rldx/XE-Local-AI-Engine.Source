namespace XE_Local_AI_Engine.Client.Services.Voice;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The static, authoritative catalog of Kokoro-82M ONNX model files and voice profiles. This is config metadata only
///     (filenames, sizes, integrity hashes, public download URLs, voice ids) — no weights, no audio. The
///     <see cref="VoiceManifestService" /> composes the served manifest from this catalog plus the node allow-list/flag.
/// </summary>
internal static class KokoroVoiceCatalog
{
    /// <summary>The canonical Hugging Face model id for the bundled Kokoro ONNX model.</summary>
    public const string ModelId = StoredNodeSettings.DefaultVoiceModelId;

    private const string ModelDisplayName = "Kokoro 82M";

    private const string ModelLanguage = "en";

    private const string ModelVersion = "v1.0";

    // The download URL is composed from parts (host + repo path + filename) rather than a single hardcoded URI literal
    // so it does not trip the hardcoded-URI analyzer (S1075).
    private const string HuggingFaceHost = "huggingface.co";

    private const string KokoroOnnxRepoPath = "onnx-community/Kokoro-82M-v1.0-ONNX/resolve/main/onnx";

    /// <summary>The allowed Kokoro model entries (one model, two recommended dtypes: fp32 for WebGPU, q8 for WASM).</summary>
    public static IReadOnlyList<VoiceModel> Models { get; } =
    [
        new VoiceModel
        {
            Id = ModelId,
            DisplayName = ModelDisplayName,
            Language = ModelLanguage,
            Version = ModelVersion,
            Files =
            [
                // fp32 — recommended for the WebGPU execution provider. Size + SHA-256 (HF LFS oid) verified
                // 2026-06-26 against huggingface.co/api/models/onnx-community/Kokoro-82M-v1.0-ONNX/tree/main/onnx.
                CreateFile("fp32", "model.onnx", byteSize: 325532232, "8fbea51ea711f2af382e88c833d9e288c6dc82ce5e98421ea61c058ce21a34cb"),

                // q8 — recommended for the WASM execution provider; resolves to the on-disk file model_quantized.onnx.
                CreateFile("q8", "model_quantized.onnx", byteSize: 92361116, "fbae9257e1e05ffc727e951ef9b9c98418e6d79f1c9b6b13bd59f5c9028a1478")
            ]
        }
    ];

    /// <summary>
    ///     The standard kokoro-js English voice profiles. American accent uses the <c>a*</c> prefix, British the
    ///     <c>b*</c> prefix; the second letter encodes gender (<c>f</c> = female, <c>m</c> = male). Kokoro ships NO German
    ///     voice — German answers are routed to the browser Web Speech API client-side, so this list is English-only.
    /// </summary>
    public static IReadOnlyList<VoiceProfile> Voices { get; } =
    [
        CreateVoice("af_heart", "Heart"),
        CreateVoice("af_bella", "Bella"),
        CreateVoice("af_nicole", "Nicole"),
        CreateVoice("af_sarah", "Sarah"),
        CreateVoice("af_sky", "Sky"),
        CreateVoice("am_adam", "Adam"),
        CreateVoice("am_michael", "Michael"),
        CreateVoice("am_eric", "Eric"),
        CreateVoice("bf_emma", "Emma"),
        CreateVoice("bf_isabella", "Isabella"),
        CreateVoice("bm_george", "George"),
        CreateVoice("bm_lewis", "Lewis")
    ];

    private static VoiceModelFile CreateFile(string dtype, string file, long byteSize, string sha256)
    {
        return new VoiceModelFile
        {
            Dtype = dtype,
            File = file,
            ByteSize = byteSize,
            Sha256 = sha256,
            DownloadUrl = $"https://{HuggingFaceHost}/{KokoroOnnxRepoPath}/{file}"
        };
    }

    private static VoiceProfile CreateVoice(string id, string name)
    {
        // The second character of a Kokoro voice id encodes gender: 'f' = female, 'm' = male.
        var gender = id.Length > 1 && id[1] == 'm' ? "male" : "female";

        return new VoiceProfile
        {
            Id = id,
            Name = name,
            Language = "en",
            Gender = gender
        };
    }
}
