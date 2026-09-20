namespace XE_Local_AI_Engine.Client.Services.Validation;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Providers.Abstractions.External;

public sealed class ModelNameValidator
{
    private const string InvalidModelIdentifier = "Invalid model identifier";

    private readonly Regex _allowedPattern;

    public ModelNameValidator(IOptions<SecurityOptions> securityOptions)
    {
        ArgumentNullException.ThrowIfNull(securityOptions);

        var pattern = securityOptions.Value.AllowedModelNamePattern;
        _allowedPattern = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     An external id (<c>ext:{connectionId}/{wireId}</c>) is validated by its OWN grammar and returns unconditionally: the general allow-pattern
    ///     cannot express the namespaced form (its first slash segment may not contain a colon, and two slashes fail outright), and widening it would
    ///     loosen validation for every other provider. <c>ExternalModelId</c> enforces both charsets, the <c>ext:</c>-only 165-character bound (longer
    ///     than any other provider's, which is why the general 150 bound must not apply), and the same traversal and empty-segment refusals.
    /// </remarks>
    public string? GetValidationError(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        // The ext: branch is validated by ExternalModelId's own grammar and returns unconditionally. See this method's remarks.
        if (ExternalModelId.HasExternalScheme(modelName))
        {
            return ExternalModelId.TryParse(modelName, out _, out _) ? null : InvalidModelIdentifier;
        }

        // 150 comfortably fits Hugging Face GGUF references (hf.co/org/repo:quant), which are longer than plain Ollama tags.
        if (modelName.Length > 150)
        {
            return InvalidModelIdentifier;
        }

        // Path-traversal / scheme guards run BEFORE the regex so "hf.co/../etc" and "file://x" are rejected even though
        // the allow pattern now permits the hf.co/huggingface.co two-slash form. The regex governs all other slash placement.
        if (modelName.Contains("..", StringComparison.Ordinal) ||
            modelName.Contains(value: '\\', StringComparison.Ordinal) ||
            modelName.Contains("://", StringComparison.Ordinal))
        {
            return InvalidModelIdentifier;
        }

        return _allowedPattern.IsMatch(modelName) ? null : InvalidModelIdentifier;
    }

    public bool IsValid(string? modelName)
    {
        return GetValidationError(modelName) is null;
    }
}
