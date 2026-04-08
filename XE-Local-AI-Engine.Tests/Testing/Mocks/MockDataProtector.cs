namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using Microsoft.AspNetCore.DataProtection;

public sealed class MockDataProtector : IDataProtector
{
    public IDataProtector CreateProtector(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return this;
    }

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return plaintext;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return protectedData;
    }
}
