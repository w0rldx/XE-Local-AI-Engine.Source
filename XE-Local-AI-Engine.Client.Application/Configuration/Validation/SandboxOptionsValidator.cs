namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Fail-loud startup guard for the AgentHome sandbox provider. There is no execution-capable code
///     default, so an unset provider in Production must STOP startup rather than silently fall back — a stripped config
///     must never grant the host-command-executing <c>process</c> provider. Non-Production tolerates an unset provider
///     (the selector resolves the deterministic fake). Wired with <c>ValidateOnStart</c>, so this throws at host start.
/// </summary>
public sealed class SandboxOptionsValidator(IHostEnvironment environment) : IValidateOptions<SandboxOptions>
{
    private readonly IHostEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public ValidateOptionsResult Validate(string? name, SandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_environment.IsProduction() && string.IsNullOrWhiteSpace(options.Provider))
        {
            throw new InvalidOperationException($"{SandboxOptions.SectionName}:{nameof(SandboxOptions.Provider)} must be set in Production. There is no "
                                                + "execution-capable default; refusing to start rather than silently fall back to a command-executing provider.");
        }

        return ValidateOptionsResult.Success;
    }
}
