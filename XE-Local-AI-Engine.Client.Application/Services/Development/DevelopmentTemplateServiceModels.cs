namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>A registered template as the API sees it: never a host path.</summary>
public sealed class DevelopmentTemplateReference
{
    public required string Id { get; init; }

    public required string Alias { get; init; }

    public required string Availability { get; init; }
}

/// <summary>
///     The result of materializing a template: the repository reference the project form will bind to, plus the
///     template commit that produced it.
/// </summary>
public sealed class DevelopmentTemplateMaterializationResult
{
    public required DevelopmentRepositoryReference Repository { get; init; }

    public required string TemplateAlias { get; init; }

    public required string TemplateCommit { get; init; }
}

public sealed class DevelopmentTemplateMaterializationException : InvalidOperationException
{
    public DevelopmentTemplateMaterializationException(string message) : base(message) { }
}
