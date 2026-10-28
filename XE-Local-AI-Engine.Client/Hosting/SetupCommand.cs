namespace XE_Local_AI_Engine.Client.Hosting;

internal sealed class SetupCommand
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    public required bool PasswordFromEnvironment { get; init; }
}
