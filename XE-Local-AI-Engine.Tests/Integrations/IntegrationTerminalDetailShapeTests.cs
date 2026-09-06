namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using Harness = IntegrationCoordinatorHarness;

/// <summary>
///     ONE terminal detail shape, whichever path terminalized the row: <c>execution.failed</c> persists and publishes
///     <c>{category, summary}</c>, <c>execution.completed</c> <c>{tokens?, durationMs}</c>, and
///     <c>execution.cancelled</c> nothing at all.
///     <para>
///         The S4 live round found an older row whose <c>execution.failed</c> carried
///         <c>{failureCategory, failureSummary}</c> — the execution store's own fallback shape, which is what every
///         terminal wrote before the terminal payload was threaded through the command. A caller that missed the frame
///         and replayed from the poll route was then handed different keys from the ones the stream had given it.
///     </para>
///     <para>
///         This suite walks every <c>TryTerminalizeAsync</c> caller path the application can reach — the coordinator's
///         run, its pre-run rejections, its fault handler, its startup sweep, the accept path's queue-full refusal and
///         the cancel primitive — and asserts the keys each one persists. The store's own fallback is covered where it
///         lives, in <c>IntegrationExecutionStoreTests</c>.
///     </para>
/// </summary>
public sealed class IntegrationTerminalDetailShapeTests
{
    private static readonly string[] FailureKeys = ["category", "summary"];

    private static readonly string[] CompletionKeys = ["durationMs", "tokens"];

