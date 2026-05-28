namespace XE_Local_AI_Engine.Client.Services.Auth;

public sealed class PairingTokenExpiredException : PairingException
{
    public PairingTokenExpiredException(string message)
        : base(message)
    {
    }
}
