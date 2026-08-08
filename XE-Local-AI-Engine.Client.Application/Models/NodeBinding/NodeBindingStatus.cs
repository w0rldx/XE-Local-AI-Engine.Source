namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

/// <summary>
///     Enumerates supported node binding status values.
/// </summary>
public enum NodeBindingStatus
{
    NotStarted,
    Pending,
    Approved,
    Consumed,
    Expired,
    Denied,
    Cancelled,
    Failed
}
