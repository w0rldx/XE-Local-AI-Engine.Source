namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Change tick for runtime residency: raised by the llama-server, image and whisper supervisors and activity gates
///     whenever a value the running-models or runtime-residents read returns has changed.
/// </summary>
/// <remarks>
///     A tick, never a payload: the reads stay the only source of the rows. Raised AFTER the change is observable through
///     them, from supervisor hot paths and lease releases, so an implementation must neither block nor throw.
/// </remarks>
public interface IRuntimeResidencyChangeNotifier
{
    /// <summary>Records that residency changed. Fire-and-forget: returns at once and never throws.</summary>
    void NotifyChanged();
}
