namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     What a live Docker daemon says it is. <see cref="DaemonId" /> is the identity that matters: the engine
///     generates it once at first start and keeps it for the life of that installation, so it distinguishes "the same
///     daemon at a new address" from "a different daemon" — which an endpoint URI on its own cannot do in either
///     direction.
/// </summary>
/// <param name="DaemonId">The daemon's own installation id, from the system-info endpoint.</param>
/// <param name="ServerVersion">The Docker Engine version string (for example <c>29.6.1</c>).</param>
/// <param name="ApiVersion">The API version the daemon serves (for example <c>1.55</c>).</param>
/// <param name="MinimumApiVersion">The oldest API version the daemon still accepts.</param>
/// <param name="OperatingSystem">The daemon's OS type (<c>linux</c> / <c>windows</c>), which decides what a mount even means.</param>
/// <param name="Endpoint">The endpoint this identity was read through.</param>
/// <param name="IsRootless">
///     Whether the daemon reported <c>name=rootless</c> among its security options. It changes which in-container UID
///     maps to the engine's own host UID, and therefore which UID can use an engine-generated bind mount at all: a
///     rootless daemon maps container UID 0 to the invoking user and container UID <c>N&gt;0</c> to
///     <c>subuid_base + N - 1</c>, so the conventional non-root UID is a host account that owns nothing of ours.
///     Reported rather than acted on here — it is an input to the identity decision, and the probe that follows
///     container creation is what actually proves the mapping.
/// </param>
public sealed record DockerDaemonIdentity(
    string DaemonId,
    string ServerVersion,
    string ApiVersion,
    string MinimumApiVersion,
    string OperatingSystem,
    DockerDaemonEndpoint Endpoint,
    bool IsRootless = false);
