namespace XE_Local_AI_Engine.Tests.ModelFit;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker 4 <see cref="ModelFitRefreshTrigger" /> tests: the template-guarded facade rejects a non-existent job and a
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

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => trigger.TriggerRecommendationRefreshAsync(jobId, CancellationToken.None));

        await management.DidNotReceive().TriggerNowAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TriggerRecommendationRefreshAsync_WhenJobIsDifferentTemplate_ThrowsAndDoesNotTrigger()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, "some-other-template"));
        var trigger = new ModelFitRefreshTrigger(management);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => trigger.TriggerRecommendationRefreshAsync(jobId, CancellationToken.None));

        await management.DidNotReceive().TriggerNowAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TriggerRecommendationRefreshAsync_WhenModelRecommendationCheckJob_DelegatesOnce()
    {
        var jobId = Guid.NewGuid();
        var management = Substitute.For<IScheduledJobManagementService>();
        management.GetJobAsync(jobId, Arg.Any<CancellationToken>())
                  .Returns(JobWithTemplate(jobId, ModelRecommendationCheckHandler.TemplateIdValue));
        var trigger = new ModelFitRefreshTrigger(management);

        await trigger.TriggerRecommendationRefreshAsync(jobId, CancellationToken.None);

        await management.Received(1).TriggerNowAsync(jobId, Arg.Any<CancellationToken>());
    }

    private static ScheduledJobDefinitionRecord JobWithTemplate(Guid id, string templateId) =>
        new(
            Id: id,
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
