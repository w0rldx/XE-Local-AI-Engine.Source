namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Provenance for a repository that was created from a template.
/// </summary>
/// <remarks>
///     Keyed by the <em>selected folder</em> rather than by the project, because the materialization produces a folder
///     and the project is created from that folder afterwards — and because "these files came from that template at
///     that commit" is a property of the directory, not of any one project bound to it. The template path is copied
///     here rather than only referenced through <see cref="TemplateId" /> so that removing the template from the
///     registry does not erase where an existing project came from.
/// </remarks>
internal sealed class DevelopmentTemplateMaterialization
{
    public Guid SelectedFolderId { get; set; }

    public Guid TemplateId { get; set; }

    public string TemplateAlias { get; set; } = string.Empty;

    /// <summary>
    ///     UTF-8 host path bytes of the template this was cloned from, encrypted at rest exactly as
    ///     <see cref="DevelopmentTemplate.HostPath" /> is. A host path must never cross the API boundary.
    /// </summary>
    public byte[] TemplatePath { get; set; } = [];

    /// <summary>The template commit this was materialized from. This is the template's version.</summary>
    public string TemplateCommit { get; set; } = string.Empty;

    public long CreatedAtUtc { get; set; }
}
