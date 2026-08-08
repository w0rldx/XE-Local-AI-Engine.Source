namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Exception raised for pairing token used failures.
/// </summary>
public sealed class PairingTokenUsedException : PairingException
{
    public PairingTokenUsedException(string message)
        : base(message)
    {
    }
}
