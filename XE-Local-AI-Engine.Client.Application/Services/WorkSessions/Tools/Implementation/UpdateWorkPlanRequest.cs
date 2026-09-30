namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

internal sealed record UpdateWorkPlanRequest(IReadOnlyList<WorkPlanOperationRequest>? Operations);
