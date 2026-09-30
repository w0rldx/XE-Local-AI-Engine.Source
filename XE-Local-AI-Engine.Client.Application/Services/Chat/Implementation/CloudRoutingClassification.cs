namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>Whether a model routes to a cloud provider, and whether that answer had to be assumed.</summary>
internal sealed record CloudRoutingClassification(bool RoutesToCloud, bool Faulted);
