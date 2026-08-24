namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation.Orchestration;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The orchestration/spawn path bakes its reasoning into the participant agent at construction (it never receives
///     per-run <c>RunOptions</c>), so the thinking budget has to be mirrored here exactly as the single-agent factory
///     sets it — otherwise a workflow participant is the one caller that still free-runs its reasoning until the context
///     window is exhausted.
/// </summary>
public sealed class ParticipantReasoningOptionsTests
{
    // The stable half of ReasoningBudgetSkipLog's message, so the assertions select the skip notice by content.
    private const string BudgetSkipMessageFragment = "cannot enforce a per-request thinking budget";

    [Test]
    [Arguments("low", 2048)]
    [Arguments("medium", 8192)]
    [Arguments("high", 24576)]
    public void Build_ThinkingCapableWithGradedEffort_CarriesTheMappedReasoningBudget(string effort, int expectedBudget)
    {
        var properties = ParticipantReasoningOptions.Build(effort, supportsThinking: true);

        AssertEx.True(properties.TryGetValue<int>(ParticipantReasoningOptions.LlamaReasoningBudgetMarkerKey, out var budget));
        AssertEx.Equal(expectedBudget, budget);
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("none", true)]
    [Arguments("high", false)]
    public void Build_WithoutGradedEffortOrThinkingCapability_OmitsTheReasoningBudget(string? effort, bool supportsThinking)
    {
        var properties = ParticipantReasoningOptions.Build(effort, supportsThinking);

        AssertEx.False(properties.ContainsKey(ParticipantReasoningOptions.LlamaReasoningBudgetMarkerKey));
    }

    /// <summary>
    ///     A participant pinned to a model llama.cpp cannot enforce a thinking budget on gets NO budget marker — the
    ///     server would accept <c>reasoning_budget_tokens</c> and then ignore it, so sending it would claim a cap that
    ///     never fires. The graded <c>think</c> option itself is untouched: enforceability governs the cap, never
    ///     whether the participant reasons.
    /// </summary>
    [Test]
    public void Build_ThinkingCapableButBudgetNotEnforceable_OmitsTheBudgetAndKeepsTheThinkOption()
    {
        var logger = new RecordingLogger<ParticipantReasoningOptionsTests>();

        var properties = ParticipantReasoningOptions.Build("high",
            supportsThinking: true,
            reasoningBudgetEnforceable: false,
            logger,
            "participant-options-unenforceable-model-a");

        AssertEx.False(properties.ContainsKey(ParticipantReasoningOptions.LlamaReasoningBudgetMarkerKey),
            "an unenforceable budget must be omitted, not sent and silently ignored");
        AssertEx.True(properties.TryGetValue<string>("think", out var think), "the graded think option is unaffected");
        AssertEx.Equal("high", think);
    }

    /// <summary>
    ///     The skip is a per-turn decision but the notice is a per-MODEL fact, so a participant rebuilt on every hop
    ///     must not re-log it. The model id below is unique to this test because the de-duplication memory is
    ///     process-wide (see <c>ReasoningBudgetSkipLog</c>).
    /// </summary>
    [Test]
    public void Build_UnenforceableBudget_ReportsTheSkipOncePerModel()
    {
        var logger = new RecordingLogger<ParticipantReasoningOptionsTests>();
        const string modelId = "participant-options-unenforceable-model-b";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            _ = ParticipantReasoningOptions.Build("medium", supportsThinking: true, reasoningBudgetEnforceable: false, logger, modelId);
        }

        var skipNotices = logger.Entries
                                .Where(entry => entry.Level == LogLevel.Information
                                                && entry.Message.Contains(BudgetSkipMessageFragment, StringComparison.Ordinal))
                                .ToArray();
        AssertEx.Equal(expected: 1, skipNotices.Length);
        AssertEx.Contains(skipNotices[0].Message, modelId);
    }

    /// <summary>
    ///     The enforceable path is the status quo and must stay byte-identical: the budget is present and nothing is
    ///     logged, even with a logger supplied.
    /// </summary>
    [Test]
    public void Build_EnforceableBudget_CarriesTheBudgetAndLogsNothing()
    {
        var logger = new RecordingLogger<ParticipantReasoningOptionsTests>();

        var properties = ParticipantReasoningOptions.Build("low",
            supportsThinking: true,
            reasoningBudgetEnforceable: true,
            logger,
            "participant-options-enforceable-model");

        AssertEx.True(properties.TryGetValue<int>(ParticipantReasoningOptions.LlamaReasoningBudgetMarkerKey, out var budget));
        AssertEx.Equal(expected: 2048, budget);
        AssertEx.False(logger.Entries.Any(entry => entry.Message.Contains(BudgetSkipMessageFragment, StringComparison.Ordinal)),
            "an enforceable budget is the status quo and must report nothing");
    }
}
