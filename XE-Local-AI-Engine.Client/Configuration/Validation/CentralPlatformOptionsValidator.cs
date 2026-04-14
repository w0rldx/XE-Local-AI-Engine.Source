namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;

public sealed class CentralPlatformOptionsValidator : IValidateOptions<CentralPlatformOptions>
{
    public ValidateOptionsResult Validate(string? name, CentralPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(string.IsNullOrWhiteSpace(options.BaseUrl), "CentralPlatform:BaseUrl is required.")
                               .AppendIf(!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "CentralPlatform:BaseUrl must be an absolute URL.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.HubPath), "CentralPlatform:HubPath is required.")
                               .AppendIf(!IsRelativePath(options.HubPath), "CentralPlatform:HubPath must be an absolute application path starting with '/'.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.PairingEndpoint), "CentralPlatform:PairingEndpoint is required.")
                               .AppendIf(!IsRelativePath(options.PairingEndpoint), "CentralPlatform:PairingEndpoint must be an absolute application path starting with '/'.")
                               .AppendIf(options.HeartbeatIntervalSeconds is < 5 or > 300, "CentralPlatform:HeartbeatIntervalSeconds must be between 5 and 300.")
                               .AppendIf(options.ReconnectDelaysMs.Count == 0, "CentralPlatform:ReconnectDelaysMs must contain at least one delay.")
                               .AppendIf(options.ReconnectDelaysMs.Any(delay => delay < 0), "CentralPlatform:ReconnectDelaysMs cannot contain negative values.")
                               .AppendIf(options.MaxReconnectAttempts is < 1 or > 100, "CentralPlatform:MaxReconnectAttempts must be between 1 and 100.")
                               .AppendIf(options.MaxSignalRMessageSizeKb is < 16 or > 1024, "CentralPlatform:MaxSignalRMessageSizeKb must be between 16 and 1024.")
                               .AppendIf(options.ToolCallTimeoutSeconds is < 5 or > 600, "CentralPlatform:ToolCallTimeoutSeconds must be between 5 and 600.")
                               .AppendIf(options.InvocationTimeoutSeconds is < 10 or > 3600, "CentralPlatform:InvocationTimeoutSeconds must be between 10 and 3600.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
        {
            return false;
        }

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        return !path.Contains("://", StringComparison.Ordinal);
    }
}
