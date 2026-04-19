namespace XE_Local_AI_Engine.Client.Services.Persistence;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;

public sealed class NodeSqliteKeyHolder : INodeSqliteKeyHolder
{
    private const int ExpectedKeyLength = 32;
    private const string EnvVarName = "XE_NODE_SQLITE_KEY";
    private const string SecretFilePath = "/run/secrets/node-sqlite-key";
    private const string AspireParameterPath = "Parameters:node-sqlite-key";
    private static readonly byte[] EmptySalt = [];

    private byte[]? _key;

    public NodeSqliteKeyHolder(IOptions<WorkerNodeOptions> workerNodeOptions, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(workerNodeOptions);
        ArgumentNullException.ThrowIfNull(configuration);

        var nodeName = workerNodeOptions.Value.NodeName;
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            throw new InvalidOperationException("WorkerNode:NodeName must be configured.");
        }

        var operatorSecret = ResolveOperatorSecret(configuration);

        try
        {
            _key = DeriveKey(operatorSecret, nodeName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(operatorSecret);
        }
    }

    public ReadOnlyMemory<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            return _key;
        }
    }

    public void Dispose()
    {
        if (_key is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    private static byte[] DeriveKey(byte[] operatorSecret, string nodeName)
    {
        var derivedKey = new byte[ExpectedKeyLength];
        var info = Encoding.UTF8.GetBytes($"c0re-node-sqlite|v1|{nodeName}");

        HKDF.DeriveKey(HashAlgorithmName.SHA256, operatorSecret, derivedKey, EmptySalt, info);

        return derivedKey;
    }

    private static byte[] ResolveOperatorSecret(IConfiguration configuration)
    {
        var envValue = Environment.GetEnvironmentVariable(EnvVarName) ?? configuration[EnvVarName];
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return DecodeBase64Secret(envValue, EnvVarName);
        }

        if (File.Exists(SecretFilePath))
        {
            return ValidateRawSecret(File.ReadAllBytes(SecretFilePath), SecretFilePath);
        }

        var aspireParameter = configuration[AspireParameterPath];
        if (!string.IsNullOrWhiteSpace(aspireParameter))
        {
            return DecodeBase64Secret(aspireParameter, AspireParameterPath);
        }

        throw new InvalidOperationException($"A node SQLite operator secret must be provided via '{EnvVarName}', '{SecretFilePath}', or '{AspireParameterPath}'.");
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

        if (rawSecret.Length != ExpectedKeyLength)
        {
            throw new InvalidOperationException($"The value from '{sourceName}' must contain exactly {ExpectedKeyLength} bytes.");
        }

        return rawSecret;
    }
}
