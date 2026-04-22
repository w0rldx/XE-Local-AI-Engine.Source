namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models.Events;

public sealed class ApprovalResolvedReceivedEventArgs : EventArgs
{
    public ApprovalResolvedReceivedEventArgs(ApprovalResolvedEvent approvalResolution)
    {
        ApprovalResolution = approvalResolution ?? throw new ArgumentNullException(nameof(approvalResolution));
    }

    public ApprovalResolvedEvent ApprovalResolution { get; }
}
