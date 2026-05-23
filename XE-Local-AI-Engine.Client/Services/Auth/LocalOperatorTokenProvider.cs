namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Cryptography;

public sealed class LocalOperatorTokenProvider : ILocalOperatorTokenProvider
{
    public LocalOperatorTokenProvider()
    {
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public string Token { get; }
}
