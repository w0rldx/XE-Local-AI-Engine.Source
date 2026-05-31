namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>Lifecycle state recorded in <c>manifest.json</c>.</summary>
internal enum AgentHomeStatus
{
    /// <summary>Layout creation is in progress; the layout may be partial.</summary>
    Initializing,

    /// <summary>Layout is complete and the AgentHome is usable.</summary>
    Ready
}
