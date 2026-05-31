namespace XE_Local_AI_Engine.HostAgent.Windows;

/// <summary>
///     Abstraction for host agent secret protector behavior.
/// </summary>
public interface IHostAgentSecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(byte[] protectedPayload);
}
