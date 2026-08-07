namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Authoring shape of a user-defined custom tool. <see cref="HttpFetch" /> performs a guarded HTTP/API request;
///     <see cref="Command" /> launches a fixed host executable. The frontend "program launch" affordance is not a kind
///     of its own — it is a path-picker that populates a <see cref="Command" /> tool's executable, so the backend and
///     executor treat it identically to <see cref="Command" />.
/// </summary>
public enum CustomToolKind
{
    HttpFetch = 0,
    Command = 1
}
