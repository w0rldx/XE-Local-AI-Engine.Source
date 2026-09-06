namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     Projects the invocation service's descriptors onto the wire. A rename and nothing else: the descriptor list is
///     already the D6-filtered set, so filtering here would be a second implementation of the envelope able to
///     disagree with the one the runtime enforces.
/// </summary>
internal static class GraphWorkflowToolMapper
{
    public static GraphWorkflowToolResponse ToResponse(this InvocableToolDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowToolResponse(value.Name, value.Description, value.ParameterSchema);
    }
}
