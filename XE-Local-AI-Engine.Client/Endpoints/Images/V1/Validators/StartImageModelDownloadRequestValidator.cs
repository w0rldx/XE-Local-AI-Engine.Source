namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>Judges the wire shape of a file-set download, then the shape of the file-set itself.</summary>
/// <remarks>
///     Both rules already lived in the handler and both are pure, so the handler now maps the same values a second
///     time rather than carrying a verdict across. The order matters: a blank name is reported ahead of a file-set
///     that has no diffusion part, because the wire values must parse before a set can be read at all.
/// </remarks>
public sealed class StartImageModelDownloadRequestValidator : Validator<StartImageModelDownloadRequest>
{
    public StartImageModelDownloadRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
        {
            var wire = StartImageModelDownloadWireValidator.Validate(request);
            return wire.IsValid
                ? ImageModelFileSetRules.Validate(StartImageModelDownloadRequestMapper.ToServiceRequest(wire.Values!).Parts)
                : wire.Error!;
        });
    }
}
