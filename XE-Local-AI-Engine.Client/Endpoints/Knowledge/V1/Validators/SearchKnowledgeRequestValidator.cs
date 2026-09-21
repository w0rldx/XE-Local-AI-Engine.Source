namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>Judges the query against the transport and content bounds, then the collection namespace.</summary>
/// <remarks>
///     Both rules are pure, so the handler recomputes the normalized query and collection id it forwards rather than
///     carrying them across. The query is reported ahead of the collection, as it was in the handler.
/// </remarks>
public sealed class SearchKnowledgeRequestValidator : Validator<SearchKnowledgeRequest>
{
    public SearchKnowledgeRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
        {
            var validation = KnowledgeQueryLimits.ValidateAndNormalize(request.Query, out _);
            if (validation == KnowledgeQueryValidation.Empty)
            {
                return "A search query is required.";
            }

            if (validation == KnowledgeQueryValidation.TooLong)
            {
                return $"The search query must be {KnowledgeQueryLimits.MaxQueryLength} characters or fewer.";
            }

            return KnowledgeCollectionScope.TryNormalize(request.CollectionId, out _)
                ? null
                : "The collection id is invalid.";
        });
    }
}
