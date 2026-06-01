namespace XE_Local_AI_Engine.Tests.Scheduler;

using Quartz;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Documents the per-JobKey overlap contract by asserting the <see cref="DisallowConcurrentExecutionAttribute" />
///     is present on <see cref="NonOverlappingSchedulerDispatchJob" /> and absent from
///     <see cref="SchedulerDispatchJob" />. A full two-concurrent-fires integration test was evaluated but skipped
///     (see summary at end of file for reasoning).
/// </summary>
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

    // SKIPPED — two-concurrent-fires integration test
    //
    // A full concurrent-fires test (schedule the same JobKey twice against the live IScheduler,
    // confirm [DisallowConcurrentExecution] causes the second fire to wait) was considered.
    // It was not written for the following reasons:
    //
    //   1. The attribute contract is already proven by the reflection tests above; the Quartz
    //      runtime behaviour of [DisallowConcurrentExecution] is the framework's own guarantee.
    //   2. A real concurrent-fire test requires the Quartz thread pool to actually overlap two
    //      executions of the same JobKey at sub-millisecond precision inside a test process,
    //      making it inherently timing-sensitive and flaky in CI.
    //   3. The persistent SQLite store (required for a live scheduler in NodeSchedulerRegistrationTests)
    //      already validates that Quartz starts up correctly; adding timed concurrent job execution
    //      on top raises the harness complexity without proportional safety gain.
    //
    // If a concurrent-overlap regression is suspected in future, a manual smoke test via the
    // full Aspire runtime (with two rapid trigger fires at the same JobKey) is the appropriate
    // verification vehicle, not a unit/integration test.
}
