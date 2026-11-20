namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>How one <see cref="IKnowledgeIngestionService.RunAsync" /> ended, as far as the background worker must act on it.</summary>
public enum KnowledgeIngestionRunOutcome
{
    /// <summary>The run reached a terminal or skipped state; nothing is owed to it.</summary>
    Completed = 0,

    /// <summary>An operator eject vetoed the embedding model: the row is Pending and waiting, and the worker owes it a wake.</summary>
    Deferred = 1
}
