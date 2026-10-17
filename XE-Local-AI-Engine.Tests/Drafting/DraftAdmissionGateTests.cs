namespace XE_Local_AI_Engine.Tests.Drafting;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Drafting.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The draft slot itself: one draft at a time, none while an invocation holds the slot, and a lease that releases the
///     slot exactly once no matter how often it is disposed (a double release would hand out two concurrent slots).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DraftAdmissionGateTests
{
    [Test]
    public void TryAcquire_WhenIdle_AdmitsOneDraftAtATime()
    {
        using var gate = CreateGate(out _);

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
        using var gate = CreateGate(out _);

        AssertEx.True(gate.TryAcquire(out var lease));
        lease?.Dispose();
        lease?.Dispose();

        AssertEx.True(gate.TryAcquire(out var next), "A double dispose must not leave the slot unusable...");
        AssertEx.False(gate.TryAcquire(out _), "...nor hand out a second concurrent slot.");
        next?.Dispose();
    }

    [Test]
    [Arguments(InvocationStatus.Completed)]
    [Arguments(InvocationStatus.Running)]
    public void TryAcquire_WhenNoInvocationHoldsTheSlot_AdmitsWhateverTheLastInvocationShows(InvocationStatus status)
    {
        // CurrentInvocation is the LAST invocation, kept after it finishes; only the slot holder means busy, or drafting would 409 forever
        // after the first turn.
        using var gate = CreateGate(out var dispatcher);
        dispatcher.CurrentInvocation.Returns(new InvocationState
        {
            Status = status
        });
        dispatcher.SlotHolder.Returns((InvocationState?)null);

        AssertEx.True(gate.TryAcquire(out var lease), "No slot holder must admit the draft.");
        lease?.Dispose();
    }

    [Test]
    public void TryAcquire_WhileAnInvocationHoldsTheSlot_RefusesAndKeepsTheDraftSlotFree()
    {
        using var gate = CreateGate(out var dispatcher);
        dispatcher.SlotHolder.Returns(new InvocationState
        {
            Status = InvocationStatus.Running
        });

        AssertEx.False(gate.TryAcquire(out _), "A slot holder must refuse the draft.");

        // The refusal must have handed the semaphore back, or the gate would be wedged once the node goes idle.
        dispatcher.SlotHolder.Returns((InvocationState?)null);
        AssertEx.True(gate.TryAcquire(out var lease), "A refusal must not leak the slot.");
        lease?.Dispose();
    }

    private static DraftAdmissionGate CreateGate(out IWorkerEventDispatcher dispatcher)
    {
        dispatcher = Substitute.For<IWorkerEventDispatcher>();
        return new DraftAdmissionGate(dispatcher);
    }
}
