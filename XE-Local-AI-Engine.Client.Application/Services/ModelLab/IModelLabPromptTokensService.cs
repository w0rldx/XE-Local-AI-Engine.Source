namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>A Next token run's prompt, rendered by the model's chat template and tokenized by the llama-server already running it.</summary>
public interface IModelLabPromptTokensService
{
    /// <summary>Null when no llama-server runs <see cref="ModelLabPromptTokensQuery.ModelName" />: this never loads a model.</summary>
    Task<ModelLabPromptTokens?> GetPromptTokensAsync(ModelLabPromptTokensQuery query, CancellationToken cancellationToken);
}

/// <summary>The inputs of the Next token run whose prompt is tokenized.</summary>
public sealed class ModelLabPromptTokensQuery
{
    public required string ModelName { get; init; }

    public string? SystemPrompt { get; init; }

    public required string Prompt { get; init; }

    public required bool DisableThinking { get; init; }
}

public sealed class ModelLabPromptTokens
{
    /// <summary>The prompt after the chat template, as the server would feed it to the model.</summary>
    public required string RenderedPrompt { get; init; }

    /// <summary>The tokens in prompt order, at most the service's cap.</summary>
    public required IReadOnlyList<ModelLabPromptToken> Tokens { get; init; }

    /// <summary>The full count, which exceeds the list's length when <see cref="Truncated" />.</summary>
    public required int TokenCount { get; init; }

    public required bool Truncated { get; init; }
}

public sealed class ModelLabPromptToken
{
    public required int Id { get; init; }

    /// <summary>The token's text as the tokenizer decodes it on its own.</summary>
    public required string Piece { get; init; }

    /// <summary>A llama.cpp control token, such as a chat template's role or turn marker.</summary>
    public required bool Special { get; init; }
}
