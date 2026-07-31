namespace XE_Local_AI_Engine.Tests.Development;

using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     The attempt lane's operator-facing reporting, which was measurably worse than the validation lane's.
///     <para>
///         Live on 2026-07-31, two consecutive coder attempts failed for two entirely different reasons — a
///         changed-file manifest mismatch and an output-budget overrun — and both were reported to the operator with
///         the identical sentence "The bounded Development coder attempt failed before producing valid exact
///         evidence", with zero artifacts persisted (evidence is only written after the attempt passes its own
///         checks). The first attempt had in fact produced the correct fix.
///     </para>
/// </summary>
public sealed class DevelopmentAttemptFailureReportingTests
{
    [Test]
    public void OutputBudget_DerivesTheWholeAttemptCeilingFromRoundsRatherThanOneCall()
    {
        AssertEx.Equal(300L, DevelopmentAttemptOutputBudget.Cumulative(maxOutputTokens: 100, providerCalls: 3));

        // The shape of the live failure: five rounds of a tool loop, each within its own per-call budget, whose sum
        // is not. The old rule failed this; the corrected one accepts it.
        var (input, output) = DevelopmentAttemptOutputBudget.Accept(reportedInputTokens: 27_038,
            reportedOutputTokens: 260,
            reportedTotalTokens: null,
            maxOutputTokens: 100,
            providerCalls: 3,
            "coder");
        AssertEx.Equal(27_038L, input);
        AssertEx.Equal(260L, output);
    }

    [Test]
    public async Task OutputBudget_RejectsAboveTheCeilingWithAnActionableTypedReason()
    {
        var failure = await AssertEx.ThrowsAsync<DevelopmentAttemptEvidenceException>(() =>
        {
            _ = DevelopmentAttemptOutputBudget.Accept(reportedInputTokens: 10,
                reportedOutputTokens: 301,
                reportedTotalTokens: null,
                maxOutputTokens: 100,
                providerCalls: 3,
                "coder");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentAttemptFailureCodes.OutputTokenBudgetExceeded, failure.FailureCode);
        AssertEx.True(failure.OperatorReason.Contains("301", StringComparison.Ordinal), "the reason must state what was produced");
        AssertEx.True(failure.OperatorReason.Contains("300", StringComparison.Ordinal), "the reason must state the budget it exceeded");
        AssertEx.True(failure.OperatorReason.Contains("maximum-tokens", StringComparison.Ordinal), "the reason must say what the operator can change");
    }

    [Test]
    public async Task OutputBudget_WhenProviderReportsNoUsage_SaysSoInsteadOfFailingGenerically()
    {
        var failure = await AssertEx.ThrowsAsync<DevelopmentAttemptEvidenceException>(() =>
        {
            _ = DevelopmentAttemptOutputBudget.Accept(reportedInputTokens: null,
                reportedOutputTokens: null,
                reportedTotalTokens: null,
                maxOutputTokens: 100,
                providerCalls: 3,
                "reviewer");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentAttemptFailureCodes.UsageNotReported, failure.FailureCode);
        AssertEx.True(failure.OperatorReason.Contains("reviewer", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The persisted <c>terminal_reason</c> column is 1024 characters, and the manifest-mismatch reason
    ///     interpolates repository paths. The composed value — code prefix included — is what gets persisted, so that
    ///     is what must be clamped; clamping the reason alone and then prefixing the code puts it back over the limit.
    /// </summary>
    [Test]
    public void EvidenceException_ClampsTheComposedTerminalReasonIncludingItsCodePrefix()
    {
        var failure = new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.ChangedFileManifestMismatch,
            new string('x', 4096));

        AssertEx.Equal(expected: 1024, failure.TerminalReason.Length);
        AssertEx.True(failure.TerminalReason.StartsWith($"[{DevelopmentAttemptFailureCodes.ChangedFileManifestMismatch}] ", StringComparison.Ordinal),
            "the code must survive the clamp, since it is the machine-readable half");
    }

    /// <summary>
    ///     A reasoning model between two tool calls emits <see cref="TextReasoningContent" />, which
    ///     <c>ChatResponseUpdate.Text</c> does not concatenate — so the live panel published nothing and froze on the
    ///     previous tool call's values while generation ran on. Measured live: 32,106 tokens decoded in one round with
    ///     the UI static for over three minutes.
    /// </summary>
    [Test]
    public void LiveProgress_PublishesAHeartbeatWhileTheModelIsReasoningOnly()
    {
        var broker = new RecordingLiveBroker();
        var time = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        var progress = new DevelopmentAttemptLiveProgress(Snapshot(),
            broker,
            Options.Create(new DevelopmentOptions()),
            time,
            maxOutputTokens: 1024,
            maxToolCalls: 16);

        progress.Output(ReasoningUpdate("thinking about the loop bound"));
        AssertEx.Equal(expected: 1, broker.Updates.Count);
        AssertEx.Equal(DevelopmentAttemptLiveUpdateKind.Progress, broker.Updates[0].Kind);

        // Bounded to the same 250 ms cadence the text path uses, so a long reason does not flood the channel.
        progress.Output(ReasoningUpdate("still thinking"));
        AssertEx.Equal(expected: 1, broker.Updates.Count);

        time.Advance(TimeSpan.FromMilliseconds(300));
        progress.Output(ReasoningUpdate("still thinking"));
        AssertEx.Equal(expected: 2, broker.Updates.Count);

        // An update carrying neither text, reasoning nor usage still publishes nothing.
        time.Advance(TimeSpan.FromSeconds(5));
        progress.Output(new ChatResponseUpdate(ChatRole.Assistant, contents: []));
        AssertEx.Equal(expected: 2, broker.Updates.Count);
    }

    private static ChatResponseUpdate ReasoningUpdate(string reasoning) =>
        new(ChatRole.Assistant, [new TextReasoningContent(reasoning)]);

    private static DevelopmentExecutionSnapshot Snapshot() =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "identity",
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            DevelopmentTrustPolicy.CurrentVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: 60,
            "Title",
            "Requirements",
            "[]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 1,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1,
            CommandProfileJson: null);

    private sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current = _current.Add(duration);
    }

    private sealed class RecordingLiveBroker : IDevelopmentAttemptLiveBroker
    {
        public List<DevelopmentAttemptLiveUpdate> Updates { get; } = [];

        public bool Register(Guid attemptId) => true;

        public bool TryPublish(DevelopmentAttemptLiveUpdate update)
        {
            Updates.Add(update);
            return true;
        }

        public bool TryGetSnapshot(Guid attemptId, out DevelopmentAttemptLiveSnapshot snapshot)
        {
            snapshot = default!;
            return false;
        }

        public bool TryGetDeliveryReader(Guid attemptId, out ChannelReader<DevelopmentAttemptLiveUpdate>? reader)
        {
            reader = null;
            return false;
        }

        public bool Complete(Guid attemptId) => true;
    }
}
