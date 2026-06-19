namespace XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     Server-side validation of a model-fit run's intent params. This is mandatory because llmfit does
///     no input validation of its own (it silently accepts an unknown <c>--use-case</c> and exits 0). The validator
///     allowlists the use-case (the six llmfit-supported values), bounds the limit, allowlists the provider, and routes
///     a benchmark model name through the existing <see cref="ModelNameValidator" />. Returns a sanitized error string,
///     or <c>null</c> when the params are valid.
/// </summary>
public sealed class ModelFitRequestValidator
{
    /// <summary>The inclusive lower bound for the recommend limit.</summary>
    public const int MinLimit = 1;

    /// <summary>
    ///     The inclusive upper bound for the recommend limit. The advisor only inspects a small fixed window of repos
    ///     (<c>DefaultRepoSearchLimit = 12</c>), so a low ceiling is realistic; it is kept identical to the handler's
    ///     JSON-schema <c>maximum</c> (50) so the endpoint/trigger/validator and the scheduled-run schema agree on one bound.
    /// </summary>
    public const int MaxLimit = 50;

    /// <summary>The six llmfit-supported use-case values. Matched ordinally and case-sensitively.</summary>
    public static readonly IReadOnlySet<string> AllowedUseCases =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "general",
            "coding",
            "reasoning",
            "chat",
            "multimodal",
            "embedding"
        };

    /// <summary>
    ///     The allowlisted providers. <c>llama.cpp</c> is the local advisor's in-process target; <c>ollama</c> is
    ///     retained for back-compat with any legacy recommendation snapshot key.
    ///     Matched ordinally and case-sensitively.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedProviders =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ollama",
            "llama.cpp"
        };

    private readonly ModelNameValidator _modelNameValidator;

    public ModelFitRequestValidator(ModelNameValidator modelNameValidator)
    {
        _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    }

    /// <summary>Validates the params for the given <paramref name="operation" />. Returns a sanitized error, or <c>null</c> when valid.</summary>
    public string? GetValidationError(ModelFitOperation operation,
        string? useCase,
        int limit,
        string? providerName,
        string? modelName)
    {
        if (string.IsNullOrWhiteSpace(providerName) || !AllowedProviders.Contains(providerName))
        {
            return "Provider is not supported.";
        }

        if (operation == ModelFitOperation.Recommend)
        {
            // use-case is optional, but if supplied it must be one of the six allowlisted values.
            if (!string.IsNullOrWhiteSpace(useCase) && !AllowedUseCases.Contains(useCase))
            {
                return "Use case is not supported.";
            }

            if (limit is < MinLimit or > MaxLimit)
            {
                return "Limit is out of range.";
            }

            return null;
        }

        if (operation == ModelFitOperation.Benchmark)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return "A model name is required for a benchmark.";
            }

            if (!_modelNameValidator.IsValid(modelName))
            {
                return "Model name is invalid.";
            }

            return null;
        }

        return "Operation is not supported.";
    }

    /// <summary>Convenience predicate: <c>true</c> only when <see cref="GetValidationError" /> returns <c>null</c>.</summary>
    public bool IsValid(ModelFitOperation operation,
        string? useCase,
        int limit,
        string? providerName,
        string? modelName)
    {
        return GetValidationError(operation, useCase, limit, providerName, modelName) is null;
    }
}
