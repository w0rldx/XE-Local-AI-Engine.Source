namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

/// <summary>Who a verified bridge token belongs to: the application instance the engine issued it to at install.</summary>
public sealed class ContainerBridgeCaller
{
    /// <summary>The external application instance the presented token was minted for.</summary>
    public required Guid InstanceId { get; init; }
}

/// <summary>Turns a presented bridge token into the caller it identifies, or into nothing.</summary>
/// <remarks>
///     The seam exists so the bridge never references External Apps: tokens are per-instance and minted at install,
///     which is that feature's business, while who is allowed onto the bridge listener is this one's. An
///     architecture test holds the direction, because nothing in the type system does.
/// </remarks>
public interface IContainerBridgeTokenVerifier
{
    /// <summary>The caller when the token is well formed, names an installed instance and matches that instance's stored secret; <see langword="null" /> otherwise.</summary>
    /// <remarks>
    ///     Every failure returns the same nothing: a malformed token, an unknown instance and a wrong secret must be
    ///     indistinguishable to whoever presented it.
    /// </remarks>
    Task<ContainerBridgeCaller?> VerifyAsync(string presentedToken, CancellationToken cancellationToken = default);
}
