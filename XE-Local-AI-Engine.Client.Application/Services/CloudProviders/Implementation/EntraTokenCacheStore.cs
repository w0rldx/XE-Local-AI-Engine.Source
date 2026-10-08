namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Security.Cryptography;
using System.Text.Json;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Encrypted at-rest store for the Entra ID <see cref="AuthenticationRecord" />: DataProtection at rest, Windows
///     user-only file security, *nix <c>0600</c>.
/// </summary>
/// <remarks>
///     Mirrors <c>CodexTokenStore</c>. A dedicated protector purpose and a separate <c>.enc</c> file keep it from
///     colliding with the API-key-shaped <see cref="CloudCredentialStore" />. The record carries no token value — only
///     the account descriptor Azure.Identity needs to attempt silent auth — but is still treated as sensitive (account
///     identifiers) and never logged.
/// </remarks>
public sealed class EntraTokenCacheStore : IEntraTokenCacheStore, IDisposable
{
    private const string ProtectorPurpose = "WorkerNode.EntraId.AuthenticationRecord.v1";
    private const string RecordFileName = "entra-auth-record.enc";

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<EntraTokenCacheStore> _logger;
    private readonly IDataProtector _protector;
    private readonly string _recordPath;

    public EntraTokenCacheStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<EntraTokenCacheStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _recordPath = Path.Combine(dataDirectory.Root, RecordFileName);
        _logger = logger;
    }

    public async Task<AuthenticationRecord?> LoadRecordAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_recordPath))
            {
                return null;
            }

            try
            {
                var protectedPayload = await File.ReadAllBytesAsync(_recordPath, cancellationToken);
                var payload = _protector.Unprotect(protectedPayload);
                using var stream = new MemoryStream(payload);
                return await AuthenticationRecord.DeserializeAsync(stream, cancellationToken);
            }
            catch (CryptographicException exception)
            {
                _logger.LogWarning(exception, "Entra ID authentication record decryption failed. Moving the stored record aside.");
                MoveRecordFileAsideBestEffort();
                return null;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Entra ID authentication record could not be deserialized. Moving the stored record aside.");
                MoveRecordFileAsideBestEffort();
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Entra ID authentication record could not be read from disk.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveRecordAsync(AuthenticationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var stream = new MemoryStream();
        await record.SerializeAsync(stream, cancellationToken);
        var protectedPayload = _protector.Protect(stream.ToArray());

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await SecureFilePermissions.WriteAllBytesAtomicAsync(_recordPath, protectedPayload, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_recordPath))
            {
                File.Delete(_recordPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private void MoveRecordFileAsideBestEffort()
    {
        try
        {
            var movedTo = SecureFilePermissions.MoveAsideUnreadable(_recordPath);
            _logger.LogWarning("The unreadable Entra ID authentication record was kept as {QuarantinePath}.", movedTo);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Failed to move the unreadable Entra ID authentication record aside.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Failed to move the unreadable Entra ID authentication record aside.");
        }
    }
}
