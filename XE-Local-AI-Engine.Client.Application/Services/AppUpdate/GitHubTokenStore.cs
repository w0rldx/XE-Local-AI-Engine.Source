namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Encrypted-at-rest store for the GitHub user access token + login used by app self-update (a sibling of the
///     <see cref="IHfTokenStore" /> / <c>CodexTokenStore</c> <see cref="IDataProtector" /> pattern). The session is
///     protected with the <c>WorkerNode.GitHubTokenStore.v1</c> protector and written to <c>github-token.enc</c> under
///     the node data dir, with owner-only file permissions. The token is exposed only via <see cref="GetSessionAsync" />
///     to the update source and is never logged, never placed in exceptions, never returned to React, and never indexed.
/// </summary>
public sealed class GitHubTokenStore : IGitHubTokenStore, IDisposable
{
    private const string SessionFileName = "github-token.enc";
    private const string ProtectorPurpose = "WorkerNode.GitHubTokenStore.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<GitHubTokenStore> _logger;
    private readonly IDataProtector _protector;

    private readonly string _sessionPath;

    public GitHubTokenStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<GitHubTokenStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _sessionPath = Path.Combine(dataDirectory.Root, SessionFileName);
        _logger = logger;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <inheritdoc />
    public async Task<GitHubSession?> GetSessionAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_sessionPath))
            {
                return null;
            }

            try
            {
                var protectedPayload = await File.ReadAllBytesAsync(_sessionPath, ct).ConfigureAwait(false);
                var payload = _protector.Unprotect(protectedPayload);
                var session = JsonSerializer.Deserialize<GitHubSession>(payload, SerializerOptions);
                return IsUsable(session) ? session : null;
            }
            catch (CryptographicException exception)
            {
                // Self-heal: an unreadable session (key-ring rotation/corruption) clears to signed-out, never crashes.
                _logger.LogWarning(exception, "GitHub session decryption failed. Clearing the stored session.");
                ClearSessionFileBestEffort();
                return null;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "The stored GitHub session could not be deserialized. Clearing it.");
                ClearSessionFileBestEffort();
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "The GitHub session could not be read from disk.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetSessionAsync(GitHubSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.Login))
        {
            throw new ArgumentException("A GitHub session must carry both an access token and a login.", nameof(session));
        }

        var protectedPayload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(session, SerializerOptions));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(_sessionPath, protectedPayload, ct).ConfigureAwait(false);
            ApplyPlatformFileSecurity();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearSessionAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_sessionPath))
            {
                File.Delete(_sessionPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasSessionAsync(CancellationToken ct)
    {
        var session = await GetSessionAsync(ct).ConfigureAwait(false);
        return session is not null;
    }

    private static bool IsUsable(GitHubSession? session)
    {
        return session is not null
               && !string.IsNullOrWhiteSpace(session.AccessToken)
               && !string.IsNullOrWhiteSpace(session.Login);
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
            File.SetUnixFileMode(_sessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private void ApplyWindowsFileSecurity()
    {
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User is not null)
        {
            fileSecurity.AddAccessRule(new FileSystemAccessRule(currentIdentity.User,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        var fileInfo = new FileInfo(_sessionPath);
        fileInfo.SetAccessControl(fileSecurity);
    }

    private void ClearSessionFileBestEffort()
    {
        try
        {
            if (File.Exists(_sessionPath))
            {
                File.Delete(_sessionPath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete the GitHub session file.");
        }
    }
}
