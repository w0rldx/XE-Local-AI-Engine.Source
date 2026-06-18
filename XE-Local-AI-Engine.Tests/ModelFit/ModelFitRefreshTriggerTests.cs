namespace XE_Local_AI_Engine.Tests.ModelFit;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitRefreshTrigger" /> tests: the template-guarded facade rejects a non-existent job and a
///     job of any other template (throwing <see cref="ScheduledJobValidationException" /> WITHOUT calling
///     <c>TriggerNowAsync</c>), and delegates exactly once to <c>TriggerNowAsync</c> for a valid
///     <c>model-recommendation-check</c> job. The facade depends only on the scheduler service — it has no utility-runner
///     dependency and cannot execute llmfit.
/// </summary>
public sealed class ModelFitRefreshTriggerTests
{
    [Test]
    public async Task TriggerRecommendationRefreshAsync_WhenJobMissing_ThrowsAndDoesNotTrigger()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>()).Returns((ScheduledJobDefinitionRecord?)null);
        var trigger = new ModelFitRefreshTrigger(management);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => trigger.TriggerRecommendationRefreshAsync(jobId, cancellationToken: CancellationToken.None));

        await management.DidNotReceive().TriggerNowAsync(Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TriggerRecommendationRefreshAsync_WhenJobIsDifferentTemplate_ThrowsAndDoesNotTrigger()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, "some-other-template"));
        var trigger = new ModelFitRefreshTrigger(management);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => trigger.TriggerRecommendationRefreshAsync(jobId, cancellationToken: CancellationToken.None));

        await management.DidNotReceive().TriggerNowAsync(Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TriggerRecommendationRefreshAsync_WhenModelRecommendationCheckJob_DelegatesOnce()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, ModelRecommendationCheckHandler.TemplateIdValue));
        var trigger = new ModelFitRefreshTrigger(management);

        await trigger.TriggerRecommendationRefreshAsync(jobId, cancellationToken: CancellationToken.None);

        // No use-case override supplied → the scheduler is fired with a null override map (the definition's baked
        // use-case is used unchanged), back-compat with the prior single-arg behavior.
        await management.Received(1).TriggerNowAsync(jobId,
            Arg.Is<IReadOnlyDictionary<string, string>?>(overrides => overrides == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Refresh_WhenUseCaseSupplied_TriggersWithOverride()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, ModelRecommendationCheckHandler.TemplateIdValue));
        var trigger = new ModelFitRefreshTrigger(management);

        await trigger.TriggerRecommendationRefreshAsync(jobId, "general", cancellationToken: CancellationToken.None);

        // The validated use-case rides the per-fire override map under the whitelisted key — and nothing else.
        await management.Received(1).TriggerNowAsync(jobId,
            Arg.Is<IReadOnlyDictionary<string, string>?>(overrides =>
                overrides != null
                && overrides.Count == 1
                && overrides.ContainsKey(SchedulerJobKeys.ModelFitUseCaseOverrideKey)
                && overrides[SchedulerJobKeys.ModelFitUseCaseOverrideKey] == "general"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Refresh_WhenLimitSupplied_TriggersWithBoundedLimit()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, ModelRecommendationCheckHandler.TemplateIdValue));
        var trigger = new ModelFitRefreshTrigger(management);

        await trigger.TriggerRecommendationRefreshAsync(jobId, limitOverride: 20, cancellationToken: CancellationToken.None);

        // The validated limit rides the per-fire override map under its own whitelisted key — and nothing else.
        await management.Received(1).TriggerNowAsync(jobId,
            Arg.Is<IReadOnlyDictionary<string, string>?>(overrides =>
                overrides != null
                && overrides.Count == 1
                && overrides.ContainsKey(SchedulerJobKeys.ModelFitLimitOverrideKey)
                && overrides[SchedulerJobKeys.ModelFitLimitOverrideKey] == "20"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(0)]
    [Arguments(51)]
    [Arguments(-1)]
    public async Task Refresh_WhenLimitOutOfRange_RejectsWithoutTrigger(int limit)
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, ModelRecommendationCheckHandler.TemplateIdValue));
        var trigger = new ModelFitRefreshTrigger(management);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => trigger.TriggerRecommendationRefreshAsync(jobId, limitOverride: limit, cancellationToken: CancellationToken.None));

        await management.DidNotReceive().TriggerNowAsync(Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Refresh_WhenUseCaseInvalid_RejectsWithoutTrigger()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, ModelRecommendationCheckHandler.TemplateIdValue));
        var trigger = new ModelFitRefreshTrigger(management);

        // An unknown use-case is rejected by the allowlist guard before the scheduler is ever asked to fire.
        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => trigger.TriggerRecommendationRefreshAsync(jobId, "not-a-use-case", cancellationToken: CancellationToken.None));

        await management.DidNotReceive().TriggerNowAsync(Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    private static ScheduledJobDefinitionRecord JobWithTemplate(Guid id, string templateId)
    {
        return new ScheduledJobDefinitionRecord(Id: id,
            TemplateId: templateId,
            DisplayName: "Model recommendation check",
            Description: null,
            Enabled: true,
            ScheduleKind: ScheduleKind.OneShot,
            CronExpression: null,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            TimeZoneId: "UTC",
            MisfirePolicy: SchedulerMisfirePolicy.SkipMissed,
            PreventOverlap: false,
            MaxRuntimeSeconds: 600,
            ParameterJson: null,
            CreatedBy: ScheduledJobCreator.User,
            CreatedAtUtc: 1L,
            UpdatedAtUtc: 1L,
            DisabledAtUtc: null,
            DeletedAtUtc: null);
    }
}
