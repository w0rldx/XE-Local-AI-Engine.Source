namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

internal sealed record RecordFindingRequest(string? Kind, string? Text, string? SourceRef, string? TaskId, string? SupersedesId);
