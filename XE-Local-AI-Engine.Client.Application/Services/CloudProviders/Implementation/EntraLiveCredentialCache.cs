namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using Azure.Core;

/// <inheritdoc cref="IEntraLiveCredentialCache" />
public sealed class EntraLiveCredentialCache : IEntraLiveCredentialCache
{
    private readonly Lock _gate = new();
    private TokenCredential? _credential;
    private string? _key;

    public TokenCredential? TryGet(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            return string.Equals(_key, key, StringComparison.Ordinal) ? _credential : null;
        }
    }

    public void Store(string key, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(credential);

        lock (_gate)
        {
            _key = key;
            _credential = credential;
        }
    }
}
