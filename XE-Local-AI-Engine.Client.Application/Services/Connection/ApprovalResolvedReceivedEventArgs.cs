namespace XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Event payload for approval resolved received notifications.
/// </summary>
public sealed class ApprovalResolvedReceivedEventArgs : EventArgs
{
    public ApprovalResolvedReceivedEventArgs(ApprovalResolvedEvent approvalResolution)
    {
        ApprovalResolution = approvalResolution ?? throw new ArgumentNullException(nameof(approvalResolution));
    }

    public ApprovalResolvedEvent ApprovalResolution { get; }
}
