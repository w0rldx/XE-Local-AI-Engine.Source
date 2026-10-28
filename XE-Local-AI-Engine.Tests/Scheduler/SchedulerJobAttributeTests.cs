namespace XE_Local_AI_Engine.Tests.Scheduler;

using Quartz;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Documents the per-JobKey overlap contract by asserting the <see cref="DisallowConcurrentExecutionAttribute" />
///     is present on <see cref="NonOverlappingSchedulerDispatchJob" /> and absent from
///     <see cref="SchedulerDispatchJob" />. A full two-concurrent-fires integration test was evaluated but skipped
///     (see summary at end of file for reasoning).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SchedulerJobAttributeTests
{
    [Test]
    public void NonOverlappingSchedulerDispatchJob_HasDisallowConcurrentExecutionAttribute()
    {
        var hasAttribute = typeof(NonOverlappingSchedulerDispatchJob)
            .IsDefined(typeof(DisallowConcurrentExecutionAttribute), inherit: false);

        AssertEx.True(hasAttribute,
            $"{nameof(NonOverlappingSchedulerDispatchJob)} must carry [DisallowConcurrentExecution] " +
            "so Quartz serializes concurrent fires of the same JobKey.");
    }

    [Test]
    public void SchedulerDispatchJob_DoesNotHaveDisallowConcurrentExecutionAttribute()
    {
        var hasAttribute = typeof(SchedulerDispatchJob)
            .IsDefined(typeof(DisallowConcurrentExecutionAttribute), inherit: false);

        AssertEx.False(hasAttribute,
            $"{nameof(SchedulerDispatchJob)} must NOT carry [DisallowConcurrentExecution] " +
            "so concurrent fires of the same definition are allowed (PreventOverlap=false path).");
    }

    // No two-concurrent-fires test: the reflection tests above prove the attribute, Quartz guarantees its runtime
    // behavior, and overlapping two fires of one JobKey inside a test process is timing-sensitive and flaky.
}
