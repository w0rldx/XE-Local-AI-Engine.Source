namespace XE_Local_AI_Engine.Client;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using ClientSecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

internal static class AddNodeCoreOptionsExtensions
{
    public static IHostApplicationBuilder AddNodeCoreOptions(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<CentralPlatformOptions>()
               .Bind(configuration.GetSection(CentralPlatformOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerNodeOptions>()
               .Bind(configuration.GetSection(WorkerNodeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<ClientSecurityOptions>()
               .Bind(configuration.GetSection(ClientSecurityOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<NodeAuthOptions>()
               .Bind(configuration.GetSection(NodeAuthOptions.SectionName))
               .ValidateDataAnnotations()
               .Validate(static options => !string.IsNullOrWhiteSpace(options.Jwt.Issuer), "NodeAuth:Jwt:Issuer is required.")
               .Validate(static options => !string.IsNullOrWhiteSpace(options.Jwt.Audience), "NodeAuth:Jwt:Audience is required.")
               .Validate(static options => options.Jwt.AccessTokenMinutes is >= 1 and <= 1440, "NodeAuth:Jwt:AccessTokenMinutes must be between 1 and 1440.")
               .Validate(static options => options.RefreshTokenDays is >= 1 and <= 365, "NodeAuth:RefreshTokenDays must be between 1 and 365.")
               .ValidateOnStart();
        builder.Services.AddOptions<CloudProviderOptions>()
               .Bind(configuration.GetSection(CloudProviderOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<NodeChatMigrationRecoveryOptions>()
               .Bind(configuration.GetSection(NodeChatMigrationRecoveryOptions.SectionName))
               .Validate(static options => options.MigrationAttemptTimeout > TimeSpan.Zero, "Migration attempt timeout must be greater than zero.")
               .Validate(static options => options.StartupLockTimeout > TimeSpan.Zero, "Startup lock timeout must be greater than zero.")
               .Validate(static options => options.StartupLockPollInterval > TimeSpan.Zero, "Startup lock poll interval must be greater than zero.")
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerShutdownDrainOptions>();

        builder.Services.AddSingleton<IValidateOptions<CentralPlatformOptions>, CentralPlatformOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<WorkerNodeOptions>, WorkerNodeOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<ClientSecurityOptions>, SecurityOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<CloudProviderOptions>, CloudProviderOptionsValidator>();

        return builder;
    }
}
