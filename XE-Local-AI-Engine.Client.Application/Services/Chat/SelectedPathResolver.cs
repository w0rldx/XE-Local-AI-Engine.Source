namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Minimal, node-agnostic view of a single conversation message that the
///     <see cref="SelectedPathResolver" /> needs in order to resolve the selected linear path.
///     Callers map their own message type (node <c>NodeChatMessageResponse</c>, platform DTOs, etc.)
///     into this shape. Only the fields the resolution algorithm reads live here; the resolver returns
///     the caller's original objects, so no other message data needs to be projected.
/// </summary>
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
///     Standalone, dependency-free resolver that collapses a conversation's full message set into the
///     linear "selected path" — exactly one variant per variant group, ordered by sequence.
///     IMPORTANT: This component must stay node-agnostic so the platform side can reuse it. Do NOT add
///     references to node-only services (DbContext, persistence, SignalR, FastEndpoints, DTOs). It operates
///     solely on the <see cref="ISelectedPathMessage" /> abstraction plus the caller-supplied selection map.
///     Resolution rules:
///     <list type="bullet">
///         <item>Messages without a <c>VariantGroupId</c> are always included.</item>
///         <item>
///             For each variant group, include ONLY the selected variant. The selection comes from the
///             supplied map (<c>variantGroupId -&gt; selectedMessageId</c>); if the group has no recorded selection
///             (or the recorded id is no longer present), default to the NEWEST sibling — highest <c>Sequence</c>,
///             tie-broken by latest <c>CreatedAtUtc</c> then largest <c>MessageId</c>.
///         </item>
///         <item>Non-destructive: deselected siblings are simply omitted from the output, never mutated.</item>
///         <item>The output is ordered by <c>Sequence</c> ascending.</item>
///     </list>
/// </summary>
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
