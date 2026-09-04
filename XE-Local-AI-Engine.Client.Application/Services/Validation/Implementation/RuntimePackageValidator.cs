namespace XE_Local_AI_Engine.Client.Services.Validation.Implementation;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed class RuntimePackageValidator : IRuntimePackageValidator
{
    private readonly ModelNameValidator _modelNameValidator;
    private readonly SecurityOptions _securityOptions;

    public RuntimePackageValidator(ModelNameValidator modelNameValidator,
        IOptions<SecurityOptions> securityOptions)
    {
        _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
        ArgumentNullException.ThrowIfNull(securityOptions);

        _securityOptions = securityOptions.Value;
    }

    public RuntimePackageValidationResult Validate(RuntimePackage package, bool enforceMessageSizeCap = true)
    {
        ArgumentNullException.ThrowIfNull(package);

        var errors = new List<string>();

        ValidateSystemPrompt(package.ResolvedSystemPrompt, errors);
        ValidateModelProfile(package.ModelProfile, errors);
        ValidateReasoningEffort(package.ReasoningEffort, errors);
        ValidateConversationContext(package.ConversationContext, enforceMessageSizeCap, errors);
        ValidateTimeouts(package.Timeouts, errors);
        ValidateConfigHash(package.ConfigHash, errors);
        ValidateAllowedTools(package.AllowedTools, errors);

        return RuntimePackageValidationResult.FromErrors(errors);
    }

    private void ValidateSystemPrompt(string systemPrompt, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            errors.Add("Invalid system prompt format");
            return;
        }

        if (ContainsNullByte(systemPrompt) || ContainsBlockedPayload(systemPrompt))
        {
            errors.Add("Invalid system prompt format");
        }

        if (Encoding.UTF8.GetByteCount(systemPrompt) > _securityOptions.MaxSystemPromptSizeKb * 1024)
        {
            errors.Add("Invalid system prompt format");
        }
    }

    private void ValidateModelProfile(string? modelProfile, List<string> errors)
    {
        var validationError = _modelNameValidator.GetValidationError(modelProfile);
        if (validationError is not null)
        {
            errors.Add(validationError);
        }
    }

    private static void ValidateReasoningEffort(string? reasoningEffort, List<string> errors)
    {
        // Blank (unspecified) is allowed; a non-blank value that the shared normalizer does not recognize is invalid.
        if (!ReasoningEffortNormalizer.IsValid(reasoningEffort))
        {
            errors.Add("Invalid reasoning effort");
        }
    }

    private void ValidateConversationContext(List<ConversationMessageDto> conversationContext, bool enforceMessageSizeCap, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(conversationContext);

        // Blank and null-byte content are integrity faults: they are equally wrong whoever produced them, so they are
        // checked on every path. The size cap is not — see the interface's doc for why it is inbound-only.
        var maxMessageSizeBytes = enforceMessageSizeCap ? _securityOptions.MaxMessageSizeKb * 1024 : int.MaxValue;

        // A vision (image-only) turn legitimately carries blank text — its payload is the image parts — so blank content
        // is a fault ONLY when the message has no images to stand in for it. A replayed tool-history turn earns the same
        // exemption for the same reason: a run that called a tool and then died left a real side effect whose record is
        // the exchanges, not the (absent) text. Null-byte and the size cap still apply to whatever text IS present.
        var invalidMessageCount = conversationContext
            .Count(message =>
            {
                var content = message.Content;
                var hasNonTextPayload = message.Images is { Count: > 0 } || message.ToolExchanges is { Count: > 0 };
                return (string.IsNullOrWhiteSpace(content) && !hasNonTextPayload) ||
                       ContainsNullByte(content) ||
                       Encoding.UTF8.GetByteCount(content) > maxMessageSizeBytes;
            });

        errors.AddRange(Enumerable.Repeat("Invalid conversation message content", invalidMessageCount));
    }

    private static void ValidateTimeouts(TimeoutSettings timeouts, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(timeouts);

        if (timeouts.InvocationTimeoutSeconds <= 0 ||
            timeouts.ToolCallTimeoutSeconds <= 0 ||
            timeouts.StreamIdleTimeoutSeconds <= 0)
        {
            errors.Add("Invalid timeout configuration");
        }
    }

    private static void ValidateConfigHash(string configHash, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configHash))
        {
            errors.Add("Configuration integrity check failed");
        }
    }

    private static void ValidateAllowedTools(List<AllowedToolDto> allowedTools, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);

        var invalidToolCount = allowedTools.Count(tool => string.IsNullOrWhiteSpace(tool.Name));

        errors.AddRange(Enumerable.Repeat("Invalid allowed tool definition", invalidToolCount));
    }

    private static bool ContainsNullByte(string value)
    {
        return value.Contains(value: '\0', StringComparison.Ordinal);
    }

    private static bool ContainsBlockedPayload(string value)
    {
        return value.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("<!entity", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("<?xml", StringComparison.OrdinalIgnoreCase);
    }
}
