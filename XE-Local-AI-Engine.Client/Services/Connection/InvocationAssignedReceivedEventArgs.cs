namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models;

public sealed class InvocationAssignedReceivedEventArgs : EventArgs
{
    public InvocationAssignedReceivedEventArgs(RuntimePackage runtimePackage)
    {
        RuntimePackage = runtimePackage ?? throw new ArgumentNullException(nameof(runtimePackage));
    }

    public RuntimePackage RuntimePackage { get; }
}
