namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using System.Diagnostics;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     Starts server-side per-application capture for a session that is already live. The audio never crosses
///     SignalR: the browser picks the process and the node does the rest. Operator-gated.
/// </summary>
/// <remarks>
///     <para>
///         The call order is create session, subscribe on the hub, <c>POST …/live/start</c>, then this. This
///         endpoint verifies the third step happened by asking the registry, because starting a recorder for a
///         session with no lanes would capture audio with nowhere to put it.
///     </para>
///     <para>
///         400 when the host cannot capture process audio at all, because retrying can never work. 409 when the
///         session is not live or already has a capture, because the caller can fix either and try again. Both
///         carry a reason code rather than prose, in the same shape the runtime routes use.
///     </para>
/// </remarks>
public sealed class StartProcessCaptureEndpoint(ProcessAudioCaptureCoordinator captures)
    : Endpoint<StartProcessCaptureRequest, ProcessCaptureStatusResponse>
{
    /// <summary>This host has no process loopback; the SPA hides the source rather than offering a retry.</summary>
    internal const string NotSupportedReason = "capture-not-supported";

    /// <summary>The live start has not run, or the session already ended.</summary>
    internal const string SessionNotLiveReason = "session-not-live";

    /// <summary>This session already has a capture; stop it before starting another.</summary>
    internal const string AlreadyCapturingReason = "capture-already-running";

    private readonly ProcessAudioCaptureCoordinator _captures = captures ?? throw new ArgumentNullException(nameof(captures));

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.SessionProcessCapture);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ProcessCaptureStatusResponse>(StatusCodes.Status200OK)
                               .Produces<ProcessCaptureBlockedResponse>(StatusCodes.Status400BadRequest)
                               .Produces<ProcessCaptureBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartProcessCaptureRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var outcome = _captures.Start(req.SessionId, req.ProcessId);
        switch (outcome)
        {
            case StartProcessCaptureOutcome.Started:
                await Send.OkAsync(new ProcessCaptureStatusResponse
                          {
                              SessionId = req.SessionId,
                              Capturing = true
                          }, ct)
                          .ConfigureAwait(false);
                return;

            case StartProcessCaptureOutcome.NotSupported:
                await Send.ResultAsync(Results.BadRequest(new ProcessCaptureBlockedResponse
                {
                    Reason = NotSupportedReason,
                    Message = TranscriptionProcessCaptureNotSupportedException.DefaultMessage
                })).ConfigureAwait(false);
                return;

            case StartProcessCaptureOutcome.SessionNotLive:
                await Send.ResultAsync(Results.Conflict(new ProcessCaptureBlockedResponse
                {
                    Reason = SessionNotLiveReason,
                    Message = "The session is not live. Start live capture for it before attaching a process."
                })).ConfigureAwait(false);
                return;

            case StartProcessCaptureOutcome.AlreadyCapturing:
                await Send.ResultAsync(Results.Conflict(new ProcessCaptureBlockedResponse
                {
                    Reason = AlreadyCapturingReason,
                    Message = "This session is already capturing an application. Stop that capture before starting another."
                })).ConfigureAwait(false);
                return;

            default:
                // Not ArgumentOutOfRangeException: the offending value is a local this method produced, not one of
                // its parameters, and CA2208 / S3928 / MA0015 all reject naming a local as the paramName.
                throw new UnreachableException($"Unknown process-capture outcome '{outcome}'.");
        }
    }
}
