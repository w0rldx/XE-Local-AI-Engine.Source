namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using System.Text.Json;

internal sealed record CompleteWorkSessionRequest(string? Summary, bool? ObjectiveMet = null, JsonElement? Output = null);
