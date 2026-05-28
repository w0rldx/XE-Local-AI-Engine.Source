namespace XE_Local_AI_Engine.Client.Services.Auth;

public sealed class PairingTokenUsedException : PairingException
{
    public PairingTokenUsedException(string message)
        : base(message)
    {
    }
}
