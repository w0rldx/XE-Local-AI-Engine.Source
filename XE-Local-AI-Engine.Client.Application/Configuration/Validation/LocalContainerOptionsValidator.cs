namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Fail-closed validation for <see cref="LocalContainerOptions" />. The provider applies a
///     resource ceiling and a network posture to a privileged sandbox container, so every limit must be positive and
///     the network mode must be one of the two provider-neutral postures (<c>none</c>/<c>restricted</c>); a blank image
///     or name prefix would produce an unnamed/imageless create. Validation runs only when the <c>local-container</c>
///     provider is selectable, so it cannot block a fake-provider startup with invalid (unused) LocalContainer config.
/// </summary>
public sealed class LocalContainerOptionsValidator : IValidateOptions<LocalContainerOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalContainerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var networkMode = options.NetworkMode?.Trim();
        var networkValid = string.Equals(networkMode, "none", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(networkMode, "restricted", StringComparison.OrdinalIgnoreCase);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(string.IsNullOrWhiteSpace(options.DefaultImage),
                                   "LocalContainer:DefaultImage must not be blank.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.ContainerNamePrefix),
                                   "LocalContainer:ContainerNamePrefix must not be blank.")
                               .AppendIf(!networkValid,
                                   "LocalContainer:NetworkMode must be 'none' or 'restricted'.")
                               .AppendIf(options.CpuLimit <= 0,
                                   "LocalContainer:CpuLimit must be greater than zero.")
                               .AppendIf(options.MemoryLimitMb <= 0,
                                   "LocalContainer:MemoryLimitMb must be greater than zero.")
                               .AppendIf(options.PidsLimit <= 0,
                                   "LocalContainer:PidsLimit must be greater than zero.")
                               .AppendIf(options.MaxCopyFileBytes <= 0,
                                   "LocalContainer:MaxCopyFileBytes must be greater than zero.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
