namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
///     The deterministic message ids a chat-bound run writes: name-based (version 5) UUIDs, RFC 4122 §4.3. The same
///     inputs always name the same message, which is what makes a replayed send and a re-ticked publish idempotent.
/// </summary>
internal static class GraphWorkflowChatIds
{
    /// <summary>The user message a send persists.</summary>
    public static Guid UserMessage(Guid requestId) =>
        NameBased(requestId, "user");

    /// <summary>
    ///     The user message a steer persists, scoped to its target: the steer's idempotency is per node run, so an
    ///     operation id reused on another node or run is another steer and gets its own message.
    /// </summary>
    public static Guid SteerMessage(Guid operationId, Guid runId, string nodeKey) =>
        NameBased(operationId, string.Create(CultureInfo.InvariantCulture, $"steer/{runId}/{nodeKey}"));

    /// <summary>The chat message one succeeded attempt of a node publishes.</summary>
    public static Guid PublishedMessage(Guid runId, string nodeKey, int attempt) =>
        NameBased(runId, string.Create(CultureInfo.InvariantCulture, $"{nodeKey}/{attempt}"));

    /// <summary>SHA-1 of the namespace bytes (network order) and the UTF-8 name, stamped version 5 and the RFC variant.</summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 4122 version 5 is DEFINED over SHA-1; this derives an identifier, not a security property.")]
    [SuppressMessage("Security", "S4790:Using weak hashing algorithms is security-sensitive",
        Justification = "RFC 4122 version 5 is DEFINED over SHA-1; this derives an identifier, not a security property.")]
    internal static Guid NameBased(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        _ = namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

        var hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
