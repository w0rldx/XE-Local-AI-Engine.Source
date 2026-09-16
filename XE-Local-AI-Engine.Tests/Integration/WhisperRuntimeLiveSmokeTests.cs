namespace XE_Local_AI_Engine.Tests.Integration;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The one test in this slice that spawns a real <c>whisper-server</c> and transcribes real audio. Opt-in and
///     skipped visibly by default: nothing in the ordinary gate may depend on a native binary or a downloaded model
///     being present.
/// </summary>
/// <remarks>
///     <para>
///         Requires <c>XE_WHISPER_LIVE=true</c>, plus a resolvable server binary and the model weights on disk. On the
///         development box that is <c>XE_WHISPERCPP_SERVER_PATH</c> pointing at a local CUDA build with
///         <c>XE_WHISPERCPP_BACKEND=cuda</c>, and <c>XE_WHISPER_MODELS_DIR</c> pointing at a directory laid out the
///         way the node lays its own out — <c>{modelId}/{fileName}</c>.
///     </para>
///     <para>
///         With <c>XE_WHISPER_RECORD=true</c> the raw verbose-JSON payload is written beside the fixture directory.
///         That dump is the input a later slice's golden test replays, which is why this test exists in this slice at
///         all rather than only where the golden test lives.
///     </para>
/// </remarks>
[RunOn(OS.Linux)]
[NotInParallel]
public sealed class WhisperRuntimeLiveSmokeTests
{
    private const string LiveEnvironmentVariable = "XE_WHISPER_LIVE";
    private const string ModelsDirectoryEnvironmentVariable = "XE_WHISPER_MODELS_DIR";
    private const string RecordEnvironmentVariable = "XE_WHISPER_RECORD";

    /// <summary>The committed fixture: 11 s of 16 kHz mono PCM, public domain (a 1961 US Government work).</summary>
    private const string FixtureRelativePath = "Fixtures/Transcription/jfk.wav";

    private const string ExpectedPhrase = "FELLOW AMERICANS";

