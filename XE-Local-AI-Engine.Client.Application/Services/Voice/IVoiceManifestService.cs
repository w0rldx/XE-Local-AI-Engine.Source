namespace XE_Local_AI_Engine.Client.Services.Voice;

/// <summary>
///     Composes the config-only <see cref="VoiceManifest" /> from the node settings store and the static Kokoro
///     voice/model catalog. Generates no audio and performs no synthesis — it only assembles the manifest the client
///     AI runtime reads to decide what it may download and which voices it may offer.
/// </summary>
public interface IVoiceManifestService
{
    /// <summary>
    ///     Builds the current voice manifest: the master flag, the allowed models (catalog ∩ node allow-list), the
    ///     selectable voice profiles, and the default voice id.
    /// </summary>
    Task<VoiceManifest> GetManifestAsync(CancellationToken cancellationToken = default);
}
