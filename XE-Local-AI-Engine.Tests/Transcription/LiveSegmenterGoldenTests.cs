namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Live;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pushes the <c>jfk.wav</c> clip through the real segmenter against a recorded run of the real runtime, and
///     asserts that what it commits is the transcript one whole-clip request produces.
/// </summary>
/// <remarks>
///     <para>
///         <b>Read this before you touch the segmenter.</b> The fixture was recorded by driving this same segmenter,
///         so its window list is a function of the segmenter's own behaviour. Any change to the tick loop, the window
///         bounds, the commit rule or the force-commit rule invalidates it, and the replay fails loudly rather than
///         quietly adapting.
///     </para>
///     <para>
///         That makes this a faithful change detector and nothing more. A red here <em>after a segmenter change</em>
///         means "re-record against a live server and read the new single-shot comparison yourself", not "the change
///         is wrong". A red here after <em>no</em> segmenter change is a real regression. The re-record is
///         <see cref="WhisperGoldenFixtureRecorder" />, which is opt-in and needs a running daemon on the pinned VAD
///         file.
///     </para>
///     <para>
///         <b>Why the transcript comparison is bounded rather than exact.</b> A window is cut at a fixed boundary and
///         nothing of the previous window is carried into the next one, so a word straddling a committed boundary is
///         the model's to guess from a fragment. Three recordings of this clip show the same ceiling with different
///         damage each time: <c>base</c> at a 5 s cap inserts "to" into "ask not what", from the forced window that
///         cuts that phrase in half; <c>large-v3-turbo</c> at the same cap duplicates "you" across its 7.1 s boundary;
///         <c>base</c> at a 10 s cap drops "do" at the 6.75 s one. Exact equality with the whole-clip transcript is
///         therefore not attainable at any allowed window size, and asserting it would only ever record which model
///         was pinned last.
///     </para>
///     <para>
///         So the bound is one word per <em>forced</em> boundary: the word-level edit distance between the committed
///         transcript and the whole-clip transcript may not exceed the number of at-cap windows in the fixture. That
///         still fails on a dropped or duplicated segment, on a de-duplication regression, and on a second word lost
///         at any one boundary — it tolerates only the fragment guess the design knowingly accepts.
///     </para>
/// </remarks>
public sealed class LiveSegmenterGoldenTests
{
    private const string FixtureFileName = "jfk-golden-base.json";

    [Test]
    public async Task JfkPushedInTwoSecondChunks_CommitsTheSingleShotTranscriptWithinOneWordPerForcedBoundary()
    {
        var (transcriber, commits) = await ReplayAsync().ConfigureAwait(false);

        var committed = Normalize(string.Join(' ', commits.Select(commit => commit.Text)));
        var singleShot = Normalize(transcriber.SingleShotText);
        var forcedBoundaries = ForcedBoundaryCount(transcriber.Fixture);
        var distance = WordEditDistance(Words(singleShot), Words(committed));

        AssertEx.NotEmpty(committed, "Chunked live transcription of a clip of speech must commit something.");
        AssertEx.True(forcedBoundaries > 0, "The clip is long enough to force at least one commit at the cap, or this bound proves nothing.");
        AssertEx.True(distance <= forcedBoundaries,
            $"The committed live transcript is {distance.ToString(CultureInfo.InvariantCulture)} words from the whole-clip "
            + $"transcript, and only {forcedBoundaries.ToString(CultureInfo.InvariantCulture)} forced boundary "
            + $"{(forcedBoundaries == 1 ? "is" : "are")} tolerated."
            + $"{Environment.NewLine}  whole clip: {singleShot}{Environment.NewLine}  committed : {committed}");
    }

    [Test]
    public void FixtureWasRecordedUnderThePinnedVadModel()
    {
        var fixture = RecordedWhisperTranscriber.ReadFixture(RecordedWhisperTranscriber.FixturePath(FixtureFileName));

        AssertEx.Equal(WhisperModelCatalog.VadSha256,
            fixture.VadSha256,
            "Re-record under the pinned VAD file: speech boundaries differ between Silero versions, and the replay would "
            + "otherwise assert against a runtime configuration the product never launches with.");
        AssertEx.Equal(WhisperModelCatalog.VadFileName, fixture.VadModel, "The fixture names the pinned VAD file.");
    }

