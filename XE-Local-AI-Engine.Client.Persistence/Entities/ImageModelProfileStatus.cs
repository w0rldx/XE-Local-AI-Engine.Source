namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Provenance of an <see cref="ImageModelProfile" />'s default generation parameters. Mirrors
///     <c>InferenceProfileStatus</c> for image models. Persisted as an <see langword="int" />.
/// </summary>
public enum ImageModelProfileStatus
{
    /// <summary>Seeded defaults that have not been tuned for this host.</summary>
    Default = 0,

    /// <summary>Operator-adjusted defaults for this model on this machine.</summary>
    Customized = 1
}