    [Test]
    public async Task CoordinatorRun_WhenTheRunCompletes_PersistsTheCompletionKeys()
    {
        using var harness = new Harness
        {
            TerminalTotalTokens = 412
        };
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Completed, harness.Row(executionId).Status);
        AssertKeys(harness, executionId, IntegrationStreamEventTypes.ExecutionCompleted, CompletionKeys);
    }

    [Test]
    public async Task CoordinatorRun_WhenTheProviderReportedNoTokens_OmitsTokensRatherThanWritingNull()
    {
        // `tokens?` is optional in the envelope, so a run whose provider reported no usage must leave the field out
        // instead of publishing a null every integrator has to special-case.
        using var harness = new Harness
        {
            TerminalTotalTokens = null
        };
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertKeys(harness, executionId, IntegrationStreamEventTypes.ExecutionCompleted, ["durationMs"]);
    }

    [Test]
    public async Task CoordinatorRun_WhenTheRunFails_PersistsTheFailureKeys()
    {
        using var harness = new Harness
        {
            TerminalStatus = InvocationStatus.Failed
        };
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Failed, harness.Row(executionId).Status);
        AssertKeys(harness, executionId, IntegrationStreamEventTypes.ExecutionFailed, FailureKeys);
    }

    [Test]
    public async Task CoordinatorPreRunRejection_PersistsTheFailureKeys()
    {
        // TerminalizeBeforeRunAsync, the Accepted|Queued -> Failed edge every pre-run rejection takes.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.DisableTrigger();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationFailureCategories.TriggerUnavailable, row.FailureCategory);
        AssertKeys(harness, executionId, IntegrationStreamEventTypes.ExecutionFailed, FailureKeys);
    }

    [Test]
    public async Task CoordinatorFaultHandler_PersistsTheFailureKeys()
    {
        // The safety net for a throw anywhere in the pipeline: it re-reads the row and terminalizes whatever
        // non-terminal status it finds, which is a DIFFERENT call into the same terminal funnel.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.DuringRun = (_, _) => throw new InvalidOperationException("the runtime broke mid-generation");

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory);
        AssertKeys(harness, executionId, IntegrationStreamEventTypes.ExecutionFailed, FailureKeys);
    }

    [Test]
    public async Task CoordinatorStartupSweep_PersistsTheFailureKeys()
    {
        // The path that produced the live row: before the payload was threaded through the command, the sweep passed
        // only the failure columns and the store's fallback wrote its own shape.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            var row = harness.Row(executionId);
            AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
            AssertEx.Equal(IntegrationFailureCategories.Restart, row.FailureCategory);
            AssertKeys(harness, executionId, IntegrationStreamEventTypes.ExecutionFailed, FailureKeys);
        }
        finally
        {
            await harness.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task CoordinatorCancelBeforeRun_PersistsNoDetailAtAll()
    {
        // `execution.cancelled` carries no payload by contract: a cancel is an outcome, not a failure. The assertion
        // is that this stays a null rather than becoming an empty object or a failure envelope with two nulls in it.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted(stopRequestedAtUtc: 4_242);

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Cancelled, harness.Row(executionId).Status);
        AssertEx.Null(Terminal(harness.Executions, executionId, IntegrationStreamEventTypes.ExecutionCancelled).DetailJson);
    }

    [Test]
    public async Task AcceptPathQueueFullRefusal_PersistsTheFailureKeys()
    {
        // The one place an accept terminalizes its own row, and the only terminal producer outside the coordinator and
        // the cancel primitive.
        var harness = new IntegrationInvokeHarness(queueCapacity: 1);
        var trigger = harness.SeedTrigger("sensor-feed");
        _ = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        var refused = await harness.AcceptAsync(trigger.Name).ConfigureAwait(false);

        AssertEx.Equal(IntegrationAcceptOutcome.QueueFull, refused.Outcome);
        var terminalized = harness.Executions.Rows.Single(row => row.Status == IntegrationExecutionStatus.Failed);
        AssertEx.Equal(IntegrationFailureCategories.QueueFull, terminalized.FailureCategory);
        AssertKeys(harness.Executions, terminalized.Id, IntegrationStreamEventTypes.ExecutionFailed, FailureKeys);
    }

    [Test]
    public async Task CancelPrimitive_PersistsNoDetailAtAll()
    {
        var executions = new FakeIntegrationExecutionStore();
        var triggers = new FakeIntegrationTriggerStore();
        var cancellations = new IntegrationCancellationRegistry();
        using var buffer = new IntegrationExecutionEventBuffer(Options.Create(new IntegrationOptions()), TimeProvider.System);
        var service = new IntegrationExecutionQueryService(executions,
            triggers,
            buffer,
            cancellations,
            TimeProvider.System,
            NullLogger<IntegrationExecutionQueryService>.Instance);

        var executionId = Guid.NewGuid();
        _ = executions.Seed(executionId, Guid.NewGuid(), Guid.NewGuid(), IntegrationExecutionStatus.Accepted);
        AssertEx.True(cancellations.TryRegister(executionId, out _));
        try
        {
            AssertEx.Equal(IntegrationCancelOutcome.Requested, await service.RequestCancelAsync(executionId));
            AssertEx.Equal(IntegrationExecutionStatus.Cancelled, executions.Rows.Single(row => row.Id == executionId).Status);
            AssertEx.Null(Terminal(executions, executionId, IntegrationStreamEventTypes.ExecutionCancelled).DetailJson);
        }
        finally
        {
            cancellations.Remove(executionId);
        }
    }

    private static void AssertKeys(Harness harness, Guid executionId, string eventType, IReadOnlyList<string> expected) =>
        AssertKeys(harness.Executions, executionId, eventType, expected);

    private static void AssertKeys(FakeIntegrationExecutionStore executions, Guid executionId, string eventType, IReadOnlyList<string> expected)
    {
        var detail = Terminal(executions, executionId, eventType).DetailJson;
        AssertEx.NotNull(detail, $"'{eventType}' has to carry a payload; a null tells an integrator nothing about how the run ended.");

        using var document = JsonDocument.Parse(detail!);
        var actual = document.RootElement.EnumerateObject().Select(static property => property.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        AssertEx.Equal(string.Join(",", expected.OrderBy(static name => name, StringComparer.Ordinal)),
            string.Join(",", actual),
            $"'{eventType}' must persist the brief's envelope, whichever path terminalized the row.");
    }

    private static IntegrationExecutionEventSnapshot Terminal(FakeIntegrationExecutionStore executions, Guid executionId, string eventType) =>
        executions.Events.Single(candidate => candidate.ExecutionId == executionId
                                              && string.Equals(candidate.EventType, eventType, StringComparison.Ordinal));
}
