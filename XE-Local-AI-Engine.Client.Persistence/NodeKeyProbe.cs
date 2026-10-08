namespace XE_Local_AI_Engine.Client.Persistence;

using System.Security.Cryptography;

/// <summary>
///     Counts, for the node-key check, how many sampled rows a key really decrypted and how many it failed on.
/// </summary>
/// <remarks>
///     A row counts as decrypted only when one of its byte columns changed: a row with no ciphertext, or a legacy
///     plaintext row the read-both envelope copies through unchanged, proves nothing about the key.
/// </remarks>
internal sealed class NodeKeyProbe
{
    public int Decrypted { get; private set; }

    public int Failed { get; private set; }

    public void Observe(object entity, Action decrypt)
    {
        var columns = entity.GetType().GetProperties().Where(static property => property.PropertyType == typeof(byte[]) && property.CanRead).ToArray();
        var before = columns.Select(column => (byte[]?)column.GetValue(entity)).ToArray();
        try
        {
            decrypt();
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            Failed++;
            return;
        }

        for (var index = 0; index < columns.Length; index++)
        {
            if (before[index] is { } stored && columns[index].GetValue(entity) is byte[] read && !stored.AsSpan().SequenceEqual(read))
            {
                Decrypted++;
                return;
            }
        }
    }
}
