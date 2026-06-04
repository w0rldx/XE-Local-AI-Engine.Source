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

        // 150 comfortably fits Hugging Face GGUF references (hf.co/org/repo:quant), which are longer than plain Ollama tags.
        if (modelName.Length > 150)
        {
            return "Invalid model identifier";
        }

        // Path-traversal / scheme guards run BEFORE the regex so "hf.co/../etc" and "file://x" are rejected even though
        // the allow pattern now permits the hf.co/huggingface.co two-slash form. The regex governs all other slash placement.
        if (modelName.Contains("..", StringComparison.Ordinal) ||
            modelName.Contains('\\', StringComparison.Ordinal) ||
            modelName.Contains("://", StringComparison.Ordinal))
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
