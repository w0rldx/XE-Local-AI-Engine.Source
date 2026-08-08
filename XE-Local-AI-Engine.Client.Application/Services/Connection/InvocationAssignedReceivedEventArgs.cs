namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Event payload for invocation assigned received notifications.
/// </summary>
public sealed class InvocationAssignedReceivedEventArgs : EventArgs
{
    public InvocationAssignedReceivedEventArgs(EncryptedRuntimePackageDto runtimePackage)
    {
        EncryptedRuntimePackage = runtimePackage ?? throw new ArgumentNullException(nameof(runtimePackage));
        Envelope = new InvocationAssignedEnvelope
        {
            StorageMode = "EncryptedSync",
            Plain = null,
            Encrypted = runtimePackage
        };
    }

    public InvocationAssignedReceivedEventArgs(InvocationAssignedEnvelope envelope)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        EncryptedRuntimePackage = envelope.Encrypted!;
    }

    public EncryptedRuntimePackageDto EncryptedRuntimePackage { get; }

    public InvocationAssignedEnvelope Envelope { get; }
}
