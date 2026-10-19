namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The three question types llama-server's <c>/v1/systemone</c> answers.</summary>
public enum LlamaServerSystemOneQuestionType
{
    /// <summary>One option of a named set, with per-option probabilities.</summary>
    Choice = 0,

    /// <summary>A single yes-likelihood in [0, 1] ("no-or-yes").</summary>
    Noul = 1,

    /// <summary>A continuous position on 2 to 10 ordered levels; it may fall between two levels.</summary>
    Score = 2
}
