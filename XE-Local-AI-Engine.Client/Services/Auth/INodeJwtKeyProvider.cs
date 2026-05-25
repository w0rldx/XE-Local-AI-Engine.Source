namespace XE_Local_AI_Engine.Client.Services.Auth;

public interface INodeJwtKeyProvider : IDisposable
{
    ReadOnlyMemory<byte> SigningKey { get; }
}
