namespace XE_Local_AI_Engine.HostAgent.Windows;

public interface IHostAgentSecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(byte[] protectedPayload);
}
