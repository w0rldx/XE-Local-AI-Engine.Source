namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     In-memory <see cref="ITokenStore" />. <see cref="Unpaired" /> is the shape every shipped node has — no
///     stored worker credentials — and <see cref="WithCredentials" /> stands in for a node that was paired by an
///     earlier build and still carries the file.
/// </summary>
public sealed class MockTokenStore : ITokenStore
{
    private readonly string? _accessToken;
    private readonly Guid? _clientNodeId;

    private MockTokenStore(string? accessToken, Guid? clientNodeId)
    {
        _accessToken = accessToken;
        _clientNodeId = clientNodeId;
    }

    public static MockTokenStore Unpaired()
    {
        return new MockTokenStore(accessToken: null, clientNodeId: null);
    }

    public static MockTokenStore WithCredentials(string accessToken, Guid clientNodeId)
    {
        return new MockTokenStore(accessToken, clientNodeId);
    }

    public Task<string?> GetAccessTokenAsync()
    {
        return Task.FromResult(_accessToken);
    }

    public Task<Guid?> GetClientNodeIdAsync()
    {
        return Task.FromResult(_clientNodeId);
    }
}
