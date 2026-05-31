namespace XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Provider implementation for i node operator secret behavior.
/// </summary>
public interface INodeOperatorSecretProvider
{
    byte[] GetOperatorSecret();
}
