namespace XE_Local_AI_Engine.Tests.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentProgressDetectorTests
{
    [Test]
    public void RepeatedTool_UsesSanitizedStableFingerprintAndWarnsAtThreshold()
    {
        var detector = CreateDetector(out _, repeatedToolThreshold: 3);

        AssertEx.Null(detector.ObserveTool("read_file", "password=first /home/user/one.cs"));
        AssertEx.Null(detector.ObserveTool("read_file", "password=second /home/user/two.cs"));
        var warning = AssertEx.NotNull(detector.ObserveTool("read_file", "password=third /home/user/three.cs"));

        AssertEx.Equal(DevelopmentProgressWarningCategory.RepeatedTool, warning.Category);
        AssertEx.Equal(3, warning.Count);
        AssertEx.False(warning.Fingerprint.Contains("password", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(warning.Fingerprint.Contains("home", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void CommandFailures_WarnOnlyForConsecutiveIdenticalFailures()
    {
        var detector = CreateDetector(out _, commandFailureThreshold: 3);
        AssertEx.Null(detector.ObserveCommandFailure("build", 1, "compile"));
        AssertEx.Null(detector.ObserveCommandFailure("test", 1, "failed"));
        AssertEx.Null(detector.ObserveCommandFailure("build", 1, "compile"));
        AssertEx.Null(detector.ObserveCommandFailure("build", 1, "compile"));

        var warning = AssertEx.NotNull(detector.ObserveCommandFailure("build", 1, "compile"));
        AssertEx.Equal(DevelopmentProgressWarningCategory.RepeatedCommandFailure, warning.Category);
    }

    [Test]
    public void MeaningfulProgress_ResetsNoProgressAndPlanningWarnings()
    {
        var detector = CreateDetector(out var time, noProgressSeconds: 10, planningThreshold: 2);
        AssertEx.Null(detector.ObservePlanningActivity());
        AssertEx.NotNull(detector.ObservePlanningActivity());
        time.Advance(TimeSpan.FromSeconds(10));
        AssertEx.Equal(DevelopmentProgressWarningCategory.NoMeaningfulProgress,
            AssertEx.NotNull(detector.Evaluate()).Category);
        AssertEx.Null(detector.Evaluate());

        AssertEx.True(detector.MarkMeaningfulProgress(DevelopmentMeaningfulProgressKind.Artifact, "artifact-1"));
        AssertEx.Equal(0L, detector.SecondsSinceMeaningfulProgress);
        AssertEx.Null(detector.ObservePlanningActivity());
        time.Advance(TimeSpan.FromSeconds(9));
        AssertEx.Null(detector.Evaluate());
        AssertEx.False(detector.MarkMeaningfulProgress(DevelopmentMeaningfulProgressKind.Artifact, "artifact-1"));
        time.Advance(TimeSpan.FromSeconds(1));
        AssertEx.NotNull(detector.Evaluate());
    }

    [Test]
    public void SubjectOscillationAndApproachingLimits_ProduceAdvisoryWarningsOnce()
    {
        var detector = CreateDetector(out _);
        AssertEx.Null(detector.ObserveSubjectHash("A"));
        AssertEx.Null(detector.ObserveSubjectHash("B"));
        AssertEx.Equal(DevelopmentProgressWarningCategory.SubjectOscillation,
            AssertEx.NotNull(detector.ObserveSubjectHash("A")).Category);

        var warnings = detector.ObserveLimits(providerRounds: 8,
            maxProviderRounds: 10,
            toolCalls: 8,
            maxToolCalls: 10,
            contextTokensUsed: 85,
            maxContextTokens: 100);
        AssertEx.Equal(3, warnings.Count);
        AssertEx.Contains(warnings, warning => warning.Category == DevelopmentProgressWarningCategory.ProviderRoundLimit);
        AssertEx.Contains(warnings, warning => warning.Category == DevelopmentProgressWarningCategory.ToolCallLimit);
        AssertEx.Contains(warnings, warning => warning.Category == DevelopmentProgressWarningCategory.ContextHeadroom);
        AssertEx.Empty(detector.ObserveLimits(9, 10, 9, 10, 90, 100));
    }

    [Test]
    public void ReviewFinding_WarnsOnlyWhenSameFindingPersistsAcrossDistinctRounds()
    {
        var detector = CreateDetector(out _, reviewFindingThreshold: 2);
        AssertEx.Null(detector.ObserveReviewFinding(1, "correctness", "same issue"));
        AssertEx.Null(detector.ObserveReviewFinding(1, "correctness", "same issue"));

        var warning = AssertEx.NotNull(detector.ObserveReviewFinding(2, "correctness", "same issue"));
        AssertEx.Equal(DevelopmentProgressWarningCategory.RepeatedReviewFinding, warning.Category);
        AssertEx.Equal(2, warning.Count);
    }

    private static DevelopmentProgressDetector CreateDetector(out AdjustableTimeProvider time,
        int repeatedToolThreshold = 3,
        int commandFailureThreshold = 3,
        int noProgressSeconds = 120,
        int planningThreshold = 3,
        int reviewFindingThreshold = 2)
    {
        time = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        return new DevelopmentProgressDetector(Options.Create(new DevelopmentOptions
        {
            RepeatedToolWarningThreshold = repeatedToolThreshold,
            RepeatedCommandFailureWarningThreshold = commandFailureThreshold,
            NoProgressWarningSeconds = noProgressSeconds,
            SubjectOscillationWarningThreshold = 1,
            ApproachingLimitPercent = 80,
            ContextHeadroomWarningPercent = 20,
            PlanningWithoutProgressWarningThreshold = planningThreshold,
            RepeatedReviewFindingWarningThreshold = reviewFindingThreshold
        }), time);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current += duration;
    }
}
