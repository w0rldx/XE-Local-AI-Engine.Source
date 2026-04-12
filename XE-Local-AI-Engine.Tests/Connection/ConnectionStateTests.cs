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

        AssertEx.Equal(2, eventCount);
    }

    [Test]
    public void TransitionTo_SameStateAndSameError_DoesNotRaiseEvent()
    {
        var state = new ConnectionState();
        var eventCount = 0;
        state.StateChanged += (_, _) => eventCount++;

        state.TransitionTo(WorkerConnectionState.Error, "boom");
        state.TransitionTo(WorkerConnectionState.Error, "boom");

        AssertEx.Equal(1, eventCount);
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
        var state = new ConnectionState();
        var before = state.LastUpdatedAt;

        Thread.Sleep(10);
        state.TransitionTo(WorkerConnectionState.Connected);

        AssertEx.True(state.LastUpdatedAt > before);
    }
}
