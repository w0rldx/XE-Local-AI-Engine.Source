namespace XE_Local_AI_Engine.Client.Services.HuggingFace;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Encrypted-at-rest store for the optional Hugging Face access token (third instance of the
///     <c>CloudCredentialStore</c> / <c>TokenStore</c> <see cref="IDataProtector" /> pattern). The token is protected
///     with the <c>WorkerNode.HfTokenStore.v1</c> protector and written to <c>hf-token.enc</c> under the node data dir.
///     It is exposed only via <see cref="GetTokenAsync" /> to the download client and is never logged, never placed in
///     exceptions, and never indexed.
/// </summary>
public sealed class HfTokenStore : IHfTokenStore, IDisposable
{
    private const string TokenFileName = "hf-token.enc";
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<HfTokenStore> _logger;
    private readonly IDataProtector _protector;

    private readonly string _tokenPath;

    public HfTokenStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<HfTokenStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector("WorkerNode.HfTokenStore.v1");
        _tokenPath = Path.Combine(dataDirectory.Root, TokenFileName);
        _logger = logger;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <inheritdoc />
    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_tokenPath))
            {
                return null;
            }

            try
            {
                var protectedPayload = await File.ReadAllBytesAsync(_tokenPath, ct).ConfigureAwait(false);
                var payload = _protector.Unprotect(protectedPayload);
                var token = Encoding.UTF8.GetString(payload);
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch (CryptographicException exception)
            {
                // Self-heal: an unreadable token (key-ring rotation/corruption) clears to anonymous, never crashes.
                _logger.LogWarning(exception, "Hugging Face token decryption failed. Clearing the stored token.");
                ClearTokenFileBestEffort();
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Hugging Face token could not be read from disk.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetTokenAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var protectedPayload = _protector.Protect(Encoding.UTF8.GetBytes(token));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(_tokenPath, protectedPayload, ct).ConfigureAwait(false);
            SecureFilePermissions.Apply(_tokenPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_tokenPath))
            {
                File.Delete(_tokenPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasTokenAsync(CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        return token is not null;
    }

    private void ClearTokenFileBestEffort()
    {
        try
        {
            if (File.Exists(_tokenPath))
            {
                File.Delete(_tokenPath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete the Hugging Face token file.");
        }
    }
}
