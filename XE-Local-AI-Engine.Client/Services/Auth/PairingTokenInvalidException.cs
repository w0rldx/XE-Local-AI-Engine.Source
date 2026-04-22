namespace XE_Local_AI_Engine.Client.Services.Auth;

public sealed class PairingTokenInvalidException : PairingException
{
    public PairingTokenInvalidException(string message)
        : base(message)
    {
    }
}
