namespace XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

using XE_Local_AI_Engine.Client.Models.Encrypted;

/// <summary>
///     Abstraction for runtime package envelope assembler behavior.
/// </summary>
public interface IRuntimePackageEnvelopeAssembler
{
    InvocationExecutionContext Assemble(EncryptedRuntimePackageDto package);
}
