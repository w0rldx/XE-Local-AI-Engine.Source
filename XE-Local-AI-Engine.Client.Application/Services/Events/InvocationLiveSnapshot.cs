namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>Clones read in one lock acquisition: the invocation the monitor shows as current, and every live invocation.</summary>
public sealed class InvocationLiveSnapshot
{
    public required InvocationState? Current { get; init; }

    public required IReadOnlyList<InvocationState> Live { get; init; }
}
