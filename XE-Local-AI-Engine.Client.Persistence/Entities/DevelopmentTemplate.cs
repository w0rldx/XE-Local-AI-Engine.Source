namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A registered project template: an ordinary Git repository the operator already has on this host, which new
///     Development projects can be materialized from.
/// </summary>
/// <remarks>
///     A template is not a scaffolding engine and carries no generated content of its own. It is a path plus a label,
///     and its version is whichever commit it happens to be at when a project is created from it — templates like
///     <c>XE-Framework</c> are living repositories, so a version number would be a lie.
/// </remarks>
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
