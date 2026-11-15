namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

/// <summary>The inputs of a finished Next token run, exactly as it sent them.</summary>
public sealed class ModelLabPromptTokensRequest
{
    public string ModelName { get; init; } = string.Empty;

    /// <summary>Left out, null or blank: the run had no system message.</summary>
    public string? SystemPrompt { get; init; }

    public string Prompt { get; init; } = string.Empty;

    /// <summary>The run's Thinking switch was off, so the template renders with thinking disabled.</summary>
    public bool DisableThinking { get; init; }
}

/// <summary>The rendered prompt and its tokens, measured by the llama-server that ran the model.</summary>
public sealed class ModelLabPromptTokensResponse
{
    public required string RenderedPrompt { get; init; }

    /// <summary>In prompt order; capped, see <see cref="Truncated" />.</summary>
    public required IReadOnlyList<ModelLabPromptTokenResponse> Tokens { get; init; }

    /// <summary>Every token in the prompt, including any cut from <see cref="Tokens" />.</summary>
    public required int TokenCount { get; init; }

    public required bool Truncated { get; init; }
}

public sealed class ModelLabPromptTokenResponse
{
    public required int Id { get; init; }

    public required string Piece { get; init; }

    /// <summary>A llama.cpp control token, such as a chat template's role or turn marker.</summary>
    public required bool Special { get; init; }
}
