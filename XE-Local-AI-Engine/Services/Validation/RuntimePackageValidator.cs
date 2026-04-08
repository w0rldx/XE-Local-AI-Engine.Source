namespace XE_Local_AI_Engine.Services.Validation
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Microsoft.Extensions.Options;
    using XE_Local_AI_Engine.Configuration;
    using XE_Local_AI_Engine.Models;
    using XE_Local_AI_Engine.Services.Invocation;

    public sealed class RuntimePackageValidator : IRuntimePackageValidator
    {
        private readonly ModelNameValidator _modelNameValidator;
        private readonly SecurityOptions _securityOptions;

        public RuntimePackageValidator(
            ModelNameValidator modelNameValidator,
            IOptions<SecurityOptions> securityOptions)
        {
            _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
            ArgumentNullException.ThrowIfNull(securityOptions);

            _securityOptions = securityOptions.Value;
        }

        public RuntimePackageValidationResult Validate(RuntimePackage package)
        {
            ArgumentNullException.ThrowIfNull(package);

            var errors = new List<string>();

            ValidateSystemPrompt(package.ResolvedSystemPrompt, errors);
            ValidateModelProfile(package.ModelProfile, errors);
            ValidateConversationContext(package.ConversationContext, errors);
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

        private void ValidateConversationContext(List<ConversationMessageDto> conversationContext, List<string> errors)
        {
            ArgumentNullException.ThrowIfNull(conversationContext);

            var maxMessageSizeBytes = _securityOptions.MaxMessageSizeKb * 1024;

            var invalidMessageCount = conversationContext
                .Select(message => message.Content)
                .Count(content =>
                    string.IsNullOrWhiteSpace(content) ||
                    ContainsNullByte(content) ||
                    Encoding.UTF8.GetByteCount(content) > maxMessageSizeBytes);

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
            return value.Contains('\0', StringComparison.Ordinal);
        }

        private static bool ContainsBlockedPayload(string value)
        {
            return value.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("<!entity", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("<?xml", StringComparison.OrdinalIgnoreCase);
        }
    }
}
