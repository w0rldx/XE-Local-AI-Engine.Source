namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class NodeSelectedFolder
{
    public Guid Id { get; set; }

    public string Alias { get; set; } = string.Empty;

    /// <summary>
    ///     UTF-8 host path bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>host_path</c>.
    /// </summary>
    public byte[] HostPath { get; set; } = [];

    public SelectedFolderMode Mode { get; set; } = SelectedFolderMode.Copy;

    public long CreatedAtUtc { get; set; }

    public long? RevokedAtUtc { get; set; }
}
