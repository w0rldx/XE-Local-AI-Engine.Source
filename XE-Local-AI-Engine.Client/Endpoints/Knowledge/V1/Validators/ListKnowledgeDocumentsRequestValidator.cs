namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Bounds on <c>GET knowledge-base/documents</c>. An absent limit is valid and means the default page; the ceiling
///     lives here because the handler reads the same constants.
/// </summary>
public sealed class ListKnowledgeDocumentsRequestValidator : Validator<ListKnowledgeDocumentsRequest>
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public ListKnowledgeDocumentsRequestValidator()
    {
        When(static request => request.Limit is not null,
            () => RuleFor(static request => request.Limit)
                  .InclusiveBetween(from: 1, MaxLimit)
                  .WithMessage($"Ask for between 1 and {MaxLimit} documents."));

        When(static request => request.Offset is not null,
            () => RuleFor(static request => request.Offset)
                  .GreaterThanOrEqualTo(valueToCompare: 0)
                  .WithMessage("The offset cannot be negative."));

        When(static request => request.DocumentIds is not null,
            () => RuleFor(static request => request.DocumentIds!.Count)
                  .LessThanOrEqualTo(MaxLimit)
                  .WithMessage($"Ask for at most {MaxLimit} documents by id."));
    }
}
