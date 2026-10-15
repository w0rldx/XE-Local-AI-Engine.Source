namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     Starts an ephemeral dictation session and returns its server-minted id. Operator-gated.
/// </summary>
/// <remarks>
///     No row exists, before or after: committed segments reach the hub and nothing else, the hub's <c>EndSession</c>
///     ends it, and the registry's attachment deadline and abandonment grace reap one the client forgot. 400 with the
///     sanitized runtime message when whisper-server could not be started. The JSON body pins the language; <c>{}</c> detects it.
/// </remarks>
public sealed class StartDictationSessionEndpoint : Endpoint<StartDictationSessionRequest, StartDictationSessionResponse>
{
    private readonly ITranscriptionService _sessions;

    public StartDictationSessionEndpoint(ITranscriptionService sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.DictationSessions);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StartDictationSessionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(StartDictationSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        DictationSessionStart started;
        try
        {
            started = await _sessions.StartDictationAsync(req.Language?.Trim(), ct);
        }
        catch (WhisperRuntimeException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(StatusCodes.Status400BadRequest, ct);
            return;
        }

        await Send.OkAsync(new StartDictationSessionResponse
            {
                SessionId = started.SessionId,
                StartedAtUtc = started.StartedAt.ToUnixTimeMilliseconds(),
                LastSeq = 0
            },
            ct);
    }
}
