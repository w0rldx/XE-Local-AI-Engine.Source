namespace XE_Local_AI_Engine.Client.Services.Vault.Implementation;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The managed <c>node.key</c> vault under the node data directory. Registered only when the host generated or
///     unwrapped the operator secret itself (<see cref="ManagedConfigurationKey" />); otherwise
///     <see cref="NullNodeVault" /> answers.
/// </summary>
/// <remarks>
///     The state is read once at construction: a missing or legacy file is <see cref="VaultState.Pending" />, a v2
///     file is <see cref="VaultState.Unlocked" /> when the host set <see cref="UnlockedConfigurationKey" /> after its
///     unlock, else <see cref="VaultState.Locked" />. Every rewrap re-reads the file and proves the old password or
///     recovery code against it, so it never trusts the in-memory secret for that check.
/// </remarks>
public sealed class NodeVault : INodeVault, IDisposable
{
    /// <summary>Host-set flag: the operator secret is the persisted <c>node.key</c>, so the vault manages it.</summary>
    public const string ManagedConfigurationKey = "NodeVault:Managed";

    /// <summary>Host-set flag: the host unwrapped a v2 <c>node.key</c> before building this container.</summary>
    public const string UnlockedConfigurationKey = "NodeVault:Unlocked";

    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<NodeVault> _logger;
    private readonly string _path;
    private readonly INodeOperatorSecretProvider _secretProvider;
    private readonly TimeProvider _timeProvider;
    private volatile VaultState _state;

    public NodeVault(IConfiguration configuration,
        INodeDataDirectory dataDirectory,
        INodeOperatorSecretProvider secretProvider,
        TimeProvider timeProvider,
        ILogger<NodeVault> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = Path.Combine(dataDirectory.Root, VaultFileCodec.KeyFileName);

#pragma warning disable MA0045 // Read once at singleton construction, which DI performs synchronously.
        var content = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
#pragma warning restore MA0045
        if (content is null || VaultFileCodec.Detect(content) == VaultFileFormat.Legacy)
        {
            _state = VaultState.Pending;
        }
        else
        {
            _state = configuration.GetValue<bool>(UnlockedConfigurationKey) ? VaultState.Unlocked : VaultState.Locked;
        }
    }

    public VaultState State => _state;

    public async Task<VaultChange?> CreateAsync(string password, bool replaceUnlocked, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previousState = _state;
            if (previousState is VaultState.Locked)
            {
                throw new InvalidOperationException("The node vault is locked; unlock it before re-creating it.");
            }

            // Checked under the gate: two creates racing past a caller's own Pending check must not both write.
            if (previousState is VaultState.Unlocked && !replaceUnlocked)
            {
                throw new VaultAlreadyCreatedException();
            }

            var previous = await ReadExistingAsync(cancellationToken);
            var masterKey = _secretProvider.GetOperatorSecret();
            try
            {
                var (file, recoveryCode) = VaultFileCodec.Create(masterKey, password, _timeProvider.GetUtcNow());
                await VaultFileCodec.WriteAsync(_path, file, cancellationToken);
                _state = VaultState.Unlocked;
                _logger.LogInformation("Node vault created; node.key is now password-wrapped.");
                return new VaultChange
                {
                    RecoveryCode = recoveryCode,
                    PreviousFile = previous,
                    PreviousState = previousState
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<VaultChange?> RewrapAsync(string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        return RewrapCoreAsync(file => VaultFileCodec.UnwrapWithPassword(file, currentPassword), newPassword, newRecoveryCode: null, cancellationToken);
    }

    public Task<VaultChange?> RewrapWithRecoveryAsync(string recoveryCode, string newPassword, string? newRecoveryCode, CancellationToken cancellationToken)
    {
        // A recovery reset always rotates the code: the one just typed in may have been seen, and the reset is its only use.
        return RewrapCoreAsync(file => VaultFileCodec.UnwrapWithRecovery(file, recoveryCode),
            newPassword,
            newRecoveryCode ?? VaultFileCodec.NewRecoveryCode(),
            cancellationToken);
    }

    public async Task RestoreAsync(VaultChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (change.PreviousFile is { } previous)
            {
                await SecureFilePermissions.WriteAllBytesAtomicAsync(_path, previous, cancellationToken);
            }
            else
            {
                File.Delete(_path);
            }

            _state = change.PreviousState;
            _logger.LogWarning("Node vault write rolled back after a failed password update.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <param name="newRecoveryCode">Also re-wraps the recovery slot under this code; <see langword="null" /> keeps it.</param>
    private async Task<VaultChange?> RewrapCoreAsync(Func<VaultFile, byte[]> unwrap, string newPassword, string? newRecoveryCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previousState = _state;
            if (!previousState.HasWrappedKey())
            {
                // Pending: no v2 file yet, so there is nothing to rewrap; the legacy confirm step creates it.
                return null;
            }

            var previous = await File.ReadAllBytesAsync(_path, cancellationToken);
            var file = VaultFileCodec.Read(previous);
            var masterKey = unwrap(file);
            try
            {
                var rewrapped = newRecoveryCode is null
                    ? VaultFileCodec.Rewrap(file, masterKey, newPassword, _timeProvider.GetUtcNow())
                    : VaultFileCodec.RewrapWithNewRecovery(file, masterKey, newPassword, newRecoveryCode, _timeProvider.GetUtcNow());
                await VaultFileCodec.WriteAsync(_path, rewrapped, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }

            _logger.LogInformation("Node vault password wrap replaced (recovery code rotated: {RecoveryCodeRotated}).", newRecoveryCode is not null);
            return new VaultChange
            {
                RecoveryCode = newRecoveryCode,
                PreviousFile = previous,
                PreviousState = previousState
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ReadOnlyMemory<byte>?> ReadExistingAsync(CancellationToken cancellationToken)
    {
        return File.Exists(_path) ? await File.ReadAllBytesAsync(_path, cancellationToken) : null;
    }
}
