namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Fail-closed validation for <see cref="LocalContainerOptions" />. The process sandbox bounds its whole-file
///     copy-into transfer by <see cref="LocalContainerOptions.MaxCopyFileBytes" />, so that ceiling must be positive.
///     <see cref="LocalContainerOptions.MaxJailDiskBytes" /> is deliberately NOT required to be positive: a
///     non-positive value is the documented way to disable the jail disk watchdog, so rejecting it here would remove a
///     supported configuration rather than catch a mistake.
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
