namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1.Mappers;

using XE_Local_AI_Engine.Client.Models.NodeBinding;

internal static class NodeBindingMapper
{
    public static NodeBindingSessionResponse ToResponse(this NodeBindingSession session)
    {
        return new NodeBindingSessionResponse
        {
            DeviceCode = session.DeviceCode,
            UserCode = session.UserCode,
            VerificationUri = session.VerificationUri,
            VerificationUriComplete = session.VerificationUriComplete,
            ExpiresAt = session.ExpiresAt,
            IntervalSeconds = session.IntervalSeconds,
            Status = ToWireStatus(session.Status)
        };
    }

    public static PollNodeBindingSessionResponse ToResponse(this PollNodeBindingResponse response)
    {
        return new PollNodeBindingSessionResponse
        {
            Status = response.Status,
            IntervalSeconds = response.IntervalSeconds,
            ExpiresAt = response.ExpiresAt
        };
    }

    public static NodeBindingSession ToSession(this PollNodeBindingSessionRequest request)
    {
        return new NodeBindingSession
        {
            DeviceCode = request.DeviceCode,
            UserCode = request.UserCode,
            VerificationUri = request.VerificationUri,
            VerificationUriComplete = request.VerificationUriComplete,
            ExpiresAt = request.ExpiresAt,
            IntervalSeconds = request.IntervalSeconds,
            Status = NodeBindingStatus.Pending
        };
    }

    private static string ToWireStatus(NodeBindingStatus status)
    {
        return status switch
        {
            NodeBindingStatus.Pending => "pending",
            NodeBindingStatus.Approved => "approved",
            NodeBindingStatus.Consumed => "consumed",
            NodeBindingStatus.Expired => "expired",
            NodeBindingStatus.Denied => "denied",
            _ => "failed"
        };
    }
}
