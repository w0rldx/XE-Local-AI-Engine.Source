namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A registered project template: an ordinary Git repository the operator already has on this host, which new
///     Development projects can be materialized from.
///     <para>
///         A template is not a scaffolding engine and carries no generated content of its own. It is a path plus a
///         label, and its version is whichever commit it happens to be at when a project is created from it — templates
///         like <c>XE-Framework</c> are living repositories, so a version number would be a lie.
///     </para>
/// </summary>
internal sealed class DevelopmentTemplate
{
    public Guid Id { get; set; }

    public string Alias { get; set; } = string.Empty;

    /// <summary>
    ///     UTF-8 host path bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>host_path</c> — the same
    ///     treatment <see cref="NodeSelectedFolder" /> gives its path, because it is the same class of value.
    /// </summary>
    public byte[] HostPath { get; set; } = [];

    public long CreatedAtUtc { get; set; }

    public long Version { get; set; }
}

/// <summary>
///     Provenance for a repository that was created from a template.
///     <para>
///         Keyed by the <em>selected folder</em> rather than by the project, because the materialization produces a
///         folder and the project is created from that folder afterwards — and because the fact "these files came from
///         that template at that commit" is a property of the directory, not of any one project bound to it.
///     </para>
///     <para>
///         The template path is copied here rather than only referenced through <see cref="TemplateId" /> so that
///         removing the template from the registry does not erase where an existing project came from.
///     </para>
/// </summary>
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
