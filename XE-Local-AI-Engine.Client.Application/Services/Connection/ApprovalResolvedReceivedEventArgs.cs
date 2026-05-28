namespace XE_Local_AI_Engine.Client.Services.Connection;

public sealed class ApprovalResolvedReceivedEventArgs : EventArgs
{
    public ApprovalResolvedReceivedEventArgs(ApprovalResolvedEvent approvalResolution)
    {
        ApprovalResolution = approvalResolution ?? throw new ArgumentNullException(nameof(approvalResolution));
    }

    public ApprovalResolvedEvent ApprovalResolution { get; }
}
