namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>The engine-created bridge network one application instance runs on: one per instance, labelled with the owner, the install and the instance id.</summary>
/// <remarks>
///     The labels are not bookkeeping. A name conflict is resolved by inspecting the existing network and reusing it
///     only when its labels prove it is this instance's own, so a crash between create and record does not leave the
///     next attempt attaching an application to a network somebody else owns.
/// </remarks>
public sealed record ContainerNetworkSpecification
{
    /// <summary>The engine-generated network name.</summary>
    public required string Name { get; init; }

    /// <summary>The engine-owned labels that make ownership provable on a name conflict.</summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    /// <summary>Whether the network is cut off from the outside world; always <see langword="false" /> in V1.</summary>
    /// <remarks>
    ///     ADR 0010 states outbound access as a disclosed limitation rather than an enforced one, because the
    ///     applications this catalog ships update themselves, fetch models and call third-party APIs.
    /// </remarks>
    public required bool Internal { get; init; }
}
