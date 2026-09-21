namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>Coarse size tier of a Whisper weight file. Used for exactly one decision: the CPU-backend ceiling.</summary>
public enum WhisperModelTier
{
    Tiny = 0,
    Base = 1,
    Small = 2,
    Medium = 3,
    LargeTurbo = 4
}

/// <summary>
///     One row of the static Whisper weight catalogue: its id, its Hugging Face location, the exact byte size and
///     SHA256 the download must match, its tier, and the approximate resident footprint the recommendation sizes
///     against.
/// </summary>
public sealed class WhisperModelEntry
{
    /// <summary>Stable catalogue id, also the directory name the weight is stored under.</summary>
    public required string Id { get; init; }

    /// <summary>Hugging Face repository the file lives in.</summary>
    public required string RepoId { get; init; }

    /// <summary>File name inside <see cref="RepoId" />.</summary>
    public required string FileName { get; init; }

    /// <summary>Exact file size, from the Hugging Face tree API.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA256, from the Hugging Face tree API's <c>lfs.oid</c>.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Coarse size tier; the CPU backend never recommends above <see cref="WhisperModelTier.Small" />.</summary>
    public required WhisperModelTier Tier { get; init; }

    /// <summary>Approximate resident VRAM on a GPU backend.</summary>
    public required long ApproximateVramBytes { get; init; }

    /// <summary>Approximate resident RAM on a CPU backend.</summary>
    public required long ApproximateRamBytes { get; init; }

    /// <summary>Whether the weights are English-only (every V1 row is multilingual).</summary>
    public required bool EnglishOnly { get; init; }
}

/// <summary>
///     The static Whisper weight catalogue plus the Silero VAD file every launch needs.
/// </summary>
/// <remarks>
///     A table, not a service: no network call decides what this node may install, so an offline node still shows the full list and a
///     mistyped id is rejected before anything is downloaded. <b><see cref="Models" /> is ordered ascending by approximate footprint,
///     and that ordering is load-bearing:</b> <see cref="WhisperModelRecommendation.Recommend" /> walks the list and keeps the LAST
///     fitting row, so a re-ordering would silently change every recommendation, and <c>WhisperModelCatalogTests</c> pins it. Every row
///     is multilingual, so <see cref="WhisperModelEntry.EnglishOnly" /> is <see langword="false" /> throughout V1.
/// </remarks>
public static class WhisperModelCatalog
{
    /// <summary>Hugging Face repository holding the pinned Silero VAD weights.</summary>
    public const string VadRepoId = "ggml-org/whisper-vad";

    /// <summary>
    ///     The pinned Silero VAD file. Deliberately v6.2.0: a box may already carry v5.1.2 from an unrelated build, and
    ///     a live round or a recorded fixture taken under a different VAD model is not comparable with one taken under
    ///     this one.
    /// </summary>
    public const string VadFileName = "ggml-silero-v6.2.0.bin";

    /// <summary>Exact byte size of <see cref="VadFileName" />.</summary>
    public const long VadSizeBytes = 885_098;

    /// <summary>Lowercase hex SHA256 of <see cref="VadFileName" />.</summary>
    public const string VadSha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";

    /// <summary>The catalogue id used when nothing has been selected and no recommendation is available.</summary>
    public const string DefaultModelId = "base";

    private const long Megabyte = 1024L * 1024L;
    private const long Gigabyte = 1024L * 1024L * 1024L;

    private const string WhisperRepoId = "ggerganov/whisper.cpp";

    /// <summary>
    ///     The V1 weight rows, ordered ascending by approximate footprint. See the type remarks: the ordering is an
    ///     invariant, not a presentation choice.
    /// </summary>
    /// <remarks>
    ///     ponytail: only <c>base</c> — an upper bound measured with a second server resident — and
    ///     <c>large-v3-turbo-q8_0</c> are measured figures; the rest are interpolated from file size. Re-measure both in
    ///     isolation during a live round and correct the table from that run, not from a second guess.
    /// </remarks>
    public static IReadOnlyList<WhisperModelEntry> Models { get; } =
    [
        new()
        {
            Id = "tiny",
            RepoId = WhisperRepoId,
            FileName = "ggml-tiny.bin",
            SizeBytes = 77_691_713,
            Sha256 = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
            Tier = WhisperModelTier.Tiny,
            ApproximateVramBytes = 400 * Megabyte,
            ApproximateRamBytes = 400 * Megabyte,
            EnglishOnly = false
        },
        new()
        {
            Id = "base",
            RepoId = WhisperRepoId,
            FileName = "ggml-base.bin",
            SizeBytes = 147_951_465,
            Sha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            Tier = WhisperModelTier.Base,
            ApproximateVramBytes = 800 * Megabyte,
            ApproximateRamBytes = 550 * Megabyte,
            EnglishOnly = false
        },
        new()
        {
            Id = "small",
            RepoId = WhisperRepoId,
            FileName = "ggml-small.bin",
            SizeBytes = 487_601_967,
            Sha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
            Tier = WhisperModelTier.Small,
            ApproximateVramBytes = 1_288_490_189,
            ApproximateRamBytes = Gigabyte,
            EnglishOnly = false
        },
        new()
        {
            Id = "medium-q5_0",
            RepoId = WhisperRepoId,
            FileName = "ggml-medium-q5_0.bin",
            SizeBytes = 539_212_467,
            Sha256 = "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f",
            Tier = WhisperModelTier.Medium,
            ApproximateVramBytes = 1_503_238_554,
            ApproximateRamBytes = 1_181_116_006,
            EnglishOnly = false
        },
        new()
        {
            Id = "large-v3-turbo-q5_0",
            RepoId = WhisperRepoId,
            FileName = "ggml-large-v3-turbo-q5_0.bin",
            SizeBytes = 574_041_195,
            Sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
            Tier = WhisperModelTier.LargeTurbo,
            ApproximateVramBytes = 1_610_612_736,
            ApproximateRamBytes = 1_288_490_189,
            EnglishOnly = false
        },
        new()
        {
            Id = "large-v3-turbo-q8_0",
            RepoId = WhisperRepoId,
            FileName = "ggml-large-v3-turbo-q8_0.bin",
            SizeBytes = 874_188_075,
            Sha256 = "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1",
            Tier = WhisperModelTier.LargeTurbo,
            ApproximateVramBytes = 1_932_735_283,
            ApproximateRamBytes = 1_610_612_736,
            EnglishOnly = false
        },
        new()
        {
            Id = "large-v3-turbo",
            RepoId = WhisperRepoId,
            FileName = "ggml-large-v3-turbo.bin",
            SizeBytes = 1_624_555_275,
            Sha256 = "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
            Tier = WhisperModelTier.LargeTurbo,
            ApproximateVramBytes = 2_791_728_742,
            ApproximateRamBytes = 2_362_232_012,
            EnglishOnly = false
        }
    ];

    /// <summary>Finds a catalogue row by id, or <see langword="null" /> when the id is unknown.</summary>
    public static WhisperModelEntry? Find(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var trimmed = modelId.Trim();
        return Models.FirstOrDefault(entry => string.Equals(entry.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The weight file's path relative to the node's whisper models directory: <c>{Id}/{FileName}</c>.</summary>
    public static string RelativeFilePath(WhisperModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Path.Combine(entry.Id, entry.FileName);
    }
}
