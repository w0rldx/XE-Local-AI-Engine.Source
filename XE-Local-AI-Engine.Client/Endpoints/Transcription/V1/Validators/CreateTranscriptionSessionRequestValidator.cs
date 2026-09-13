namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Bounds on a new session. The source kind is the load-bearing rule: the service throws an
///     <see cref="ArgumentException" /> for an unknown one, and an unhandled exception is a 500 — so an unknown kind
///     has to be refused here, with the same parse the service performs, or the two would disagree.
/// </summary>
public sealed class CreateTranscriptionSessionRequestValidator : Validator<CreateTranscriptionSessionRequest>
{
    /// <summary>The two language modes; anything else is a client that has drifted from the contract.</summary>
    private static readonly string[] LanguageModes = ["auto", "override"];

    public CreateTranscriptionSessionRequestValidator()
    {
        // Matched against the NAMES. Enum.TryParse also parses the underlying numbers, so "99" would pass both this
        // rule and the service's, and be stored as an ordinal no member has — surfacing on the wire as "99" for a
        // client that has no case for it. Same rule as DevWorkflowTokenRules.IsNamed, kept local so two endpoint
        // feature folders do not reach into each other.
        RuleFor(static request => request.SourceKind)
            .Must(static sourceKind => string.IsNullOrWhiteSpace(sourceKind)
                                       || Enum.GetNames<TranscriptionSourceKind>().Contains(sourceKind.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage($"The source kind must be one of {string.Join(", ", Enum.GetNames<TranscriptionSourceKind>())}.");

        RuleFor(static request => request.LanguageMode)
            .Must(static mode => LanguageModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            .WithMessage("The language mode must be 'auto' or 'override'.");

        // An override with nothing to override by is silently degraded to auto downstream. Refusing it here is what
        // stops an operator who asked for German getting an auto-detected transcript and no sign anything was dropped.
        //
        // Declared on the REQUEST, not on the property, and that is the whole point. FastEndpoints' validation schema
        // processor lifts a property rule into the OpenAPI schema, and it did so for both forms of a conditional one:
        // the block `When(pred, () => RuleFor(x => x.LanguageOverride).NotEmpty().Length(2, 8))` published the member
        // as `required`, and switching to the chained `.When(...)` dropped that but still published `minLength: 2`.
        // Either way the generated client refused every `languageMode: "auto"` create request before it left the
        // browser — a break no backend test could see, because the server was answering correctly throughout. An
        // object-level rule has no property to attach to, so nothing about it can reach the schema.
        RuleFor(static request => request)
            .Must(static request => !IsOverride(request.LanguageMode) || request.LanguageOverride?.Trim().Length is >= 2 and <= 8)
            .WithMessage("Name the language to force as a 2-8 character code.");

        RuleFor(static request => request.MaxWindowSeconds)
            .InclusiveBetween(from: 2, to: 10)
            .WithMessage("The capture window must be between 2 and 10 seconds.");
    }

    private static bool IsOverride(string? languageMode) =>
        string.Equals(languageMode, "override", StringComparison.OrdinalIgnoreCase);
}
