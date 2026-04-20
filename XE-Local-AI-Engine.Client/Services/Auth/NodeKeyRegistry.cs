namespace XE_Local_AI_Engine.Client.Services.Auth;

using NSec.Cryptography;

public sealed class NodeKeyRegistry : INodeKeyRegistry
{
    private static readonly TimeSpan RetiredKeyGraceWindow = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, RetiredNodeKey> _retiredKeys = new(StringComparer.Ordinal);
    private readonly object _syncRoot = new();
    private readonly TimeProvider _timeProvider;

    private ActiveNodeKey? _activeKey;
    private bool _disposed;

    public NodeKeyRegistry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string ActiveKeyId
    {
        get
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return _activeKey?.KeyId ?? throw new InvalidOperationException("No active node key is registered.");
            }
        }
    }

    public PublicKey ActivePublicKey
    {
        get
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                return _activeKey?.PrivateKey.PublicKey ?? throw new InvalidOperationException("No active node key is registered.");
            }
        }
    }

    public IReadOnlyList<NodeKeyResolution> ResolveGraceEligible()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            var now = _timeProvider.GetUtcNow();
            EvictExpiredRetiredKeys(now);

            var resolutions = new List<NodeKeyResolution>();

            if (_activeKey is { } activeKey)
            {
                resolutions.Add(new NodeKeyResolution
                {
                    RequestedKeyId = activeKey.KeyId,
                    Status = NodeKeyLookupStatus.Active,
                    KeyIdUsed = activeKey.KeyId,
                    PrivateKey = activeKey.PrivateKey,
                    PublicKey = activeKey.PrivateKey.PublicKey
                });
            }

            resolutions.AddRange(_retiredKeys.Values
                .OrderByDescending(static key => key.ExpiresAtUtc)
                .Select(static retiredKey => new NodeKeyResolution
                {
                    RequestedKeyId = retiredKey.KeyId,
                    Status = NodeKeyLookupStatus.Retired,
                    KeyIdUsed = retiredKey.KeyId,
                    PrivateKey = retiredKey.PrivateKey,
                    PublicKey = retiredKey.PrivateKey.PublicKey
                }));

            return resolutions;
        }
    }

    public NodeKeyResolution Resolve(string nodeKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeKeyId);

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            var now = _timeProvider.GetUtcNow();
            EvictExpiredRetiredKeys(now, nodeKeyId);

            if (_activeKey is { } activeKey && string.Equals(activeKey.KeyId, nodeKeyId, StringComparison.Ordinal))
            {
                return new NodeKeyResolution
                {
                    RequestedKeyId = nodeKeyId,
                    Status = NodeKeyLookupStatus.Active,
                    KeyIdUsed = activeKey.KeyId,
                    PrivateKey = activeKey.PrivateKey,
                    PublicKey = activeKey.PrivateKey.PublicKey
                };
            }

            if (_retiredKeys.TryGetValue(nodeKeyId, out var retiredKey))
            {
                if (retiredKey.ExpiresAtUtc > now)
                {
                    return new NodeKeyResolution
                    {
                        RequestedKeyId = nodeKeyId,
                        Status = NodeKeyLookupStatus.Retired,
                        KeyIdUsed = retiredKey.KeyId,
                        PrivateKey = retiredKey.PrivateKey,
                        PublicKey = retiredKey.PrivateKey.PublicKey
                    };
                }

                _retiredKeys.Remove(nodeKeyId);
                retiredKey.PrivateKey.Dispose();

                return new NodeKeyResolution
                {
                    RequestedKeyId = nodeKeyId,
                    Status = NodeKeyLookupStatus.RetiredExpired,
                    KeyIdUsed = nodeKeyId
                };
            }

            return new NodeKeyResolution
            {
                RequestedKeyId = nodeKeyId,
                Status = NodeKeyLookupStatus.Missing,
                KeyIdUsed = _activeKey?.KeyId
            };
        }
    }

    public void Rotate(string nodeKeyId, Key privateKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeKeyId);
        ArgumentNullException.ThrowIfNull(privateKey);

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            var now = _timeProvider.GetUtcNow();
            EvictExpiredRetiredKeys(now);

            if (_activeKey is { } activeKey)
            {
                if (string.Equals(activeKey.KeyId, nodeKeyId, StringComparison.Ordinal))
                {
                    activeKey.PrivateKey.Dispose();
                }
                else
                {
                    _retiredKeys[activeKey.KeyId] = new RetiredNodeKey(activeKey.KeyId,
                        activeKey.PrivateKey,
                        now.Add(RetiredKeyGraceWindow));
                }
            }

            _activeKey = new ActiveNodeKey(nodeKeyId, privateKey);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeKey?.PrivateKey.Dispose();
            _activeKey = null;

            foreach (var retiredKey in _retiredKeys.Values)
            {
                retiredKey.PrivateKey.Dispose();
            }

            _retiredKeys.Clear();
        }
    }

    private void EvictExpiredRetiredKeys(DateTimeOffset now, string? preservedKeyId = null)
    {
        var expiredKeyIds = _retiredKeys
            .Where(entry => entry.Value.ExpiresAtUtc <= now
                            && !string.Equals(entry.Key, preservedKeyId, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var expiredKeyId in expiredKeyIds)
        {
            var retiredKey = _retiredKeys[expiredKeyId];
            _retiredKeys.Remove(expiredKeyId);
            retiredKey.PrivateKey.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ActiveNodeKey(string KeyId, Key PrivateKey);

    private sealed record RetiredNodeKey(string KeyId, Key PrivateKey, DateTimeOffset ExpiresAtUtc);
}
