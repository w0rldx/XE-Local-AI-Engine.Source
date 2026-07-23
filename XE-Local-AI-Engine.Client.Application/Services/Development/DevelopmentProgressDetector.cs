namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.Options;

public enum DevelopmentProgressWarningCategory
{
    RepeatedTool,
    RepeatedCommandFailure,
    NoMeaningfulProgress,
    SubjectOscillation,
    ProviderRoundLimit,
    ToolCallLimit,
    ContextHeadroom,
    RepeatedReviewFinding,
    PlanningWithoutArtifactProgress
}

public enum DevelopmentMeaningfulProgressKind
{
    Artifact,
    File,
    Validation,
    ReviewFinding
}

public sealed record DevelopmentProgressWarning(
    DevelopmentProgressWarningCategory Category,
    string Fingerprint,
    int Count,
    long OccurredAtUtc,
    string Message);

public sealed class DevelopmentProgressDetector
{
    private readonly DevelopmentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, HashSet<int>> _reviewFindingRounds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _meaningfulProgressFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<DevelopmentProgressWarningCategory> _limitWarnings = [];
    private readonly Queue<string> _subjectHistory = new(3);
    private string? _lastToolFingerprint;
    private int _repeatedToolCount;
    private string? _lastCommandFailureFingerprint;
    private int _repeatedCommandFailureCount;
    private int _subjectOscillationCount;
    private int _planningWithoutProgressCount;
    private bool _noProgressWarningRaised;
    private long _lastMeaningfulProgressUtc;

    public DevelopmentProgressDetector(IOptions<DevelopmentOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lastMeaningfulProgressUtc = Now;
    }

    public long SecondsSinceMeaningfulProgress => Math.Max(0, (Now - _lastMeaningfulProgressUtc) / 1000);

    public DevelopmentProgressWarning? ObserveTool(string toolId, string? arguments)
    {
        var fingerprint = DevelopmentAttemptLiveSanitizer.StableFingerprint(toolId, arguments);
        _repeatedToolCount = string.Equals(_lastToolFingerprint, fingerprint, StringComparison.Ordinal)
            ? _repeatedToolCount + 1
            : 1;
        _lastToolFingerprint = fingerprint;
        return ThresholdWarning(_repeatedToolCount,
            _options.RepeatedToolWarningThreshold,
            DevelopmentProgressWarningCategory.RepeatedTool,
            fingerprint,
            "The same tool operation is repeating without demonstrated progress.");
    }

    public DevelopmentProgressWarning? ObserveCommandFailure(string commandId, int exitCode, string? failureCategory)
    {
        var fingerprint = DevelopmentAttemptLiveSanitizer.StableFingerprint(commandId,
            $"exit:{exitCode};category:{failureCategory}");
        _repeatedCommandFailureCount = string.Equals(_lastCommandFailureFingerprint, fingerprint, StringComparison.Ordinal)
            ? _repeatedCommandFailureCount + 1
            : 1;
        _lastCommandFailureFingerprint = fingerprint;
        return ThresholdWarning(_repeatedCommandFailureCount,
            _options.RepeatedCommandFailureWarningThreshold,
            DevelopmentProgressWarningCategory.RepeatedCommandFailure,
            fingerprint,
            "The same command failure is repeating.");
    }

    public DevelopmentProgressWarning? ObserveSubjectHash(string subjectHash)
    {
        var fingerprint = DevelopmentAttemptLiveSanitizer.StableFingerprint("subject", subjectHash);
        if (_subjectHistory.Count == 3)
        {
            _subjectHistory.Dequeue();
        }

        _subjectHistory.Enqueue(fingerprint);
        if (_subjectHistory.Count != 3)
        {
            return null;
        }

        var values = _subjectHistory.ToArray();
        if (!string.Equals(values[0], values[2], StringComparison.Ordinal)
            || string.Equals(values[0], values[1], StringComparison.Ordinal))
        {
            return null;
        }

        _subjectOscillationCount++;
        return ThresholdWarning(_subjectOscillationCount,
            _options.SubjectOscillationWarningThreshold,
            DevelopmentProgressWarningCategory.SubjectOscillation,
            DevelopmentAttemptLiveSanitizer.StableFingerprint("oscillation", $"{values[0]}:{values[1]}"),
            "The workspace subject is oscillating between prior states.");
    }

