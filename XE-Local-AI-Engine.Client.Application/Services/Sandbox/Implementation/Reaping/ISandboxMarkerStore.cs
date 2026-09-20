namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>Persists <see cref="SandboxProcessMarker" /> records for the lifetime of a sandboxed process group.</summary>
/// <remarks>
///     Every member is best-effort: marker bookkeeping must never fail a sandbox command, so an unwritable or unreadable store degrades to
///     "no reaping" rather than surfacing an error into the run flow.
/// </remarks>
public interface ISandboxMarkerStore
{
    /// <summary>Records a newly launched process group. Returns the marker's identity, or <see langword="null" /> if it could not be written.</summary>
    string? Write(SandboxProcessMarker marker);

    /// <summary>Replaces the contents of an already-registered marker, keeping its identity.</summary>
    /// <remarks>
    ///     This is how a marker PRE-REGISTERED before a launch — naming the scope the launch is about to create, with no pid yet — is
    ///     completed once the child exists. Best-effort like the rest of the store.
    /// </remarks>
    void Update(string markerId, SandboxProcessMarker marker);

    /// <summary>Removes a marker after graceful teardown. A missing marker is a no-op.</summary>
    void Delete(string markerId);

    /// <summary>Reads every marker currently on disk, skipping any that cannot be parsed.</summary>
    IReadOnlyList<SandboxMarkerEntry> ReadAll();
}
