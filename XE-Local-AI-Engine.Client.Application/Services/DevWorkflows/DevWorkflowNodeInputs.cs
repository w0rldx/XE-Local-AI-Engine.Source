namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text;
using System.Text.Json;

/// <summary>
///     A node run's <c>InputJson</c> document, and the one merge every writer of it goes through.
///     <para>
///         Two callers add members to it — the retry policy, which tells the next attempt what the last one failed
///         with, and the dispatcher, which tells it why a human retried it — and both must strip what the attempt
///         before them left behind. Written once here rather than twice: a member that is dropped by one merge and
///         carried by the other is a stale sentence handed to a model as if it were current.
///     </para>
/// </summary>
internal static class DevWorkflowNodeInputs
{
    /// <summary>What the operator said when they retried this node run, for the ONE attempt their decision started.</summary>
    public const string OperatorRetryReason = "operatorRetryReason";

    /// <summary>The attempt that reason belongs to, so a reader can tell it is about this try rather than an earlier one.</summary>
    public const string OperatorRetryAttempt = "operatorRetryAttempt";

    /// <summary>
    ///     Dropped by EVERY merge, never only by the one that writes them: an operator's reason is for the attempt
    ///     their decision started, so the next automatic re-attempt must not compose an objective around a complaint
    ///     nobody made about it.
    /// </summary>
    private static readonly string[] OperatorRetryMembers = [OperatorRetryReason, OperatorRetryAttempt];

    /// <summary>
    ///     The inputs with <paramref name="drop" /> and the operator-retry members removed, and whatever
    ///     <paramref name="write" /> adds written after them — so a merge replaces its own members rather than nesting
    ///     a round inside the one before it.
    /// </summary>
    public static string Merge(string? inputJson, Action<Utf8JsonWriter>? write, params string[] drop)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            using (var existing = Parse(inputJson))
            {
                if (existing is not null)
                {
                    foreach (var property in existing.RootElement.EnumerateObject()
                                                     .Where(property => !drop.Contains(property.Name, StringComparer.Ordinal)
                                                                        && !OperatorRetryMembers.Contains(property.Name, StringComparer.Ordinal)))
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            write?.Invoke(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes <paramref name="json" /> as a value if it parses as one, and as a plain string if it does not.</summary>
    public static void WriteJsonOrString(Utf8JsonWriter writer, string json)
    {
        ArgumentNullException.ThrowIfNull(writer);
        using var document = Parse(json);
        if (document is null)
        {
            writer.WriteStringValue(json);
            return;
        }

        document.RootElement.WriteTo(writer);
    }

    /// <summary>A JSON object, or null when there is none or the text is not one this can carry through.</summary>
    public static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The operator's retry reason these inputs carry for <paramref name="attempt" />, or nothing.</summary>
    public static string? OperatorRetryReasonFor(string? inputJson, int attempt)
    {
        using var document = Parse(inputJson);
        if (document is null
            || !document.RootElement.TryGetProperty(OperatorRetryReason, out var reason)
            || reason.ValueKind != JsonValueKind.String
            || reason.GetString() is not { } said
            || string.IsNullOrWhiteSpace(said))
        {
            return null;
        }

        // The attempt is what makes it THIS try's reason. A member without one is from a payload written before it
        // existed and is read as it used to be, which is as a reason for whatever attempt is carrying it.
        return !document.RootElement.TryGetProperty(OperatorRetryAttempt, out var carried)
               || carried.ValueKind != JsonValueKind.Number
               || !carried.TryGetInt32(out var number)
               || number == attempt
            ? said
            : null;
    }
}
