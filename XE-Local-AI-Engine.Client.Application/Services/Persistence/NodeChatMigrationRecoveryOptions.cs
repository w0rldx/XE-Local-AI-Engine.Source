namespace XE_Local_AI_Engine.Client.Services.Persistence;

public sealed class NodeChatMigrationRecoveryOptions
{
    public const string SectionName = "NodeChatMigrations";

    public TimeSpan MigrationAttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StartupLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan StartupLockPollInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}
