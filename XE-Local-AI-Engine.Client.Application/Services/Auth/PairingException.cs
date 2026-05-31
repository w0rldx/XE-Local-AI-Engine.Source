namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Exception raised for pairing failures.
/// </summary>
public class PairingException : Exception
{
    public PairingException(string message)
        : base(message)
    {
    }
}
