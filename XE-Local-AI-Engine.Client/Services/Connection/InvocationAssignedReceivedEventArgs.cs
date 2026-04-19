namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models.Encrypted;

public sealed class InvocationAssignedReceivedEventArgs : EventArgs
{
    public InvocationAssignedReceivedEventArgs(EncryptedRuntimePackageDto runtimePackage)
    {
        EncryptedRuntimePackage = runtimePackage ?? throw new ArgumentNullException(nameof(runtimePackage));
    }

    public EncryptedRuntimePackageDto EncryptedRuntimePackage { get; }
}
