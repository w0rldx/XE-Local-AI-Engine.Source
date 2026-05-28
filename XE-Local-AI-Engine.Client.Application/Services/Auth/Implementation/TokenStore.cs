namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Client.Models;

public sealed class TokenStore : ITokenStore, IDisposable
{
    private const string CredentialsFileName = "worker-credentials.enc";
    private const bool DefaultAutoConnectOnStart = false;
    private static readonly TimeSpan ExpiringSoonThreshold = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _credentialsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<TokenStore> _logger;

    private readonly IDataProtector _protector;

    private StoredWorkerCredentials? _credentials;

    public TokenStore(IDataProtectionProvider dataProtectionProvider,
        IHostEnvironment hostEnvironment,
        ILogger<TokenStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector("WorkerNode.TokenStore.v1");
        _logger = logger;
        _credentialsPath = Path.Combine(hostEnvironment.ContentRootPath, CredentialsFileName);

        _credentials = LoadCredentialsFromDisk();
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    public event EventHandler? TokensChanged;

    public bool IsPaired => _credentials is not null;

    public bool IsTokenExpired => TokenExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow;

    public bool IsTokenExpiringSoon =>
        TokenExpiresAt is { } expiresAt &&
        expiresAt > DateTimeOffset.UtcNow &&
        expiresAt - DateTimeOffset.UtcNow <= ExpiringSoonThreshold;

    public DateTimeOffset? TokenExpiresAt => _credentials?.ExpiresAt;

    public bool AutoConnectOnStart => _credentials?.AutoConnectOnStart ?? DefaultAutoConnectOnStart;

    public string? BindingMethod => _credentials?.BindingMethod;

    public string? LastKnownNodeName => _credentials?.LastKnownNodeName;

    public async Task<string?> GetAccessTokenAsync()
    {
        await EnsureCredentialsLoadedAsync().ConfigureAwait(false);
        return IsTokenExpired ? null : _credentials?.AccessToken;
    }

    public async Task<Guid?> GetClientNodeIdAsync()
    {
        await EnsureCredentialsLoadedAsync().ConfigureAwait(false);
        return _credentials?.ClientNodeId;
    }

    public async Task<string?> GetRefreshTokenAsync()
    {
        await EnsureCredentialsLoadedAsync().ConfigureAwait(false);
        return _credentials?.RefreshToken;
    }

    public async Task StoreTokensAsync(PairClientResponse pairingResponse, TokenStoreMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(pairingResponse);

        var expiresAt = ParseJwtExpiry(pairingResponse.AccessToken) ?? pairingResponse.ExpiresAt;
        var credentials = new StoredWorkerCredentials
        {
            ClientNodeId = pairingResponse.ClientNodeId,
            AccessToken = pairingResponse.AccessToken,
            RefreshToken = pairingResponse.RefreshToken,
            ExpiresAt = expiresAt,
            BindingMethod = metadata?.BindingMethod ?? _credentials?.BindingMethod ?? "pairing-token",
            AutoConnectOnStart = metadata?.AutoConnectOnStart ?? _credentials?.AutoConnectOnStart ?? DefaultAutoConnectOnStart,
            LastKnownNodeName = metadata?.LastKnownNodeName ?? _credentials?.LastKnownNodeName
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(credentials, SerializerOptions);
        var protectedPayload = _protector.Protect(payload);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(_credentialsPath, protectedPayload).ConfigureAwait(false);
            ApplyPlatformFileSecurity();
            _credentials = credentials;
        }
        finally
        {
            _lock.Release();
        }

        RaiseTokensChanged();
    }

    public async Task SetAutoConnectOnStartAsync(bool enabled)
    {
        await EnsureCredentialsLoadedAsync().ConfigureAwait(false);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_credentials is null)
            {
                return;
            }

            _credentials = _credentials with
            {
                AutoConnectOnStart = enabled
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(_credentials, SerializerOptions);
            var protectedPayload = _protector.Protect(payload);
            await File.WriteAllBytesAsync(_credentialsPath, protectedPayload).ConfigureAwait(false);
            ApplyPlatformFileSecurity();
        }
        finally
        {
            _lock.Release();
        }

        RaiseTokensChanged();
    }

