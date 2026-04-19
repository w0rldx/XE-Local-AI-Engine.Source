namespace XE_Local_AI_Engine.Client.Services.Auth;

using NSec.Cryptography;

public interface INodeKeyRegistry : IDisposable
{
    string ActiveKeyId { get; }

    PublicKey ActivePublicKey { get; }

    NodeKeyResolution Resolve(string nodeKeyId);

    void Rotate(string nodeKeyId, Key privateKey);
}
