namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>A registered template as the API sees it: never a host path.</summary>
public sealed record DevelopmentTemplateReference(string Id, string Alias, string Availability);

/// <summary>
///     The result of materializing a template: the repository reference the project form will bind to, plus the
///     template commit that produced it.
/// </summary>
public sealed record DevelopmentTemplateMaterializationResult(
    DevelopmentRepositoryReference Repository,
    string TemplateAlias,
    string TemplateCommit);

public sealed class DevelopmentTemplateMaterializationException : InvalidOperationException
{
    public DevelopmentTemplateMaterializationException(string message) : base(message) { }
}
