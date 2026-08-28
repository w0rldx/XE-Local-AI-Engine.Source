namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
///     Derives the deterministic operation id a dispatcher write stamps on its event row, mirroring
///     <c>WorkSessionOperationId</c> one level up. The phase rides INSIDE the id, so the store's two-column
///     <c>(run_id, operation_id)</c> idempotency holds one row per phase with no phase column, and a tick replayed after
///     a crash short-circuits on the query-first path instead of appending a second event.
///     <para>
///         Only writes whose command demands an operation id use this. An ordinary status move does not: it passes the
///         <c>Any</c> version sentinel and no operation id, because it has no lost update to protect against and a
///         replayed tick re-derives the same answer from rows that did not change.
///     </para>
/// </summary>
internal static class DevWorkflowOperationId
{
    public static Guid For(Guid runId, string nodeKey, int attempt, string phase)
    {
        ArgumentNullException.ThrowIfNull(nodeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        var material = string.Create(CultureInfo.InvariantCulture, $"dev-workflow:{runId:N}:{nodeKey}:{attempt}:{phase}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(start: 0, length: 16));
    }
}