    private static readonly JsonSerializerOptions RecordingOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Test]
    public void Fixture_IsTheCommittedSixteenKilohertzMonoClip()
    {
        // Runs in the ORDINARY gate, unlike the live case below. It is what stops the fixture being replaced,
        // resampled or re-encoded without anyone noticing — every later slice's golden data is derived from these
        // exact bytes.
        var path = FixturePath();

        AssertEx.True(File.Exists(path), $"The shared audio fixture must be copied to the output directory ({path}).");

        var bytes = File.ReadAllBytes(path);
        AssertEx.Equal(expected: 352_078, bytes.Length);
        AssertEx.Equal("59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e",
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            "The fixture's digest changed: every recorded golden response in this feature is derived from these bytes.");

        // RIFF/WAVE header: 16 kHz, mono, 16-bit PCM. Asserted from the bytes rather than trusted from the file name.
        AssertEx.Equal("RIFF", Encoding.ASCII.GetString(bytes, index: 0, count: 4));
        AssertEx.Equal("WAVE", Encoding.ASCII.GetString(bytes, index: 8, count: 4));
        AssertEx.Equal(expected: 1, BitConverter.ToInt16(bytes, startIndex: 22), "The clip must be mono.");
        AssertEx.Equal(expected: 16_000, BitConverter.ToInt32(bytes, startIndex: 24), "The clip must be 16 kHz.");
        AssertEx.Equal(expected: 16, BitConverter.ToInt16(bytes, startIndex: 34), "The clip must be 16-bit PCM.");
    }

    [Test]
    public async Task LiveRuntime_TranscribesTheFixture()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip.Test($"Set {LiveEnvironmentVariable}=true (plus {WhisperServerRuntimeOverrideOptions.ServerPathEnvironmentVariable} "
                      + $"and {ModelsDirectoryEnvironmentVariable}) to run the whisper runtime smoke.");
        }

        var modelsDirectory = Environment.GetEnvironmentVariable(ModelsDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            Skip.Test($"Set {ModelsDirectoryEnvironmentVariable} to a directory laid out as {{modelId}}/{{fileName}}.");
        }

        var overrideOptions = WhisperServerRuntimeOverrideOptions.FromEnvironment();
        if (!overrideOptions.IsActive)
        {
            Skip.Test($"Set {WhisperServerRuntimeOverrideOptions.ServerPathEnvironmentVariable} to a built whisper-server.");
        }

        var modelId = WhisperModelCatalog.DefaultModelId;
        var entry = AssertEx.NotNull(WhisperModelCatalog.Find(modelId));
        var modelPath = Path.Combine(modelsDirectory, WhisperModelCatalog.RelativeFilePath(entry));
        if (!File.Exists(modelPath))
        {
            Skip.Test($"The '{modelId}' weights are not present under {ModelsDirectoryEnvironmentVariable}.");
        }

        var options = new WhisperRuntimeOptions
        {
            ModelsDirectory = modelsDirectory,
            VadModelPath = ResolveVadModelPath(modelsDirectory),
            IdleTimeToLive = TimeSpan.FromMinutes(5)
        };

        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var binaryManager = new WhisperCppBinaryManager(httpClient, cacheRoot: null, activeTag: null, overrideOptions);
        var launcher = new WhisperServerProcessLauncher(NullLogger<WhisperServerProcessLauncher>.Instance);
        var readinessProbe = new WhisperServerReadinessProbe(httpClient, options);
        var backendSelector = new StaticBackendSelector(overrideOptions.Backend);

        // The real supervisor over the real launcher: this is the only place in the slice where a whisper-server child
        // process actually starts, and DisposeAsync is what tree-kills it.
        await using var supervisor = new WhisperServerProcessSupervisor(backendSelector,
            binaryManager,
            launcher,
            readinessProbe,
            httpClient,
            options,
            TimeProvider.System,
            NullLogger<WhisperServerProcessSupervisor>.Instance);

        var transcriber = new WhisperServerTranscriber(supervisor, httpClient, options, NullLogger<WhisperServerTranscriber>.Instance);

        await using var audio = new FileStream(FixturePath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await transcriber.TranscribeAsync(modelId, new WhisperTranscriptionRequest
        {
            Audio = audio,
            ContentType = "audio/wav",
            LanguageMode = WhisperLanguageMode.Auto,
            UseVoiceActivityDetection = true,
            DetectLanguage = true
        }, CancellationToken.None);

        if (string.Equals(Environment.GetEnvironmentVariable(RecordEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            await RecordAsync(result);
        }

        AssertEx.Contains(Normalize(result.Text), ExpectedPhrase, StringComparison.Ordinal,
            $"The transcript did not contain '{ExpectedPhrase}'. Got: {result.Text}");
        AssertEx.NotEmpty(result.Segments);
        AssertEx.True(result.DurationSeconds > 0, "The runtime must report the duration it processed.");

        var previousEnd = -1d;
        foreach (var segment in result.Segments)
        {
            AssertEx.True(segment.StartSeconds >= 0, "A segment start must not be negative.");
            AssertEx.True(segment.EndSeconds >= segment.StartSeconds, "A segment must not end before it starts.");
            AssertEx.True(segment.StartSeconds >= previousEnd - 0.001,
                "Segments must be returned in ascending, non-overlapping order.");
            previousEnd = segment.EndSeconds;
        }
    }

    /// <summary>The whole recorded result, so a later slice's fake transcriber can replay a real payload.</summary>
    private static async Task RecordAsync(WhisperTranscriptionResult result)
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcription", "live-smoke-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination,
            JsonSerializer.Serialize(result, RecordingOptions));
    }

    /// <summary>
    ///     Punctuation and case are stripped, because the assertion is about the words rather than the formatting.
    ///     Upper-cased on purpose: the lower-case form is locale-unsafe for comparison.
    /// </summary>
    private static string Normalize(string text) =>
        new(text.ToUpperInvariant().Where(static character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)).ToArray());

    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, FixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The VAD weights, when they sit beside the models the way the node lays them out.</summary>
    private static string? ResolveVadModelPath(string modelsDirectory)
    {
        var path = Path.Combine(modelsDirectory, "vad", WhisperModelCatalog.VadFileName);
        return File.Exists(path) ? path : null;
    }

    private sealed class StaticBackendSelector(WhisperBackend backend) : IWhisperBackendSelector
    {
        public Task<WhisperBackend> SelectBackendAsync(CancellationToken ct) =>
            Task.FromResult(backend);
    }
}
