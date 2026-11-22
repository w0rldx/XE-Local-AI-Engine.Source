namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>An in-memory marker store a test can observe, which can park the provider while it completes a marker with a group id.</summary>
/// <remarks>That call sits between launching the child and registering it, so a test can kill the sandbox in exactly that window.</remarks>
internal sealed class GatedMarkerStore : ISandboxMarkerStore, IDisposable
{
    public void Dispose() =>
        ReleaseCompletion.Dispose();

    private readonly Dictionary<string, SandboxProcessMarker> _live = [];
    private readonly Lock _sync = new();

    /// <summary>Whether completing a marker with a group id waits for <see cref="ReleaseCompletion" />.</summary>
    public bool GateCompletion { get; init; }

    /// <summary>Completed with the marker once one carrying a group id was recorded (after any gate).</summary>
    public TaskCompletionSource<SandboxProcessMarker> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when the provider reached the gate.</summary>
    public TaskCompletionSource CompletionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualResetEventSlim ReleaseCompletion { get; } = new();

    public IReadOnlyList<SandboxProcessMarker> Live
    {
        get
        {
            lock (_sync)
            {
                return [.. _live.Values];
            }
        }
    }

    public string? Write(SandboxProcessMarker marker)
    {
        var id = "marker-" + Guid.NewGuid().ToString("N");
        Record(id, marker);
        return id;
    }

    public void Update(string markerId, SandboxProcessMarker marker) =>
        Record(markerId, marker);

    public void Delete(string markerId)
    {
        lock (_sync)
        {
            _ = _live.Remove(markerId);
        }
    }

    public IReadOnlyList<SandboxMarkerEntry> ReadAll()
    {
        lock (_sync)
        {
            return [.. _live.Select(static entry => new SandboxMarkerEntry(entry.Key, entry.Value))];
        }
    }

    private void Record(string markerId, SandboxProcessMarker marker)
    {
        if (marker.ProcessGroupId is not null && GateCompletion)
        {
            _ = CompletionEntered.TrySetResult();
            _ = ReleaseCompletion.Wait(TestBudgets.Contended);
        }

        lock (_sync)
        {
            _live[markerId] = marker;
        }

        if (marker.ProcessGroupId is not null)
        {
            _ = Completed.TrySetResult(marker);
        }
    }
}