    public async Task ClearTokensAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(_credentialsPath))
            {
                File.Delete(_credentialsPath);
            }

            _credentials = null;
        }
        finally
        {
            _lock.Release();
        }

        RaiseTokensChanged();
    }

    public async Task HandleKeyRotationAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _credentials = await TryReadCredentialsLockedAsync(true).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private StoredWorkerCredentials? LoadCredentialsFromDisk()
    {
        if (!File.Exists(_credentialsPath))
        {
            return null;
        }

        try
        {
            var protectedPayload = File.ReadAllBytes(_credentialsPath);
            return DeserializeCredentials(_protector.Unprotect(protectedPayload));
        }
        catch (CryptographicException exception)
        {
            _logger.LogWarning(exception, "Failed to unprotect worker credentials during startup. Clearing stored credentials.");
            ClearCredentialsFileBestEffort();
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load worker credentials from disk.");
            return null;
        }
    }

    private async Task EnsureCredentialsLoadedAsync()
    {
        if (_credentials is not null)
        {
            return;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _credentials = await TryReadCredentialsLockedAsync(true).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<StoredWorkerCredentials?> TryReadCredentialsLockedAsync(bool clearOnCryptographicFailure)
    {
        if (!File.Exists(_credentialsPath))
        {
            return null;
        }

        try
        {
            var protectedPayload = await File.ReadAllBytesAsync(_credentialsPath).ConfigureAwait(false);
            var payload = _protector.Unprotect(protectedPayload);
            return DeserializeCredentials(payload);
        }
        catch (CryptographicException exception) when (clearOnCryptographicFailure)
        {
            _logger.LogWarning(exception, "Worker credential decryption failed. Clearing stored credentials and requiring re-pairing.");
            ClearCredentialsFileBestEffort();
            RaiseTokensChanged();
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Worker credentials could not be read. Clearing stored credentials and requiring re-pairing.");
            ClearCredentialsFileBestEffort();
            RaiseTokensChanged();
            return null;
        }
    }

    private void RaiseTokensChanged()
    {
        TokensChanged?.Invoke(this, EventArgs.Empty);
    }

    private static StoredWorkerCredentials DeserializeCredentials(byte[] payload)
    {
        var credentials = JsonSerializer.Deserialize<StoredWorkerCredentials>(payload, SerializerOptions);
        return credentials ?? throw new InvalidOperationException("Stored worker credentials could not be deserialized.");
    }

    private void ApplyPlatformFileSecurity()
    {
        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsFileSecurity();
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(_credentialsPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private void ApplyWindowsFileSecurity()
    {
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(true, false);

        var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User is not null)
        {
            fileSecurity.AddAccessRule(new FileSystemAccessRule(currentIdentity.User,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        var fileInfo = new FileInfo(_credentialsPath);
        fileInfo.SetAccessControl(fileSecurity);
    }

    private void ClearCredentialsFileBestEffort()
    {
        _credentials = null;

        try
        {
            if (File.Exists(_credentialsPath))
            {
                File.Delete(_credentialsPath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete corrupted worker credentials file.");
        }
    }

    private static DateTimeOffset? ParseJwtExpiry(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = DecodeBase64Url(segments[1]);
            using var document = JsonDocument.Parse(payloadBytes);

            if (!document.RootElement.TryGetProperty("exp", out var expElement))
            {
                return null;
            }

            return expElement.ValueKind switch
            {
                JsonValueKind.Number when expElement.TryGetInt64(out var exp) => DateTimeOffset.FromUnixTimeSeconds(exp),
                JsonValueKind.String when long.TryParse(expElement.GetString(), out var exp) => DateTimeOffset.FromUnixTimeSeconds(exp),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;

        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private sealed record StoredWorkerCredentials
    {
        public required Guid ClientNodeId { get; init; }

        public required string AccessToken { get; init; }

        public required string RefreshToken { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }

        public string BindingMethod { get; init; } = "pairing-token";

        public bool AutoConnectOnStart { get; init; } = DefaultAutoConnectOnStart;

        public string? LastKnownNodeName { get; init; }
    }
}
