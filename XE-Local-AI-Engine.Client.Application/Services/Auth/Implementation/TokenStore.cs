namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Reads the DataProtection-encrypted worker credentials an earlier build could leave in the node data directory.
/// </summary>
/// <remarks>
///     Read-only: the pairing flow that wrote <c>worker-credentials.enc</c> is gone, so the file is never created,
///     updated or deleted here. The load is best-effort — a missing, unreadable or wrong-key file is the unpaired
///     case, not an error.
/// </remarks>
public sealed class TokenStore : ITokenStore
{
    private const string CredentialsFileName = "worker-credentials.enc";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly StoredWorkerCredentials? _credentials;
    private readonly TimeProvider _timeProvider;

    public TokenStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<TokenStore> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;

        var protector = dataProtectionProvider.CreateProtector("WorkerNode.TokenStore.v1");
        _credentials = LoadCredentialsFromDisk(Path.Combine(dataDirectory.Root, CredentialsFileName), protector, logger);
    }

    public Task<string?> GetAccessTokenAsync()
    {
        var expired = _credentials is not null && _credentials.ExpiresAt <= _timeProvider.GetUtcNow();
        return Task.FromResult(expired ? null : _credentials?.AccessToken);
    }

    public Task<Guid?> GetClientNodeIdAsync()
    {
        return Task.FromResult(_credentials?.ClientNodeId);
    }

    private static StoredWorkerCredentials? LoadCredentialsFromDisk(string credentialsPath, IDataProtector protector, ILogger<TokenStore> logger)
    {
        if (!File.Exists(credentialsPath))
        {
            return null;
        }

        try
        {
            // Constructor-time load: both members are synchronous reads over the field, so it has to be populated
            // before the instance is handed out, and a ctor cannot await.
#pragma warning disable MA0045 // forced sync: constructor, backing synchronous reads
            var protectedPayload = File.ReadAllBytes(credentialsPath);
#pragma warning restore MA0045
            var payload = protector.Unprotect(protectedPayload);
            return JsonSerializer.Deserialize<StoredWorkerCredentials>(payload, SerializerOptions);
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "Failed to unprotect stored worker credentials. Treating this node as unpaired.");
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to load stored worker credentials from disk. Treating this node as unpaired.");
            return null;
        }
    }

    private sealed record StoredWorkerCredentials(Guid ClientNodeId, string AccessToken, DateTimeOffset ExpiresAt);
}
