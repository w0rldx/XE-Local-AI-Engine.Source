namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Validates a per-application capture request. The only field to check is the process id: WASAPI's
///     <c>WithProcessLoopback</c> takes a <c>uint</c>, so a zero or negative id is not a process that could ever be
///     captured and is rejected before a recorder is built.
/// </summary>
public sealed class StartProcessCaptureRequestValidator : Validator<StartProcessCaptureRequest>
{
    public StartProcessCaptureRequestValidator() =>
        RuleFor(request => request.ProcessId)
            .GreaterThan(0)
            .WithMessage("The process id must be a positive number.");
}
