namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

internal sealed record SaveArtifactRequest(string? Name, string? MediaType, string? Kind, string? Text, string? Base64);
