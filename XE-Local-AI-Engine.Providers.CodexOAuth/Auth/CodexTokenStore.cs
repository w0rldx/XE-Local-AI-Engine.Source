namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Encrypted token store mirroring <c>CloudCredentialStore</c>: DataProtection at rest,
///     user-only file permissions via <see cref="SecureFilePermissions" />. Uses a dedicated protector purpose and a
///     separate <c>.enc</c> file so it cannot collide with the API-key-shaped cloud credential store.
///     Never logs token values.
/// </summary>
public sealed class CodexTokenStore : ICodexTokenStore, IDisposable
{
    private const string TokensFileName = "codex-oauth-tokens.enc";
    private const string ProtectorPurpose = "WorkerNode.CodexOAuth.Tokens.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<CodexTokenStore> _logger;
    private readonly IDataProtector _protector;

    private readonly string _tokensPath;

    public CodexTokenStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<CodexTokenStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _tokensPath = Path.Combine(dataDirectory.Root, TokensFileName);
        _logger = logger;
    }

    public async Task<CodexTokens?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_tokensPath))
            {
                return null;
            }

            try
            {
                var protectedPayload = await File.ReadAllBytesAsync(_tokensPath, cancellationToken).ConfigureAwait(false);
                var payload = _protector.Unprotect(protectedPayload);
                return DeserializeTokens(payload);
            }
            catch (CryptographicException exception)
            {
                _logger.LogWarning(exception, "Codex token decryption failed. Clearing stored Codex tokens.");
                ClearTokensFileBestEffort();
                return null;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Codex tokens could not be deserialized. Clearing stored Codex tokens.");
                ClearTokensFileBestEffort();
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Codex tokens could not be read from disk.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CodexTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ValidateTokens(tokens);

        var payload = JsonSerializer.SerializeToUtf8Bytes(tokens, SerializerOptions);
        var protectedPayload = _protector.Protect(payload);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(_tokensPath, protectedPayload, cancellationToken).ConfigureAwait(false);
            SecureFilePermissions.Apply(_tokensPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_tokensPath))
            {
                File.Delete(_tokensPath);
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

    private static CodexTokens DeserializeTokens(byte[] payload)
    {
        var tokens = JsonSerializer.Deserialize<CodexTokens>(payload, SerializerOptions);
        return tokens ?? throw new InvalidOperationException("Stored Codex tokens could not be deserialized.");
    }

    private static void ValidateTokens(CodexTokens tokens)
    {
        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            throw new ArgumentException("Stored Codex tokens are missing an access token.", nameof(tokens));
        }

        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            throw new ArgumentException("Stored Codex tokens are missing a refresh token.", nameof(tokens));
        }

        if (string.IsNullOrWhiteSpace(tokens.AccountId))
        {
            throw new ArgumentException("Stored Codex tokens are missing an account id.", nameof(tokens));
        }
    }

    private void ClearTokensFileBestEffort()
    {
        try
        {
            if (File.Exists(_tokensPath))
            {
                File.Delete(_tokensPath);
            }
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Failed to delete Codex tokens file.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Failed to delete Codex tokens file.");
        }
    }
}
