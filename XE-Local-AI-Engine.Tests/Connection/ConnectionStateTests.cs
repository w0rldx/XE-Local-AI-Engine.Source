namespace XE_Local_AI_Engine.Tests.Connection;

using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ConnectionStateTests
{
    [Test]
    public void Initial_StateIsDisconnected()
    {
        var state = new ConnectionState();

        AssertEx.Equal(WorkerConnectionState.Disconnected, state.Current);
    }

    [Test]
    public void TransitionTo_Connected_UpdatesCurrent()
    {
        var state = new ConnectionState();

        state.TransitionTo(WorkerConnectionState.Connected);

        AssertEx.Equal(WorkerConnectionState.Connected, state.Current);
    }

    [Test]
    public void TransitionTo_Error_SetsLastError()
    {
        var state = new ConnectionState();

        state.TransitionTo(WorkerConnectionState.Error, "boom");

        AssertEx.Equal("boom", state.LastError);
    }

    [Test]
    public void TransitionTo_SameStateWithDifferentError_RaisesEvent()
    {
        var state = new ConnectionState();
        var eventCount = 0;
        state.StateChanged += (_, _) => eventCount++;

        state.TransitionTo(WorkerConnectionState.Connected);
        state.TransitionTo(WorkerConnectionState.Connected, "boom");

        AssertEx.Equal(expected: 2, eventCount);
    }

    [Test]
    public void TransitionTo_SameStateAndSameError_DoesNotRaiseEvent()
    {
        var state = new ConnectionState();
        var eventCount = 0;
        state.StateChanged += (_, _) => eventCount++;

        state.TransitionTo(WorkerConnectionState.Error, "boom");
        state.TransitionTo(WorkerConnectionState.Error, "boom");

        AssertEx.Equal(expected: 1, eventCount);
    }

    [Test]
    public void TransitionTo_RaisesStateChangedEvent_WithCorrectArgs()
    {
        var state = new ConnectionState();
        WorkerConnectionStateChangedEventArgs? args = null;
        state.StateChanged += (_, eventArgs) => args = eventArgs;

        state.TransitionTo(WorkerConnectionState.Connected);

        var changedArgs = AssertEx.NotNull(args);
        AssertEx.Equal(WorkerConnectionState.Disconnected, changedArgs.PreviousState);
        AssertEx.Equal(WorkerConnectionState.Connected, changedArgs.CurrentState);
    }

    [Test]
    public void TransitionTo_UpdatesLastUpdatedAt()
    {
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepTimeProvider(before: t0, after: t0.AddSeconds(1));
        var state = new ConnectionState(clock);
        var before = state.LastUpdatedAt;

        clock.Step();
        state.TransitionTo(WorkerConnectionState.Connected);

        AssertEx.True(state.LastUpdatedAt > before);
    }

    /// <summary>Returns <paramref name="before"/> until <see cref="Step"/> is called, then returns <paramref name="after"/>.</summary>
    private sealed class StepTimeProvider(DateTimeOffset before, DateTimeOffset after) : TimeProvider
    {
        private bool _stepped;

        public void Step() => _stepped = true;

        public override DateTimeOffset GetUtcNow() => _stepped ? after : before;
    }
}
