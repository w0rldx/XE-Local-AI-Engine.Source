namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Minimal, node-agnostic view of one conversation message, holding only the fields
///     <see cref="SelectedPathResolver" /> reads to resolve the selected linear path.
/// </summary>
/// <remarks>
///     Callers map their own message type into this shape, and the resolver returns their original objects, so no
///     other message data needs projecting.
/// </remarks>
public interface ISelectedPathMessage
{
    /// <summary>Stable identifier of the message.</summary>
    Guid MessageId { get; }

    /// <summary>Linear order within the conversation; the output is ordered by this ascending.</summary>
    int Sequence { get; }

    /// <summary>
    ///     Group that ties variant siblings together. <c>null</c> for ordinary single messages,
    ///     which are always included in the path.
    /// </summary>
    Guid? VariantGroupId { get; }

    /// <summary>
    ///     Creation timestamp (epoch millis) used only as a deterministic tie-break when selecting the
    ///     default (newest) sibling of a variant group and two siblings share the same <see cref="Sequence" />.
    /// </summary>
    long CreatedAtUtc { get; }
}
