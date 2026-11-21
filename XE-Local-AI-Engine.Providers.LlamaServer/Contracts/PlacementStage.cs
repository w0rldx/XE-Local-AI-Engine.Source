namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Where in a chat admission a live re-placement runs, relative to the window ladder.</summary>
public enum PlacementStage
{
    /// <summary>Before the ladder: expert offload only, for a model with experts.</summary>
    BeforeLadder = 0,

    /// <summary>After the ladder found no window: a Hybrid at the live GPU share.</summary>
    AfterLadder = 1
}
