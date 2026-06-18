namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelRecommendationScheduleSeeder" /> tests (plan §12 row
///     <c>ScheduleSeeder_SeedsManualJob_WithNewParams</c>): the startup seeder idempotently seeds ONE enabled Manual
///     <c>model-recommendation-check</c> schedule whose parameters carry the new advisor schema (no approved-image /
///     provider fields), and re-runs never duplicate it when a definition already exists.
/// </summary>
public sealed class ModelRecommendationScheduleSeederTests
{
    [Test]
    public async Task ScheduleSeeder_SeedsManualJob_WithNewParams()
    {
        var management = Substitute.For<IScheduledJobManagementService>();
        management.ListJobsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<ScheduledJobDefinitionRecord>>([]));
        ScheduledJobManagementInput? captured = null;
        management.CreateJobAsync(Arg.Do<ScheduledJobManagementInput>(input => captured = input), Arg.Any<CancellationToken>())
                  .Returns(callInfo => Task.FromResult(Definition((ScheduledJobManagementInput)callInfo[0])));

        var seeder = BuildSeeder(management);
        await seeder.StartAsync(CancellationToken.None);

        await management.Received(1).CreateJobAsync(Arg.Any<ScheduledJobManagementInput>(), Arg.Any<CancellationToken>());
        AssertEx.NotNull(captured);
        AssertEx.Equal(ModelRecommendationCheckHandler.TemplateIdValue, captured!.TemplateId);
        AssertEx.Equal(ScheduleKind.Manual, captured.ScheduleKind);

        var parameters = captured.Parameters!;
        AssertEx.False(parameters.Contains("approvedImageId", StringComparison.Ordinal), "seed params must carry no approved-image field.");
        AssertEx.False(parameters.Contains("providerName", StringComparison.Ordinal), "seed params must carry no provider field.");
        AssertEx.Contains(parameters, "Recommend");
        AssertEx.Contains(parameters, "coding");
    }

    [Test]
    public async Task ScheduleSeeder_WhenDefinitionExists_DoesNotReseed()
    {
        var management = Substitute.For<IScheduledJobManagementService>();
        management.ListJobsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<ScheduledJobDefinitionRecord>>(
                  [
                      Definition(new ScheduledJobManagementInput(ModelRecommendationCheckHandler.TemplateIdValue,
                          "existing", null, ScheduleKind.Manual, null, null, null, null, null, "UTC",
                          SchedulerMisfirePolicy.SkipMissed, true, 600, """{"operation":"Recommend"}"""))
                  ]));

        var seeder = BuildSeeder(management);
        await seeder.StartAsync(CancellationToken.None);

        await management.DidNotReceive().CreateJobAsync(Arg.Any<ScheduledJobManagementInput>(), Arg.Any<CancellationToken>());
    }

    private static ModelRecommendationScheduleSeeder BuildSeeder(IScheduledJobManagementService management)
    {
        var services = new ServiceCollection();
        services.AddSingleton(management);
        var provider = services.BuildServiceProvider();

        return new ModelRecommendationScheduleSeeder(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ModelRecommendationScheduleSeeder>.Instance);
    }

    private static ScheduledJobDefinitionRecord Definition(ScheduledJobManagementInput input)
    {
        return new ScheduledJobDefinitionRecord(Id: Guid.NewGuid(),
            TemplateId: input.TemplateId,
            DisplayName: input.DisplayName,
            Description: input.Description,
            Enabled: true,
            ScheduleKind: input.ScheduleKind,
            CronExpression: input.CronExpression,
            IntervalSeconds: input.IntervalSeconds,
            RepeatCount: input.RepeatCount,
            StartAtUtc: input.StartAtUtc,
            EndAtUtc: input.EndAtUtc,
            TimeZoneId: input.TimeZoneId,
            MisfirePolicy: input.MisfirePolicy,
            PreventOverlap: input.PreventOverlap,
            MaxRuntimeSeconds: input.MaxRuntimeSeconds,
            ParameterJson: input.Parameters,
            CreatedBy: ScheduledJobCreator.System,
            CreatedAtUtc: 0L,
            UpdatedAtUtc: 0L,
            DisabledAtUtc: null,
            DeletedAtUtc: null);
    }
}
