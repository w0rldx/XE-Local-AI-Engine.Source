namespace XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     Marks a FastEndpoints endpoint that must only be registered in desktop self-update mode. The
///     <c>UseFastEndpoints</c> endpoint filter excludes every type implementing this marker when the host is not in
///     desktop mode, so off the desktop flag these routes are entirely absent (a request 404s) rather than throwing a 500
///     for a missing service. Mirrors the invariant that the app-update + GitHub-auth surface is desktop-mode only.
/// </summary>
public interface IDesktopOnlyEndpoint;
