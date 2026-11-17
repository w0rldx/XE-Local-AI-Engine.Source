namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;

/// <summary>
///     Structural integrity of the committed dedup calibration data (<c>DedupPairs/</c>) and the arithmetic of the frozen
///     calibration rule. Nothing here embeds text: the opt-in <see cref="MemoryDedupCalibrationLiveTests" /> does.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class MemoryDedupPairSetTests
{
    private const int PairsPerKindAndLanguage = 12;
    private const int ScenariosPerLanguage = 8;
    private const int MinimumHeldOutCandidates = 48;

    [Test]
    public void Pairs_HoldTwelvePerKindAndLanguage_WithUniqueIdsAndTwoDistinctSides()
    {
        var set = MemoryDedupPairSet.Load(MemoryDedupPairSet.CommittedDirectory);

        AssertEx.Equal(set.Pairs.Count, set.Pairs.Select(static pair => pair.Id).Distinct(StringComparer.Ordinal).Count(), "pair ids must be unique.");
        foreach (var language in MemoryDedupPairSet.Languages)
        {
            foreach (var kind in MemoryDedupPairSet.PairKinds)
            {
                var count = set.Pairs.Count(pair => pair.Language == language && pair.Kind == kind);
                AssertEx.Equal(PairsPerKindAndLanguage, count, $"{kind} pairs in '{language}'.");
            }
        }

        AssertEx.Equal(MemoryDedupPairSet.Languages.Count * MemoryDedupPairSet.PairKinds.Count * PairsPerKindAndLanguage, set.Pairs.Count, "no pair has an unknown kind or language.");
        foreach (var pair in set.Pairs)
        {
            AssertEx.False(string.IsNullOrWhiteSpace(pair.LessonId) || string.IsNullOrWhiteSpace(pair.A) || string.IsNullOrWhiteSpace(pair.B), $"pair '{pair.Id}' has a blank field.");
            AssertEx.False(string.Equals(pair.A.Trim(), pair.B.Trim(), StringComparison.OrdinalIgnoreCase), $"pair '{pair.Id}' compares a text with itself.");
        }
    }

    [Test]
    public void Texts_AreUniqueAcrossBothFiles_SoNoPairOrScenarioItemRepeats()
    {
        var set = MemoryDedupPairSet.Load(MemoryDedupPairSet.CommittedDirectory);
        var texts = set.Pairs.SelectMany(static pair => new[] { pair.A, pair.B })
                       .Concat(set.Scenarios.SelectMany(static scenario => scenario.Existing.Concat(scenario.Candidates)).Select(static item => item.Behavior))
                       .Select(static text => text.Trim())
                       .ToList();

        var repeated = texts.GroupBy(static text => text, StringComparer.OrdinalIgnoreCase).Where(static group => group.Count() > 1).Select(static group => group.Key).ToList();

        AssertEx.Empty(repeated, "every text must appear once: " + string.Join(" | ", repeated));
    }

    [Test]
    public void HeldOut_HoldsEightScenariosPerLanguage_EachWithOneCandidateOfEveryKind()
    {
        var set = MemoryDedupPairSet.Load(MemoryDedupPairSet.CommittedDirectory);

        AssertEx.Equal(set.Scenarios.Count, set.Scenarios.Select(static scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count(), "scenario ids must be unique.");
        foreach (var language in MemoryDedupPairSet.Languages)
        {
            AssertEx.Equal(ScenariosPerLanguage, set.Scenarios.Count(scenario => scenario.Language == language), $"scenarios in '{language}'.");
        }

        AssertEx.Equal(MemoryDedupPairSet.Languages.Count * ScenariosPerLanguage, set.Scenarios.Count, "no scenario has an unknown language.");
        AssertEx.True(set.Scenarios.Sum(static scenario => scenario.Candidates.Count) >= MinimumHeldOutCandidates, $"at least {MinimumHeldOutCandidates} held-out candidates are required.");
        foreach (var scenario in set.Scenarios)
        {
            AssertEx.True(scenario.Existing.Count > 0, $"scenario '{scenario.Id}' has no existing memory.");
            AssertEx.True(scenario.Existing.All(static item => item.Kind is null), $"scenario '{scenario.Id}': an existing memory carries a candidate kind.");
            var kinds = scenario.Candidates.Select(static item => item.Kind ?? "(none)").Order(StringComparer.Ordinal).ToList();
            AssertEx.Equal(string.Join(',', MemoryDedupPairSet.CandidateKinds.Order(StringComparer.Ordinal)), string.Join(',', kinds), $"scenario '{scenario.Id}' candidate kinds.");
        }
    }

    [Test]
    public void HeldOut_Lessons_AreDisjointFromTheCalibrationLessons()
    {
        var set = MemoryDedupPairSet.Load(MemoryDedupPairSet.CommittedDirectory);
        var calibration = set.Pairs.Select(static pair => pair.LessonId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var shared = set.Scenarios.SelectMany(static scenario => scenario.Existing.Concat(scenario.Candidates))
                        .Select(static item => item.LessonId)
                        .Where(calibration.Contains)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

        AssertEx.Empty(shared, "held-out lessons must not reuse calibration lessons: " + string.Join(", ", shared));
    }

    [Test]
    public void HeldOut_CandidateKinds_AgreeWithLessonAndScope()
    {
        var set = MemoryDedupPairSet.Load(MemoryDedupPairSet.CommittedDirectory);

        foreach (var scenario in set.Scenarios)
        {
            var existingLessons = scenario.Existing.Select(static item => item.LessonId).ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < scenario.Candidates.Count; index++)
            {
                var candidate = scenario.Candidates[index];
                var where = $"scenario '{scenario.Id}' candidate {index} ({candidate.Kind})";
                var sameLessonExisting = scenario.Existing.Where(item => item.LessonId == candidate.LessonId).ToList();
                switch (candidate.Kind)
                {
                    case MemoryDedupPairSet.DuplicateCandidate:
                        AssertEx.True(sameLessonExisting.Exists(item => item.Scope == candidate.Scope), $"{where} restates no same-scope existing memory.");
                        break;
                    case MemoryDedupPairSet.CrossScopeCandidate:
                        AssertEx.True(sameLessonExisting.Count > 0 && sameLessonExisting.TrueForAll(item => item.Scope != candidate.Scope), $"{where} must restate an existing memory under another scope.");
                        break;
                    case MemoryDedupPairSet.NearMissCandidate:
                        AssertEx.False(existingLessons.Contains(candidate.LessonId), $"{where} must state a lesson no existing memory holds.");
                        AssertEx.True(scenario.Existing.Any(item => item.Scope == candidate.Scope), $"{where} must share its scope with an existing memory.");
                        break;
                    case MemoryDedupPairSet.NewCandidate:
                        AssertEx.False(existingLessons.Contains(candidate.LessonId), $"{where} must state a lesson no existing memory holds.");
                        break;
                    default:
                        var original = scenario.Candidates.Take(index).ToList()
                                               .FindIndex(item => item.Kind == MemoryDedupPairSet.NewCandidate && item.LessonId == candidate.LessonId && item.Scope == candidate.Scope);
                        AssertEx.True(original >= 0, $"{where} must restate an earlier 'new' candidate of the same scope.");
                        break;
                }
            }
        }
    }

    [Test]
    public void HeldOut_ExpectedIndexes_EqualTheProductionDecision_WhenLessonsMatchExactly()
    {
        var set = MemoryDedupPairSet.Load(MemoryDedupPairSet.CommittedDirectory);
        var lessons = set.Scenarios.SelectMany(static scenario => scenario.Existing.Concat(scenario.Candidates))
                         .Select(static item => item.LessonId)
                         .Distinct(StringComparer.Ordinal)
                         .ToList();

        foreach (var scenario in set.Scenarios)
        {
            AssertEx.True(scenario.ExpectedDuplicateIndexes.All(index => index >= 0 && index < scenario.Candidates.Count), $"scenario '{scenario.Id}' names an index out of range.");
            AssertEx.Equal(scenario.ExpectedDuplicateIndexes.Count, scenario.ExpectedDuplicateIndexes.Distinct().Count(), $"scenario '{scenario.Id}' repeats an index.");

            // One-hot vector per lesson: cosine 1 for the same lesson, 0 otherwise, so the decision depends on lesson and scope only.
            var result = MemorySemanticDeduplicator.ClassifyCandidates(scenario.ToExisting(),
                scenario.ToCandidates(),
                [.. scenario.Existing.Select(item => OneHot(lessons.IndexOf(item.LessonId), lessons.Count))],
                [.. scenario.Candidates.Select(item => OneHot(lessons.IndexOf(item.LessonId), lessons.Count))],
                threshold: 0.5d);

            AssertEx.Equal(string.Join(',', scenario.ExpectedDuplicateIndexes.Order()), string.Join(',', result.DuplicateIndexes.Order()), $"scenario '{scenario.Id}' expected duplicates.");
        }
    }

    [Test]
    public void Quantile_InterpolatesLinearlyBetweenOrderStatistics()
    {
        double[] values = [5d, 1d, 4d, 2d, 3d];

        AssertEx.Equal(3d, MemoryDedupCalibrationRule.Quantile(values, 0.5d));
        AssertEx.Equal(4.8d, Math.Round(MemoryDedupCalibrationRule.Quantile(values, 0.95d), 12));
        AssertEx.Equal(1d, MemoryDedupCalibrationRule.Quantile(values, 0d));
        AssertEx.Equal(5d, MemoryDedupCalibrationRule.Quantile(values, 1d));
    }

    [Test]
    public void RoundUpToStep_RoundsUpToTheNextStep_AndKeepsAnExactStep()
    {
        AssertEx.Equal(0.86d, MemoryDedupCalibrationRule.RoundUpToStep(0.86d));
        AssertEx.Equal(0.865d, MemoryDedupCalibrationRule.RoundUpToStep(0.8601d));
        AssertEx.Equal(1.005d, MemoryDedupCalibrationRule.RoundUpToStep(1.0001d));
    }

    [Test]
    public void BootstrapInterval_IsReproducibleForTheSeed_AndBracketsThePointEstimate()
    {
        double[] values = [.. Enumerable.Range(0, 48).Select(static index => index / 48d)];

        var first = MemoryDedupCalibrationRule.BootstrapInterval(values);
        var second = MemoryDedupCalibrationRule.BootstrapInterval(values);
        var point = MemoryDedupCalibrationRule.Quantile(values, MemoryDedupCalibrationRule.NegativeQuantile);

        AssertEx.Equal(first, second);
        AssertEx.True(first.Lower <= point && point <= first.Upper, $"[{first.Lower}, {first.Upper}] must contain {point}.");
        AssertEx.True(first.Upper <= values.Max(), "a resampled quantile never exceeds the largest value.");
    }

    [Test]
    public void Auc_CountsPairwiseWinsAndHalvesTies()
    {
        AssertEx.Equal(1d, MemoryDedupCalibrationRule.Auc([0.9d, 0.8d], [0.1d, 0.2d]));
        AssertEx.Equal(0d, MemoryDedupCalibrationRule.Auc([0.1d], [0.9d]));
        AssertEx.Equal(0.5d, MemoryDedupCalibrationRule.Auc([0.5d], [0.5d]));
    }

    [Test]
    public void Passes_RequiresEveryBound()
    {
        AssertEx.True(MemoryDedupCalibrationRule.Passes(0.9d, 0.5d, 0.5d, 0.9d));
        AssertEx.False(MemoryDedupCalibrationRule.Passes(1.005d, 1d, 1d, 1d), "t above 1 can never flag anything.");
        AssertEx.False(MemoryDedupCalibrationRule.Passes(0.9d, 0.49d, 1d, 1d), "calibration recall below 0.5.");
        AssertEx.False(MemoryDedupCalibrationRule.Passes(0.9d, 1d, 0.49d, 1d), "held-out recall below 0.5.");
        AssertEx.False(MemoryDedupCalibrationRule.Passes(0.9d, 1d, 1d, 0.89d), "held-out precision below 0.9.");
        AssertEx.False(MemoryDedupCalibrationRule.Passes(0.9d, 1d, 1d, null), "no flagged candidate has no precision.");
    }

    private static ReadOnlyMemory<float> OneHot(int position, int length)
    {
        var vector = new float[length];
        vector[position] = 1f;
        return vector;
    }
}

/// <summary>One calibration pair of <c>DedupPairs/pairs.json</c>; the authoring rules are in that folder's README.</summary>
internal sealed class MemoryDedupPair
{
    public required string Id { get; init; }

    public required string Language { get; init; }

    public required string Kind { get; init; }

    public required string LessonId { get; init; }

    public required string A { get; init; }

    public required string B { get; init; }
}

/// <summary>An existing memory or a candidate of a held-out scenario; only candidates carry a <see cref="Kind" />.</summary>
internal sealed class MemoryDedupItem
{
    public required string LessonId { get; init; }

    public required MemoryScope Scope { get; init; }

    public required string Behavior { get; init; }

    public string? Kind { get; init; }
}

/// <summary>One held-out scenario of <c>DedupPairs/heldout.json</c>: what production would see in one extraction run.</summary>
internal sealed class MemoryDedupScenario
{
    public required string Id { get; init; }

    public required string Language { get; init; }

    public required IReadOnlyList<MemoryDedupItem> Existing { get; init; }

    public required IReadOnlyList<MemoryDedupItem> Candidates { get; init; }

    public required IReadOnlyList<int> ExpectedDuplicateIndexes { get; init; }

    public IReadOnlyList<MemoryDedupExisting> ToExisting() =>
    [
        .. Existing.Select(static item => new MemoryDedupExisting
        {
            Id = Guid.NewGuid(),
            Version = 1,
            Scope = item.Scope,
            Behavior = item.Behavior
        })
    ];

    public IReadOnlyList<MemoryDedupCandidate> ToCandidates() =>
    [
        .. Candidates.Select(static item => new MemoryDedupCandidate
        {
            Scope = item.Scope,
            Behavior = item.Behavior
        })
    ];
}

/// <summary>Loads the committed dedup calibration data from the copy the test project places next to its assembly.</summary>
internal sealed class MemoryDedupPairSet
{
    public const string PairsFile = "pairs.json";
    public const string HeldOutFile = "heldout.json";

    public const string DuplicateCandidate = "duplicate";
    public const string NearMissCandidate = "near-miss";
    public const string CrossScopeCandidate = "cross-scope";
    public const string NewCandidate = "new";
    public const string InBatchDuplicateCandidate = "in-batch-duplicate";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // A misspelled field must fail the load, never read as an empty value.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<MemoryScope>(allowIntegerValues: false) }
    };

    public static string CommittedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Knowledge", "RetrievalEval", "DedupPairs");

    public static IReadOnlyList<string> Languages { get; } = ["en", "de"];

    public static IReadOnlyList<string> PairKinds { get; } = [DuplicateCandidate, NearMissCandidate, "unrelated"];

    public static IReadOnlyList<string> CandidateKinds { get; } = [DuplicateCandidate, NearMissCandidate, CrossScopeCandidate, NewCandidate, InBatchDuplicateCandidate];

    public required IReadOnlyList<MemoryDedupPair> Pairs { get; init; }

    public required IReadOnlyList<MemoryDedupScenario> Scenarios { get; init; }

    public required string PairsSha256 { get; init; }

    public required string HeldOutSha256 { get; init; }

    public static MemoryDedupPairSet Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var pairs = Read(directory, PairsFile);
        var heldOut = Read(directory, HeldOutFile);
        return new MemoryDedupPairSet
        {
            Pairs = Parse<PairDocument>(pairs, PairsFile).Pairs ?? throw new InvalidDataException($"Dedup calibration data: {PairsFile} has no 'pairs'."),
            Scenarios = Parse<HeldOutDocument>(heldOut, HeldOutFile).Scenarios ?? throw new InvalidDataException($"Dedup calibration data: {HeldOutFile} has no 'scenarios'."),
            PairsSha256 = Convert.ToHexStringLower(SHA256.HashData(pairs)),
            HeldOutSha256 = Convert.ToHexStringLower(SHA256.HashData(heldOut))
        };
    }

    private static byte[] Read(string directory, string file)
    {
        var path = Path.Combine(directory, file);
        return File.Exists(path) ? File.ReadAllBytes(path) : throw new InvalidDataException($"Dedup calibration data: '{path}' does not exist.");
    }

    private static T Parse<T>(byte[] utf8Json, string file)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, JsonOptions) ?? throw new InvalidDataException($"Dedup calibration data: {file} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Dedup calibration data: {file} is malformed: {exception.Message}", exception);
        }
    }

    internal sealed class PairDocument
    {
        public IReadOnlyList<MemoryDedupPair>? Pairs { get; init; }
    }

    internal sealed class HeldOutDocument
    {
        public IReadOnlyList<MemoryDedupScenario>? Scenarios { get; init; }
    }
}
