namespace XE_Local_AI_Engine.Client.Hosting.Vault;

using Microsoft.AspNetCore.Identity;

/// <summary>
///     The Identity password rules the real host enforces, checked by the pre-host before it accepts a recovery reset:
///     Identity only exists after the unlock, and a reset it then refuses would leave the operator with a 204 and the
///     old password.
/// </summary>
/// <remarks>
///     Mirrors <c>AddIdentityCore</c> in <c>ConfigureServices</c>: <see cref="PasswordOptions" /> defaults with
///     <see cref="PasswordOptions.RequiredLength" /> 12, and Identity's ASCII character classes and error texts. Change
///     both together.
/// </remarks>
internal static class VaultPasswordPolicy
{
    private static readonly PasswordOptions Options = new()
    {
        RequiredLength = 12
    };

    internal static IReadOnlyList<string> Validate(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var describer = new IdentityErrorDescriber();
        var errors = new List<string>();
        if (password.Length < Options.RequiredLength)
        {
            errors.Add(describer.PasswordTooShort(Options.RequiredLength).Description);
        }

        if (Options.RequireNonAlphanumeric && password.All(char.IsAsciiLetterOrDigit))
        {
            errors.Add(describer.PasswordRequiresNonAlphanumeric().Description);
        }

        if (Options.RequireDigit && !password.Any(char.IsAsciiDigit))
        {
            errors.Add(describer.PasswordRequiresDigit().Description);
        }

        if (Options.RequireLowercase && !password.Any(char.IsAsciiLetterLower))
        {
            errors.Add(describer.PasswordRequiresLower().Description);
        }

        if (Options.RequireUppercase && !password.Any(char.IsAsciiLetterUpper))
        {
            errors.Add(describer.PasswordRequiresUpper().Description);
        }

        if (password.Distinct().Count() < Options.RequiredUniqueChars)
        {
            errors.Add(describer.PasswordRequiresUniqueChars(Options.RequiredUniqueChars).Description);
        }

        return errors;
    }
}
