namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

/// <summary>
///     Startup/options validator for security options settings.
/// </summary>
public sealed class SecurityOptionsValidator : IValidateOptions<SecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.MaxSystemPromptSizeKb is < 1 or > 1024, "Security:MaxSystemPromptSizeKb must be between 1 and 1024.")
                               .AppendIf(options.MaxMessageSizeKb is < 1 or > 1024, "Security:MaxMessageSizeKb must be between 1 and 1024.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.AllowedModelNamePattern), "Security:AllowedModelNamePattern is required.")
                               .AppendIf(!IsValidRegex(options.AllowedModelNamePattern), "Security:AllowedModelNamePattern must be a valid regular expression.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
