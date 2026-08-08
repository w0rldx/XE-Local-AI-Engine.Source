namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class DevelopmentAttemptLiveProgress
{
    private static readonly TimeSpan OutputPublishInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDevelopmentAttemptLiveBroker _broker;
    private readonly DevelopmentProgressDetector _detector;
    private readonly DevelopmentExecutionSnapshot _execution;
    private readonly int _maxContextTokens;
    private readonly int _maxProviderRounds;
    private readonly int _maxToolCalls;
    private readonly StringBuilder _pendingOutput = new();
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _changedFiles = new(StringComparer.Ordinal);
    private int _commandCount;
    private long _firstOutputAtUtc;
    private long _lastOutputPublishAtUtc;
    private long _operationStartedAtUtc;
    private long _patchByteCount;
    private int _providerRoundCount = 1;
    private int _toolCallCount;
    private bool _hasPublishedReasoning;

    public DevelopmentAttemptLiveProgress(DevelopmentExecutionSnapshot execution,
        IDevelopmentAttemptLiveBroker broker,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider,
        int maxOutputTokens,
        int maxToolCalls)
    {
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _detector = new DevelopmentProgressDetector(options, timeProvider);
        _maxProviderRounds = Math.Max(1, maxToolCalls + 1);
        _maxToolCalls = maxToolCalls;
        _maxContextTokens = Math.Max(2048, maxOutputTokens * 2);
    }

    public void Output(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var now = Now;
        if (!string.IsNullOrEmpty(update.Text))
        {
            _firstOutputAtUtc = _firstOutputAtUtc == 0 ? now : _firstOutputAtUtc;
            _pendingOutput.Append(update.Text);
        }

        // Reasoning is NOT part of Text: it arrives as TextReasoningContent, which ChatResponseUpdate.Text does not
        // concatenate. A reasoning model between two tool calls therefore produced updates that appended nothing,
        // published nothing, and left the whole live panel — every metric tile, and the stall age itself — frozen on
        // the last tool call's values while generation ran on. Measured live on 2026-07-31: 32,106 decoded tokens in
        // one provider round with the UI static for over three minutes and "CONNECTED" still showing.
        //
        // The reasoning text itself is deliberately not forwarded as output: it is not the attempt's answer, and the
        // live channel is bounded. What is forwarded is the fact that the model is still working, which is the part
        // the operator needs to distinguish "thinking" from "hung".
        var reasoning = update.Contents.OfType<TextReasoningContent>().Any(static content => !string.IsNullOrEmpty(content.Text));
        var usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details;
        if (_pendingOutput.Length > 0 && (now - _lastOutputPublishAtUtc >= OutputPublishInterval.TotalMilliseconds || usage is not null))
        {
            PublishOutput(usage);
        }
        else if (usage is not null)
        {
            PublishMetrics(usage);
        }
        else if (reasoning && (!_hasPublishedReasoning || now - _lastOutputPublishAtUtc >= OutputPublishInterval.TotalMilliseconds))
        {
            // The first reasoning update publishes immediately — "the model started thinking" is the transition the
            // operator is waiting on, and delaying it by a cadence window is exactly the silence being fixed. Tracked
            // by its own flag rather than by testing _lastOutputPublishAtUtc against 0, because 0 is a real instant
            // (the Unix epoch) and a sentinel that collides with a legal value fires twice when they meet.
            _hasPublishedReasoning = true;
            _lastOutputPublishAtUtc = now;
            Publish(DevelopmentAttemptLiveUpdateKind.Progress, "Model is reasoning.");
        }
    }

    public void CompleteOutput(UsageDetails? usage)
    {
        if (_pendingOutput.Length > 0)
        {
            PublishOutput(usage);
        }
        else if (usage is not null)
        {
            PublishMetrics(usage);
        }
    }

    public void ToolStarted(string toolId, string? argumentFingerprintSource)
    {
        _toolCallCount++;
        _providerRoundCount = Math.Min(_maxProviderRounds, _toolCallCount + 1);
        _operationStartedAtUtc = Now;
        Publish(DevelopmentAttemptLiveUpdateKind.Tool,
            activity: $"Running tool {toolId}.",
            currentToolId: toolId,
            elapsedMilliseconds: 0);
        PublishWarning(_detector.ObserveTool(toolId, argumentFingerprintSource));
        PublishLimitWarnings();
    }

    public void ToolCompleted(string toolId)
    {
        Publish(DevelopmentAttemptLiveUpdateKind.Tool,
            activity: $"Tool {toolId} finished.",
            currentToolId: toolId,
            elapsedMilliseconds: ElapsedSince(_operationStartedAtUtc));
        PublishWarning(_detector.Evaluate());
        _operationStartedAtUtc = 0;
    }

    public void CommandStarted(string commandId)
    {
        _commandCount++;
        Publish(DevelopmentAttemptLiveUpdateKind.Command,
            activity: $"Running command {commandId}.",
            currentCommandId: commandId,
            elapsedMilliseconds: 0);
    }

    public void CommandCompleted(DevelopmentCommandEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Publish(DevelopmentAttemptLiveUpdateKind.Command,
            activity: evidence.Completed && evidence.ExitCode == 0
                ? $"Command {evidence.CommandId} finished."
                : $"Command {evidence.CommandId} failed.",
            currentCommandId: evidence.CommandId,
            elapsedMilliseconds: evidence.DurationMilliseconds);
        if (!evidence.Completed || evidence.ExitCode != 0)
        {
            PublishWarning(_detector.ObserveCommandFailure(evidence.CommandId,
                evidence.ExitCode,
                evidence.Completed ? "exit" : "incomplete"));
        }

        PublishWarning(_detector.Evaluate());
    }

    public void FileChanged(string relativePath, long byteCount)
    {
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            _changedFiles.Add(relativePath);
        }

        _patchByteCount = Math.Max(_patchByteCount, byteCount);
        _ = _detector.MarkMeaningfulProgress(DevelopmentMeaningfulProgressKind.File, relativePath);
        Publish(DevelopmentAttemptLiveUpdateKind.Progress, "Workspace files changed.");
    }

    public void PatchObserved(IReadOnlyCollection<string> changedFiles, long patchByteCount, string subjectHash)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        foreach (var path in changedFiles.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            _changedFiles.Add(path);
        }

        _patchByteCount = Math.Max(0, patchByteCount);
        _ = _detector.MarkMeaningfulProgress(DevelopmentMeaningfulProgressKind.Artifact, subjectHash);
        PublishWarning(_detector.ObserveSubjectHash(subjectHash));
        Publish(DevelopmentAttemptLiveUpdateKind.Progress,
            "Exact workspace evidence was exported.",
            subjectHash: subjectHash);
    }

    public void ReviewObserved(int reviewRound, DevelopmentReviewerSubmission submission, string subjectHash)
    {
        ArgumentNullException.ThrowIfNull(submission);
        foreach (var finding in submission.Findings)
        {
            PublishWarning(_detector.ObserveReviewFinding(reviewRound, finding.Category, finding.Summary));
        }

        _ = _detector.MarkMeaningfulProgress(DevelopmentMeaningfulProgressKind.ReviewFinding, subjectHash);
        Publish(DevelopmentAttemptLiveUpdateKind.Progress,
            "Review evidence was produced.",
            subjectHash: subjectHash);
    }

    private void PublishOutput(UsageDetails? usage)
    {
        var delta = _pendingOutput.ToString();
        _pendingOutput.Clear();
        _lastOutputPublishAtUtc = Now;
        Publish(DevelopmentAttemptLiveUpdateKind.Output,
            "Model output received.",
            outputDelta: delta,
            usage: usage);
    }

    private void PublishMetrics(UsageDetails usage)
    {
        Publish(DevelopmentAttemptLiveUpdateKind.Metrics, "Usage updated.", usage: usage);
        PublishLimitWarnings(usage);
    }

    private void PublishLimitWarnings(UsageDetails? usage = null)
    {
        var contextUsed = usage is null
            ? (long?)null
            : Math.Max(0, (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0));
        foreach (var warning in _detector.ObserveLimits(_providerRoundCount,
                     _maxProviderRounds,
                     _toolCallCount,
                     _maxToolCalls,
                     contextUsed,
                     _maxContextTokens))
        {
            PublishWarning(warning);
        }
    }

    private void PublishWarning(DevelopmentProgressWarning? warning)
    {
        if (warning is null)
        {
            return;
        }

        Publish(DevelopmentAttemptLiveUpdateKind.Warning,
            warning.Message,
            warningCategory: warning.Category,
            warningMessage: warning.Message);
    }

    private void Publish(DevelopmentAttemptLiveUpdateKind kind,
        string activity,
        string? outputDelta = null,
        UsageDetails? usage = null,
        string? currentToolId = null,
        string? currentCommandId = null,
        long? elapsedMilliseconds = null,
        string? subjectHash = null,
        DevelopmentProgressWarningCategory? warningCategory = null,
        string? warningMessage = null)
    {
        var inputTokens = usage?.InputTokenCount;
        var outputTokens = usage?.OutputTokenCount;
        var contextTokens = inputTokens.HasValue || outputTokens.HasValue
            ? Math.Max(0, (inputTokens ?? 0) + (outputTokens ?? 0))
            : (long?)null;
        var usagePercent = contextTokens.HasValue
            ? Math.Min(100d, contextTokens.Value * 100d / _maxContextTokens)
            : (double?)null;
        var elapsedOutputSeconds = _firstOutputAtUtc == 0 ? 0 : Math.Max(0, Now - _firstOutputAtUtc) / 1000d;

        _ = _broker.TryPublish(new DevelopmentAttemptLiveUpdate
        {
            ProjectId = _execution.ProjectId,
            TaskId = _execution.TaskId,
            AttemptId = _execution.AttemptId,
            Kind = kind,
            Role = _execution.AttemptRole,
            Status = DevelopmentAttemptStatus.Running,
            ModelId = _execution.ModelId,
            Provider = _execution.Provider,
            OutputDelta = outputDelta,
            CurrentActivity = activity,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ReasoningTokens = usage?.ReasoningTokenCount,
            OutputTokensPerSecond = outputTokens.HasValue && elapsedOutputSeconds > 0
                ? outputTokens.Value / elapsedOutputSeconds
                : null,
            ProviderRoundCount = _providerRoundCount,
            ToolCallCount = _toolCallCount,
            CommandCount = _commandCount,
            CurrentToolId = currentToolId,
            CurrentCommandId = currentCommandId,
            CurrentOperationElapsedMilliseconds = elapsedMilliseconds,
            ChangedFileCount = _changedFiles.Count,
            PatchByteCount = _patchByteCount,
            SubjectHash = subjectHash,
            ContextUsagePercent = usagePercent,
            ContextHeadroomPercent = usagePercent.HasValue ? 100d - usagePercent.Value : null,
            SecondsSinceMeaningfulProgress = _detector.SecondsSinceMeaningfulProgress,
            WarningCategory = warningCategory,
            WarningMessage = warningMessage
        });
    }

    private long ElapsedSince(long startedAtUtc) =>
        startedAtUtc == 0 ? 0 : Math.Max(0, Now - startedAtUtc);

    private long Now => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
}
