namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     One engine-owned transient scope as the startup sweep sees it: its unit name, and how long it has been active.
/// </summary>
/// <remarks>
///     The age is what tells a scope a previous run abandoned from one a worker created seconds ago and has not finished registering. It
///     is <see langword="null" /> when the manager did not answer, and the sweep reads an unmeasurable age as a reason NOT to signal:
///     killing another instance's live command is unrecoverable, while leaving an orphan costs one <c>RuntimeMaxSec</c>.
/// </remarks>
internal sealed class SandboxScopeUnitStatus
{
    public required string UnitName { get; init; }

    public required TimeSpan? ActiveFor { get; init; }
}
