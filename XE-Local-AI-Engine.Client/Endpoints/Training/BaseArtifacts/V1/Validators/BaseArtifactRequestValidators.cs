namespace XE_Local_AI_Engine.Client.Endpoints.Training.BaseArtifacts.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Boundary validation for the repository the operator names. The shape check happens here so a malformed id never
///     reaches the Hub client, and so the refusal is a 400 with a field message rather than a 409 from a failed lookup.
/// </summary>
public sealed class CreateBaseArtifactRequestValidator : Validator<CreateBaseArtifactRequest>
{
    public CreateBaseArtifactRequestValidator()
    {
        RuleFor(static request => request.RepoId)
            .NotEmpty()
            .WithMessage("A Hugging Face repository id is required.")
            .Must(BeAHuggingFaceRepoId)
            .WithMessage("The repository id must be in owner/name form, for example unsloth/Llama-3.2-1B-Instruct.");

        RuleFor(static request => request.Revision!)
            .MaximumLength(100)
            .When(static request => !string.IsNullOrWhiteSpace(request.Revision));
    }

    private static bool BeAHuggingFaceRepoId(string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId))
        {
            return false;
        }

        var parts = repoId.Trim().Split('/');
        return parts.Length == 2
               && parts.All(static part => part.Length > 0)
               // The id is composed into a URL path; anything traversal-shaped is rejected before it gets there.
               && !repoId.Contains("..", StringComparison.Ordinal);
    }
}
