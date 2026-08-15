namespace XE_Local_AI_Engine.Tests.Drafting;

using NSubstitute;
using XE_Local_AI_Engine.AI.Contracts.Enums;
using XE_Local_AI_Engine.Client.Services.Drafting.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The draft slot itself: one draft at a time, none while an invocation is in flight, and a lease that releases the
///     slot exactly once no matter how often it is disposed (a double release would hand out two concurrent slots).
/// </summary>
public sealed class DraftAdmissionGateTests
{
    [Test]
    public void TryAcquire_WhenIdle_AdmitsOneDraftAtATime()
    {
        using var gate = CreateGate(out _, out _);

#pragma warning disable CA2000 // Holding the lease across the refusal IS the assertion; every lease is disposed below.
        AssertEx.True(gate.TryAcquire(out var first), "An idle node must admit the first draft.");
        AssertEx.False(gate.TryAcquire(out var second), "A second concurrent draft must be refused, not queued.");
#pragma warning restore CA2000
        AssertEx.Null(second);

        first?.Dispose();
        AssertEx.True(gate.TryAcquire(out var third), "The slot must be reusable once the first draft releases it.");
        third?.Dispose();
    }

    [Test]
    public void TryAcquire_WhenDisposedTwice_ReleasesSlotOnce()
    {
        using var gate = CreateGate(out _, out _);

        AssertEx.True(gate.TryAcquire(out var lease));
        lease?.Dispose();
        lease?.Dispose();

        AssertEx.True(gate.TryAcquire(out var next), "A double dispose must not leave the slot unusable...");
        AssertEx.False(gate.TryAcquire(out _), "...nor hand out a second concurrent slot.");
        next?.Dispose();
    }

    [Test]
    [Arguments(InvocationStatus.Completed)]
    [Arguments(InvocationStatus.Failed)]
    [Arguments(InvocationStatus.Cancelled)]
    public void TryAcquire_WhenCurrentInvocationIsTerminal_Admits(InvocationStatus status)
    {
        // The dispatcher keeps the LAST invocation around after it finishes (live-verified: Status=Completed minutes
        // after the chat turn ended). A terminal leftover must not read as busy, or drafting is refused 409 forever
        // after the first chat turn of the process lifetime.
        using var gate = CreateGate(out var dispatcher, out _);
        dispatcher.CurrentInvocation.Returns(new InvocationState { Status = status });

        AssertEx.True(gate.TryAcquire(out var lease), "A terminal leftover invocation must not refuse the draft.");
        lease?.Dispose();
    }

    [Test]
    public void TryAcquire_WhenInvocationInFlight_RefusesAndKeepsSlotFree()
    {
        using var gate = CreateGate(out var dispatcher, out var runner);
        dispatcher.CurrentInvocation.Returns(new InvocationState { Status = InvocationStatus.Running });

        AssertEx.False(gate.TryAcquire(out _), "A live invocation must refuse the draft.");

        dispatcher.CurrentInvocation.Returns((InvocationState?)null);
        runner.ActiveInvocationCount.Returns(1);
        AssertEx.False(gate.TryAcquire(out _), "A non-zero active invocation count must refuse the draft.");

        // The refusals must have handed the semaphore back, or the gate would be wedged once the node goes idle.
        runner.ActiveInvocationCount.Returns(0);
        AssertEx.True(gate.TryAcquire(out var lease), "A refusal must not leak the slot.");
        lease?.Dispose();
    }

    private static DraftAdmissionGate CreateGate(out IWorkerEventDispatcher dispatcher, out IInvocationRunner runner)
    {
        dispatcher = Substitute.For<IWorkerEventDispatcher>();
        runner = Substitute.For<IInvocationRunner>();
        return new DraftAdmissionGate(dispatcher, runner);
    }
}
