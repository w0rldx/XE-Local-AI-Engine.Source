namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using XE_Local_AI_Engine.Client.Services.Persistence;

public sealed class NodeOperatorSecretProvider : INodeOperatorSecretProvider
{
    public const int ExpectedSecretLength = 32;
    public const string EnvVarName = "XE_NODE_SQLITE_KEY";
    public const string SecretFilePath = "/run/secrets/node-sqlite-key";
    public const string AspireParameterPath = "Parameters:node-sqlite-key";

    private const string AppHostSecretsProjectPath = "XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj";

    private readonly IConfiguration _configuration;

    public NodeOperatorSecretProvider(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public byte[] GetOperatorSecret()
    {
        var envValue = Environment.GetEnvironmentVariable(EnvVarName) ?? _configuration[EnvVarName];
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return DecodeBase64Secret(envValue, EnvVarName);
        }

        if (File.Exists(SecretFilePath))
        {
            return ValidateRawSecret(File.ReadAllBytes(SecretFilePath), SecretFilePath);
        }

        var aspireParameter = _configuration[AspireParameterPath];
        if (!string.IsNullOrWhiteSpace(aspireParameter))
        {
            return DecodeBase64Secret(aspireParameter, AspireParameterPath);
        }

        throw new InvalidOperationException(
            $"A node operator secret is required. Provide a base64-encoded 32-byte value via '{EnvVarName}', provide a raw 32-byte secret file at '{SecretFilePath}', or set the Aspire AppHost user-secret '{AspireParameterPath}'. For local Aspire runs, use: dotnet user-secrets set \"{AspireParameterPath}\" \"<base64-32-byte-secret>\" --project \"{AppHostSecretsProjectPath}\".");
    }

    private static byte[] DecodeBase64Secret(string base64Value, string sourceName)
    {
        try
        {
            return ValidateRawSecret(Convert.FromBase64String(base64Value), sourceName);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"The value from '{sourceName}' must be valid base64.", exception);
        }
    }

    private static byte[] ValidateRawSecret(byte[] rawSecret, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(rawSecret);

        if (rawSecret.Length != ExpectedSecretLength)
        {
            throw new InvalidOperationException($"The value from '{sourceName}' must contain exactly {ExpectedSecretLength} bytes.");
        }

        return rawSecret;
    }
}
