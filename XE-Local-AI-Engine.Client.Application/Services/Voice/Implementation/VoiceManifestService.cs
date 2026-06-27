namespace XE_Local_AI_Engine.Client.Services.Voice.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IVoiceManifestService" />: composes the config-only voice manifest from the node settings store
///     and the static <see cref="KokoroVoiceCatalog" />. Applies the absent-field defaults (master flag off, the bundled
///     Kokoro allow-list, the <c>af_heart</c> default voice) and intersects the catalog with the node allow-list. No audio.
/// </summary>
public sealed class VoiceManifestService(INodeSettingsStore nodeSettingsStore) : IVoiceManifestService
{
    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));

    public async Task<VoiceManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var enabled = settings.VoiceFeatureEnabled ?? StoredNodeSettings.DefaultVoiceFeatureEnabled;
        var allowedModelIds = settings.AllowedVoiceModels is { Count: > 0 } stored
            ? stored
            : StoredNodeSettings.DefaultAllowedVoiceModels;
        var defaultVoiceId = string.IsNullOrWhiteSpace(settings.DefaultVoiceProfile)
            ? StoredNodeSettings.DefaultVoiceProfileId
            : settings.DefaultVoiceProfile;

        var models = KokoroVoiceCatalog.Models
                                       .Where(model => allowedModelIds.Contains(model.Id, StringComparer.Ordinal))
                                       .ToList();

        return new VoiceManifest
        {
            Enabled = enabled,
            Models = models,
            Voices = KokoroVoiceCatalog.Voices,
            DefaultVoiceId = defaultVoiceId,

            // Remote TTS fallback is deferred (not built yet); the field is kept for forward-compat.
            RemoteFallback = null
        };
    }
}
