namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Exception raised for pairing token expired failures.
/// </summary>
public sealed class PairingTokenExpiredException : PairingException
{
    public PairingTokenExpiredException(string message)
        : base(message)
    {
    }
}
