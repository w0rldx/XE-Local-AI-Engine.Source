namespace XE_Local_AI_Engine.Client.Services.Validation;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;

public sealed class ModelNameValidator
{
    private readonly Regex _allowedPattern;

    public ModelNameValidator(IOptions<SecurityOptions> securityOptions)
    {
        ArgumentNullException.ThrowIfNull(securityOptions);

        var pattern = securityOptions.Value.AllowedModelNamePattern;
        _allowedPattern = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    }

    public string? GetValidationError(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        if (modelName.Length > 100)
        {
            return "Invalid model identifier";
        }

        if (modelName.Contains("..", StringComparison.Ordinal) ||
            modelName.Contains('/', StringComparison.Ordinal) ||
            modelName.Contains('\\', StringComparison.Ordinal))
        {
            return "Invalid model identifier";
        }

        if (modelName.Contains("://", StringComparison.Ordinal))
        {
            return "Invalid model identifier";
        }

        return _allowedPattern.IsMatch(modelName) ? null : "Invalid model identifier";
    }

    public bool IsValid(string? modelName)
    {
        return GetValidationError(modelName) is null;
    }
}