    public IReadOnlyList<DevelopmentProgressWarning> ObserveLimits(int providerRounds,
        int maxProviderRounds,
        int toolCalls,
        int maxToolCalls,
        long? contextTokensUsed,
        long? maxContextTokens)
    {
        var warnings = new List<DevelopmentProgressWarning>(3);
        AddLimitWarning(warnings,
            DevelopmentProgressWarningCategory.ProviderRoundLimit,
            providerRounds,
            maxProviderRounds,
            _options.ApproachingLimitPercent,
            "The provider-round limit is approaching.");
        AddLimitWarning(warnings,
            DevelopmentProgressWarningCategory.ToolCallLimit,
            toolCalls,
            maxToolCalls,
            _options.ApproachingLimitPercent,
            "The tool-call limit is approaching.");

        if (contextTokensUsed.HasValue && maxContextTokens is > 0)
        {
            var usagePercent = Percentage(contextTokensUsed.Value, maxContextTokens.Value);
            var threshold = 100 - _options.ContextHeadroomWarningPercent;
            AddLimitWarning(warnings,
                DevelopmentProgressWarningCategory.ContextHeadroom,
                usagePercent,
                100,
                threshold,
                "The model context headroom is low.");
        }

        return warnings;
    }

    public DevelopmentProgressWarning? ObserveReviewFinding(int reviewRound, string category, string summary)
    {
        var fingerprint = DevelopmentAttemptLiveSanitizer.StableFingerprint(category, summary);
        if (!_reviewFindingRounds.TryGetValue(fingerprint, out var rounds))
        {
            rounds = [];
            _reviewFindingRounds.Add(fingerprint, rounds);
        }

        if (!rounds.Add(reviewRound))
        {
            return null;
        }

        return ThresholdWarning(rounds.Count,
            _options.RepeatedReviewFindingWarningThreshold,
            DevelopmentProgressWarningCategory.RepeatedReviewFinding,
            fingerprint,
            "The same review finding remains across review rounds.");
    }

    public DevelopmentProgressWarning? ObservePlanningActivity()
    {
        _planningWithoutProgressCount++;
        var fingerprint = DevelopmentAttemptLiveSanitizer.StableFingerprint("planning", "without-progress");
        return ThresholdWarning(_planningWithoutProgressCount,
            _options.PlanningWithoutProgressWarningThreshold,
            DevelopmentProgressWarningCategory.PlanningWithoutArtifactProgress,
            fingerprint,
            "Planning or reporting is repeating without artifact progress.");
    }

    public DevelopmentProgressWarning? Evaluate()
    {
        if (_noProgressWarningRaised || SecondsSinceMeaningfulProgress < _options.NoProgressWarningSeconds)
        {
            return null;
        }

        _noProgressWarningRaised = true;
        return Warning(DevelopmentProgressWarningCategory.NoMeaningfulProgress,
            DevelopmentAttemptLiveSanitizer.StableFingerprint("progress", "stalled"),
            1,
            "No meaningful progress has been observed within the configured interval.");
    }

    public bool MarkMeaningfulProgress(DevelopmentMeaningfulProgressKind kind, string? stableIdentity = null)
    {
        if (!string.IsNullOrWhiteSpace(stableIdentity))
        {
            var fingerprint = DevelopmentAttemptLiveSanitizer.StableFingerprint(kind.ToString(), stableIdentity);
            if (!_meaningfulProgressFingerprints.Add(fingerprint))
            {
                return false;
            }
        }

        _lastMeaningfulProgressUtc = Now;
        _noProgressWarningRaised = false;
        _planningWithoutProgressCount = 0;
        _lastToolFingerprint = null;
        _repeatedToolCount = 0;
        _lastCommandFailureFingerprint = null;
        _repeatedCommandFailureCount = 0;
        return true;
    }

    private void AddLimitWarning(ICollection<DevelopmentProgressWarning> warnings,
        DevelopmentProgressWarningCategory category,
        long current,
        long maximum,
        int thresholdPercent,
        string message)
    {
        if (maximum <= 0 || current < 0 || Percentage(current, maximum) < thresholdPercent || !_limitWarnings.Add(category))
        {
            return;
        }

        warnings.Add(Warning(category,
            DevelopmentAttemptLiveSanitizer.StableFingerprint("limit", category.ToString()),
            1,
            message));
    }

    private DevelopmentProgressWarning? ThresholdWarning(int count,
        int threshold,
        DevelopmentProgressWarningCategory category,
        string fingerprint,
        string message)
    {
        return count == threshold ? Warning(category, fingerprint, count, message) : null;
    }

    private DevelopmentProgressWarning Warning(DevelopmentProgressWarningCategory category,
        string fingerprint,
        int count,
        string message) =>
        new(category, fingerprint, count, Now, message);

    private long Now => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static int Percentage(long current, long maximum) =>
        (int)Math.Min(100, current * 100m / maximum);
}
