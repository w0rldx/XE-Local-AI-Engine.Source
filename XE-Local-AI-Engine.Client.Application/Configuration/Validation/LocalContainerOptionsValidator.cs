namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Fail-closed validation for <see cref="LocalContainerOptions" />. The process sandbox bounds its whole-file
///     copy-into transfer by <see cref="LocalContainerOptions.MaxCopyFileBytes" />, so that ceiling must be positive.
///     Validation runs only when the <c>local-container</c> provider is selectable, so it cannot block a fake-provider
///     startup with invalid (unused) LocalContainer config.
/// </summary>
public sealed class LocalContainerOptionsValidator : IValidateOptions<LocalContainerOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalContainerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.MaxCopyFileBytes <= 0,
                                   "LocalContainer:MaxCopyFileBytes must be greater than zero.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
