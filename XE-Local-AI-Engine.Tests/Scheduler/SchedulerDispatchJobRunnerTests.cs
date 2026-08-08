namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Tests for <see cref="SchedulerDispatchJobRunner" /> — the Quartz <c>IJob</c> → executor hop. It must forward EVERY
///     whitelisted per-fire override key (model-fit use-case AND breadth limit) from the merged data map to the executor;
///     a regression here silently drops an override so the run falls back to the baked parameter (the limit-override bug
///     caught in live verification 2026-06-03). A cron / no-override fire forwards a null override map.
/// </summary>
public sealed class SchedulerDispatchJobRunnerTests
{
    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(year: 2026, month: 6, day: 3, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    [Test]
    public async Task RunAsync_WhenFireCarriesUseCaseAndLimit_ForwardsBothWhitelistedOverrides()
    {
        var executor = Substitute.For<ISchedulerDispatchExecutor>();
        var context = BuildContext(new JobDataMap
        {
            [SchedulerJobKeys.ScheduledJobIdKey] = JobId.ToString(),
            [SchedulerJobKeys.ModelFitUseCaseOverrideKey] = "general",
            [SchedulerJobKeys.ModelFitLimitOverrideKey] = "20"
        });

        await SchedulerDispatchJobRunner.RunAsync(executor, NullLogger.Instance, context);

        await executor.Received(1).DispatchAsync(JobId,
            "fire-x",
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(overrides =>
                overrides != null
                && overrides.Count == 2
                && overrides[SchedulerJobKeys.ModelFitUseCaseOverrideKey] == "general"
                && overrides[SchedulerJobKeys.ModelFitLimitOverrideKey] == "20"));
    }

    [Test]
    public async Task RunAsync_WhenFireCarriesOnlyLimit_ForwardsLimitOverride()
    {
        var executor = Substitute.For<ISchedulerDispatchExecutor>();
        var context = BuildContext(new JobDataMap
        {
            [SchedulerJobKeys.ScheduledJobIdKey] = JobId.ToString(),
            [SchedulerJobKeys.ModelFitLimitOverrideKey] = "15"
        });

        await SchedulerDispatchJobRunner.RunAsync(executor, NullLogger.Instance, context);

        await executor.Received(1).DispatchAsync(JobId,
            "fire-x",
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(overrides =>
                overrides != null
                && overrides.Count == 1
                && overrides[SchedulerJobKeys.ModelFitLimitOverrideKey] == "15"));
    }

    [Test]
    public async Task RunAsync_WhenNoOverrides_ForwardsNullOverrideMap()
    {
        var executor = Substitute.For<ISchedulerDispatchExecutor>();
        var context = BuildContext(new JobDataMap
        {
            [SchedulerJobKeys.ScheduledJobIdKey] = JobId.ToString()
        });

        await SchedulerDispatchJobRunner.RunAsync(executor, NullLogger.Instance, context);

        await executor.Received(1).DispatchAsync(JobId,
            "fire-x",
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(overrides => overrides == null));
    }

    [Test]
    public async Task RunAsync_WhenScheduledJobIdMissing_DoesNotDispatch()
    {
        var executor = Substitute.For<ISchedulerDispatchExecutor>();
        var context = BuildContext(new JobDataMap());

        await SchedulerDispatchJobRunner.RunAsync(executor, NullLogger.Instance, context);

        await executor.DidNotReceive().DispatchAsync(Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    private static IJobExecutionContext BuildContext(JobDataMap mergedDataMap)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(mergedDataMap);
        context.FireInstanceId.Returns("fire-x");
        context.ScheduledFireTimeUtc.Returns(Now);
        context.FireTimeUtc.Returns(Now);
        context.CancellationToken.Returns(CancellationToken.None);
        // JobDetail is only read on the missing-id warning path; supply a key so that path does not NRE.
        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey("test-job", SchedulerJobKeys.Group));
        context.JobDetail.Returns(jobDetail);
        return context;
    }
}
