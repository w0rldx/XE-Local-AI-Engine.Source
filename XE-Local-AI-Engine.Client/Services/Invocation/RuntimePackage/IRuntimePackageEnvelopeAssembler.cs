namespace XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;

using XE_Local_AI_Engine.Client.Models.Encrypted;

public interface IRuntimePackageEnvelopeAssembler
{
    InvocationExecutionContext Assemble(EncryptedRuntimePackageDto package);
}
