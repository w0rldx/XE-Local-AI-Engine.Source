namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1.Mappers;

/// <summary>
///     Bounds the persisted tour key and confines the status to the two the mapper parses.
/// </summary>
/// <remarks>
///     The key's two rules cascade, the status reports beside them rather than after them, and both failures stay
///     keyed to their property — the shape the handler's own <c>AddError(r =&gt; …)</c> pair produced. The key rule is
///     <c>Must</c>, not <c>NotEmpty</c>, because the OpenAPI schema processor reads a <c>NotEmpty</c> and would
///     publish <c>minLength</c> and <c>required</c> on a member the wire contract never required.
/// </remarks>
public sealed class SaveTutorialStateRequestValidator : Validator<SaveTutorialStateRequest>
{
    /// <summary>Bounds the persisted key so an authenticated operator cannot bloat the identity row with an oversized key.</summary>
    public const int MaxKeyLength = 128;

    public SaveTutorialStateRequestValidator()
    {
        // Must, not NotEmpty: the schema processor would publish minLength and required off NotEmpty.
        RuleFor(static request => request.Key)
            .Cascade(CascadeMode.Stop)
            .Must(static key => !string.IsNullOrWhiteSpace(key))
            .WithMessage("Key is required.")
            .Must(static key => key.Trim().Length <= MaxKeyLength)
            .WithMessage($"Key must be {MaxKeyLength} characters or fewer.");

        RuleFor(static request => request.Status)
            .Must(static status => TutorialStateMapper.TryParseStatus(status, out _))
            .WithMessage("Status must be 'completed' or 'skipped'.");
    }
}
