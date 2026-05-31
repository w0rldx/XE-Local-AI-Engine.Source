namespace XE_Local_AI_Engine.Client.Services.Auth;

using NSec.Cryptography;

/// <summary>
///     Abstraction for node key registry behavior.
/// </summary>
public interface INodeKeyRegistry : IDisposable
{
    string ActiveKeyId { get; }

    PublicKey ActivePublicKey { get; }

    IReadOnlyList<NodeKeyResolution> ResolveGraceEligible();

    NodeKeyResolution Resolve(string nodeKeyId);

    void Rotate(string nodeKeyId, Key privateKey);
}
