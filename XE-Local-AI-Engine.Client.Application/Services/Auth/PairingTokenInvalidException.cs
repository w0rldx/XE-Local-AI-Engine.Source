namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Exception raised for pairing token invalid failures.
/// </summary>
public sealed class PairingTokenInvalidException : PairingException
{
    public PairingTokenInvalidException(string message)
        : base(message)
    {
    }
}