    [Test]
    public async Task CommittedSegmentsNeverRepeatTheTrailingWordsOfThePrevious()
    {
        var (_, commits) = await ReplayAsync().ConfigureAwait(false);

        AssertEx.True(commits.Count > 1, "The clip is long enough to commit more than one segment, or this proves nothing.");

        for (var index = 1; index < commits.Count; index++)
        {
            var previous = Words(commits[index - 1].Text);
            var current = Words(commits[index].Text);
            var overlap = Math.Min(3, Math.Min(previous.Length, current.Length));

            AssertEx.False(overlap > 0 && previous.AsSpan(previous.Length - overlap).SequenceEqual(current.AsSpan(0, overlap)),
                $"Segment {index} opens with the same {overlap} words segment {index - 1} ended on: "
                + $"'{commits[index - 1].Text}' then '{commits[index].Text}'. The watermark did not de-duplicate the window overlap.");
        }
    }

    [Test]
    public async Task CommittedSpanMatchesTheClipDurationWithinOneSecond()
    {
        var fixture = RecordedWhisperTranscriber.ReadFixture(RecordedWhisperTranscriber.FixturePath(FixtureFileName));
        var (_, commits) = await ReplayAsync().ConfigureAwait(false);

        var clipMs = fixture.Windows[^1].EndMs;
        var committedMs = commits[^1].EndMs - commits[0].StartMs;

        AssertEx.True(Math.Abs(clipMs - committedMs) <= 1_000,
            $"The committed span is {committedMs.ToString(CultureInfo.InvariantCulture)} ms against a "
            + $"{clipMs.ToString(CultureInfo.InvariantCulture)} ms clip; more than a second of speech is missing or invented.");
    }

    /// <summary>Drives the real segmenter over the real clip, answering every window from the recorded run.</summary>
    private static async Task<(RecordedWhisperTranscriber Transcriber, IReadOnlyList<LiveCommit> Commits)> ReplayAsync()
    {
        var transcriber = RecordedWhisperTranscriber.FromFixture(FixtureFileName);
        var fixture = transcriber.Fixture;
        var pcm = WavPayload.Read(await File.ReadAllBytesAsync(RecordedWhisperTranscriber.FixturePath(fixture.Clip)).ConfigureAwait(false));

        var segmenter = new LiveTranscriptionSegmenter(transcriber,
            TranscriptChannel.Mono,
            fixture.Model,
            languageCode: null,
            translate: false,
            new LiveSegmenterSettings
            {
                MaxWindowSeconds = fixture.MaxWindowSeconds,
                TailGuardMs = fixture.TailGuardMs,
                TickMs = fixture.TickMs
            });

        var commits = new List<LiveCommit>();
        var frameBytes = fixture.PushMs * WavPcm16.BytesPerMillisecond;
        for (var offset = 0; offset < pcm.Length; offset += frameBytes)
        {
            var tick = await segmenter.PushAsync(pcm.Slice(offset, Math.Min(frameBytes, pcm.Length - offset)), CancellationToken.None)
                                      .ConfigureAwait(false);
            commits.AddRange(tick.Commits);
        }

        commits.AddRange((await segmenter.FlushAsync(CancellationToken.None).ConfigureAwait(false)).Commits);
        return (transcriber, commits);
    }

    /// <summary>Lower-cases, drops punctuation and collapses whitespace, which is the comparison R17 asks for.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                _ = builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                _ = builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    private static string[] Words(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>How many of the recorded windows were submitted because the uncommitted span reached the cap.</summary>
    /// <remarks>
    ///     An at-cap window is the only place the segmenter commits a model answer about audio it cut itself, so the
    ///     count of them is exactly the number of fragment guesses the transcript may carry.
    /// </remarks>
    private static int ForcedBoundaryCount(GoldenFixture fixture) =>
        fixture.Windows.Count(window => window.EndMs - window.StartMs == fixture.MaxWindowSeconds * 1_000L);

    /// <summary>Levenshtein distance over words: how many words must be inserted, deleted or replaced.</summary>
    private static int WordEditDistance(string[] expected, string[] actual)
    {
        var previous = new int[actual.Length + 1];
        var current = new int[actual.Length + 1];
        for (var column = 0; column <= actual.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= expected.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= actual.Length; column++)
            {
                var substitution = previous[column - 1]
                                   + (string.Equals(expected[row - 1], actual[column - 1], StringComparison.Ordinal) ? 0 : 1);
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[actual.Length];
    }
}
