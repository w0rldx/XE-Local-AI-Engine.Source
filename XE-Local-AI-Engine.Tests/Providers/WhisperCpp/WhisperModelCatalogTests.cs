namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Invariants of the static weight catalogue. The ascending-footprint ordering is the load-bearing one: the
///     recommendation walks the list and keeps the last fitting row, so a re-ordering would silently change every
///     recommendation this node makes without failing anything else.
/// </summary>
public sealed class WhisperModelCatalogTests
{
    [Test]
    public void EveryRow_HasA64HexSha256AndAPositiveSize()
    {
        var models = WhisperModelCatalog.Models;

        AssertEx.True(models.Count >= 7,
            $"Expected the seven V1 weight rows; found {models.Count}. The catalogue is broken.");

        foreach (var entry in models)
        {
            AssertEx.NotEmpty(entry.Id);
            AssertEx.NotEmpty(entry.RepoId);
            AssertEx.NotEmpty(entry.FileName);
            AssertEx.True(entry.SizeBytes > 0, $"'{entry.Id}' must declare a positive byte size.");
            AssertEx.Equal(expected: 64, entry.Sha256.Length, $"'{entry.Id}' must carry a full SHA256 digest.");
            AssertEx.True(entry.Sha256.All(Uri.IsHexDigit), $"'{entry.Id}' digest must be hexadecimal.");
            AssertEx.True(entry.ApproximateVramBytes > 0, $"'{entry.Id}' must declare an approximate VRAM footprint.");
            AssertEx.True(entry.ApproximateRamBytes > 0, $"'{entry.Id}' must declare an approximate RAM footprint.");
        }
    }

    [Test]
    public void Rows_AreOrderedAscendingByApproximateFootprint()
    {
        var models = WhisperModelCatalog.Models;

        for (var i = 1; i < models.Count; i++)
        {
            AssertEx.True(models[i - 1].ApproximateVramBytes < models[i].ApproximateVramBytes,
                $"'{models[i].Id}' must declare more approximate VRAM than '{models[i - 1].Id}': the recommendation "
                + "keeps the LAST fitting row, so the ordering decides what it returns.");
            AssertEx.True(models[i - 1].ApproximateRamBytes < models[i].ApproximateRamBytes,
                $"'{models[i].Id}' must declare more approximate RAM than '{models[i - 1].Id}'.");
        }
    }

    [Test]
    public void Ids_AreUniqueAndTheDefaultResolves()
    {
        var models = WhisperModelCatalog.Models;
        var distinctIds = models.Select(static entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        AssertEx.Equal(models.Count, distinctIds, "Two catalogue rows share an id; the id is the storage directory name.");
        AssertEx.NotNull(WhisperModelCatalog.Find(WhisperModelCatalog.DefaultModelId),
            "The default model id must resolve to a catalogue row.");
    }

    [Test]
    public void Find_UnknownId_ReturnsNull()
    {
        AssertEx.Null(WhisperModelCatalog.Find("not-a-model"));
        AssertEx.Null(WhisperModelCatalog.Find(""));
        AssertEx.Null(WhisperModelCatalog.Find(null));
    }

    [Test]
    public void Find_IsCaseInsensitiveAndTrims()
    {
        var entry = AssertEx.NotNull(WhisperModelCatalog.Find("  LARGE-V3-TURBO-Q8_0 "));

        AssertEx.Equal("large-v3-turbo-q8_0", entry.Id);
    }

    [Test]
    public void RelativeFilePath_IsModelIdThenFileName()
    {
        var entry = AssertEx.NotNull(WhisperModelCatalog.Find("base"));

        AssertEx.Equal(Path.Combine("base", "ggml-base.bin"), WhisperModelCatalog.RelativeFilePath(entry));
    }

    [Test]
    public void VadRow_MatchesTheRecordedDigest()
    {
        // Deliberately v6.2.0: a box may already carry v5.1.2 from an unrelated build, and a live round or a recorded
        // fixture taken under a different VAD model is not comparable with one taken under this one.
        AssertEx.Equal("ggml-org/whisper-vad", WhisperModelCatalog.VadRepoId);
        AssertEx.Equal("ggml-silero-v6.2.0.bin", WhisperModelCatalog.VadFileName);
        AssertEx.Equal(expected: 885_098L, WhisperModelCatalog.VadSizeBytes);
        AssertEx.Equal("2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987", WhisperModelCatalog.VadSha256);
        AssertEx.NotEqual("29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf", WhisperModelCatalog.VadSha256);
    }
}
