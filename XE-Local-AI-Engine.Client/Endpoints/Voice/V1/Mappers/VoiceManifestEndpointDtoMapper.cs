namespace XE_Local_AI_Engine.Client.Endpoints.Voice.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Voice;

internal static class VoiceManifestEndpointDtoMapper
{
    public static VoiceManifestResponse ToResponse(this VoiceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new VoiceManifestResponse
        {
            Enabled = manifest.Enabled,
            Models = manifest.Models.Select(ToDto).ToList(),
            Voices = manifest.Voices.Select(ToDto).ToList(),
            DefaultVoiceId = manifest.DefaultVoiceId,
            RemoteFallback = manifest.RemoteFallback is null
                ? null
                : new VoiceManifestRemoteFallbackDto
                {
                    Enabled = manifest.RemoteFallback.Enabled,
                    Endpoint = manifest.RemoteFallback.Endpoint
                }
        };
    }

    private static VoiceManifestModelDto ToDto(VoiceModel model)
    {
        return new VoiceManifestModelDto
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            Language = model.Language,
            Version = model.Version,
            Files = model.Files.Select(ToDto).ToList()
        };
    }

    private static VoiceManifestModelFileDto ToDto(VoiceModelFile file)
    {
        return new VoiceManifestModelFileDto
        {
            Dtype = file.Dtype,
            File = file.File,
            ByteSize = file.ByteSize,
            Sha256 = file.Sha256,
            DownloadUrl = file.DownloadUrl
        };
    }

    private static VoiceManifestVoiceDto ToDto(VoiceProfile voice)
    {
        return new VoiceManifestVoiceDto
        {
            Id = voice.Id,
            Name = voice.Name,
            Language = voice.Language,
            Gender = voice.Gender
        };
    }
}
