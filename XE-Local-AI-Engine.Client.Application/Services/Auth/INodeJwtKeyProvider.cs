namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Provider implementation for i node jwt key behavior.
/// </summary>
public interface INodeJwtKeyProvider : IDisposable
{
    ReadOnlyMemory<byte> SigningKey { get; }
}
