namespace XE_Local_AI_Engine.Testing.FakeDocker;

/// <summary>
///     One network the fake daemon holds. The driver and the label map are what the production client's ownership
///     check compares, so a test seeds a "foreign" network by seeding one of these with different labels.
/// </summary>
public sealed class FakeDockerNetwork
{
    /// <summary>The 64-hex network id the fake minted at create.</summary>
    public required string Id { get; init; }

    /// <summary>The network name, which is also the key it is stored under: one name, one network.</summary>
    public required string Name { get; init; }

    /// <summary>The driver. Anything other than <c>bridge</c> fails the client's ownership check.</summary>
    public string Driver { get; set; } = "bridge";

    /// <summary>Whether the network is internal.</summary>
    public bool Internal { get; set; }

    /// <summary>The ownership labels. Every label the caller asked for must be present and equal for a reuse.</summary>
    public IDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    ///     Ids of the containers attached to this network. A real daemon refuses to remove a network that still has
    ///     endpoints, and modelling that is what makes teardown ordering observable.
    /// </summary>
    public ISet<string> AttachedContainerIds { get; } = new HashSet<string>(StringComparer.Ordinal);
}
