namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Opens a transcription session in the <c>Created</c> state and returns it. The audio arrives afterwards, through
///     <see cref="UploadTranscriptionAudioEndpoint" /> — so the title is decided here, which is why the file flow sends
///     the chosen file's name on this call. A blank model id resolves to the node's effective model. Operator-gated.
/// </summary>
public sealed class CreateTranscriptionSessionEndpoint : Endpoint<CreateTranscriptionSessionRequest, TranscriptionSessionDetailResponse>
{
    private readonly ITranscriptionService _sessions;

    public CreateTranscriptionSessionEndpoint(ITranscriptionService sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.Sessions);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<CreateTranscriptionSessionRequest>("application/json")
                               .Produces<TranscriptionSessionDetailResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CreateTranscriptionSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var session = await _sessions.CreateSessionAsync(new CreateTranscriptionSessionInput
            {
                Title = req.Title,
                SourceKind = req.SourceKind,
                ModelId = req.ModelId,
                Config = new TranscriptionSessionConfig
                {
                    LanguageMode = req.LanguageMode,
                    LanguageOverride = req.LanguageOverride,
                    Translate = req.Translate,
                    MaxWindowSeconds = req.MaxWindowSeconds,
                    ChannelAttribution = req.ChannelAttribution
                }
            },
            ct);

        await Send.OkAsync(session.ToResponse(), ct);
    }
}
