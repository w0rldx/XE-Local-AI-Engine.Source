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

/// <summary>
///     Standalone, dependency-free resolver collapsing a conversation's full message set into the linear "selected
///     path": exactly one variant per group, ordered by ascending sequence.
/// </summary>
/// <remarks>
///     It must stay node-agnostic so the platform side can reuse it: no reference to a node-only service, and it
///     operates solely on <see cref="ISelectedPathMessage" /> plus the caller's selection map. An ungrouped message
///     is always included; a group contributes ONLY its selected variant, defaulting to the NEWEST sibling, and a
///     deselected sibling is omitted, never mutated. Raw <c>Sequence</c> ordering holds only while no group has a
///     late-minted sibling, so context builders re-order through <see cref="CreateAnchorResolver{TMessage}" />.
/// </remarks>
public static class SelectedPathResolver
{
    /// <summary>
    ///     Resolves the selected linear path from <paramref name="messages" /> using <paramref name="selection" />.
    /// </summary>
    /// <typeparam name="TMessage">The caller's message type, adapted to <see cref="ISelectedPathMessage" />.</typeparam>
    /// <param name="messages">All conversation messages (any order). Not mutated.</param>
    /// <param name="selection">
    ///     Map of <c>variantGroupId -&gt; selectedMessageId</c>. May be <c>null</c> or empty, in which case every
    ///     group falls back to its newest sibling.
    /// </param>
    /// <returns>The selected path, ordered by <see cref="ISelectedPathMessage.Sequence" /> ascending.</returns>
    public static IReadOnlyList<TMessage> Resolve<TMessage>(IEnumerable<TMessage> messages,
        IReadOnlyDictionary<Guid, Guid>? selection)
        where TMessage : ISelectedPathMessage
    {
        ArgumentNullException.ThrowIfNull(messages);

        var all = messages as IReadOnlyList<TMessage> ?? messages.ToList();
        if (all.Count == 0)
        {
            return [];
        }

        // Group the variant siblings up front so we resolve each group exactly once.
        var groups = new Dictionary<Guid, List<TMessage>>();
        foreach (var message in all)
        {
            if (message.VariantGroupId is { } groupId)
            {
                if (!groups.TryGetValue(groupId, out var siblings))
                {
                    siblings = [];
                    groups[groupId] = siblings;
                }

                siblings.Add(message);
            }
        }

        var selected = new List<TMessage>(all.Count);
        var emittedGroups = new HashSet<Guid>();

        foreach (var message in all)
        {
            if (message.VariantGroupId is not { } groupId)
            {
                selected.Add(message);
                continue;
            }

            // Emit a group's chosen variant once, regardless of which sibling we encounter first.
            if (!emittedGroups.Add(groupId))
            {
                continue;
            }

            var chosen = ChooseVariant(groups[groupId], selection, groupId);
            selected.Add(chosen);
        }

        selected.Sort(static (left, right) =>
        {
            var bySequence = left.Sequence.CompareTo(right.Sequence);
            return bySequence != 0 ? bySequence : left.CreatedAtUtc.CompareTo(right.CreatedAtUtc);
        });

        return selected;
    }

    /// <summary>
    ///     Builds the ANCHOR lookup for <paramref name="messages" />: each message's logical position, which is its
    ///     variant group's EARLIEST member sequence, or its own when it is ungrouped.
    /// </summary>
    /// <typeparam name="TMessage">The caller's message type, adapted to <see cref="ISelectedPathMessage" />.</typeparam>
    /// <param name="messages">ALL conversation messages, including the siblings the selected path omits.</param>
    /// <returns>A lookup from message to anchor sequence, falling back to a message's own sequence.</returns>
    /// <remarks>
    ///     Callers order and filter the resolved path by this, never by the chosen sibling's own <c>Sequence</c>,
    ///     which is a physical insertion counter: regenerating an EARLY turn after later turns exist mints a sibling
    ///     whose sequence lands past them, so raw ordering puts it at the tail and a <c>Sequence &lt;= cutoff</c>
    ///     filter drops it. The anchor is a property of the whole group, which is why every sibling must be passed.
    /// </remarks>
    public static Func<TMessage, int> CreateAnchorResolver<TMessage>(IEnumerable<TMessage> messages)
        where TMessage : ISelectedPathMessage
    {
        ArgumentNullException.ThrowIfNull(messages);

        var anchorByGroup = new Dictionary<Guid, int>();
        foreach (var message in messages)
        {
            if (message.VariantGroupId is not { } groupId)
            {
                continue;
            }

            anchorByGroup[groupId] = anchorByGroup.TryGetValue(groupId, out var anchor)
                ? Math.Min(anchor, message.Sequence)
                : message.Sequence;
        }

        return message => message.VariantGroupId is { } groupId && anchorByGroup.TryGetValue(groupId, out var anchor)
            ? anchor
            : message.Sequence;
    }

    private static TMessage ChooseVariant<TMessage>(IReadOnlyList<TMessage> siblings,
        IReadOnlyDictionary<Guid, Guid>? selection,
        Guid groupId)
        where TMessage : ISelectedPathMessage
    {
        if (selection is not null
            && selection.TryGetValue(groupId, out var selectedMessageId))
        {
            foreach (var sibling in siblings)
            {
                if (sibling.MessageId == selectedMessageId)
                {
                    return sibling;
                }
            }
        }

        // No (valid) recorded selection: default to the newest sibling.
        var newest = siblings[0];
        for (var index = 1; index < siblings.Count; index++)
        {
            if (IsNewer(siblings[index], newest))
            {
                newest = siblings[index];
            }
        }

        return newest;
    }

    private static bool IsNewer<TMessage>(TMessage candidate, TMessage current)
        where TMessage : ISelectedPathMessage
    {
        if (candidate.Sequence != current.Sequence)
        {
            return candidate.Sequence > current.Sequence;
        }

        if (candidate.CreatedAtUtc != current.CreatedAtUtc)
        {
            return candidate.CreatedAtUtc > current.CreatedAtUtc;
        }

        return candidate.MessageId.CompareTo(current.MessageId) > 0;
    }
}
