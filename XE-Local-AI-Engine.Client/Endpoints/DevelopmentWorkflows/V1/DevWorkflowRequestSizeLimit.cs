namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using System.Globalization;
using Microsoft.AspNetCore.Http.Metadata;

/// <summary>
///     The request-body cap for the two routes that carry a definition graph — create and update.
/// </summary>
/// <remarks>
///     Without one they inherit Kestrel's 30 MB default, and a body that size is bound, parsed and walked by the
///     runtime's own parser before the node cap ever gets a chance to refuse it. Two mechanisms, as on the
///     graph-workflow side: the metadata is what the HOST enforces before the body is read, and
///     <see cref="IsOversized" /> is the cheap early exit the handler makes for itself. The metadata is Kestrel-side,
///     so the in-memory test host neither honours nor disproves it; the early exit is provable without a connection.
/// </remarks>
internal sealed class DevWorkflowRequestSizeLimit : IRequestSizeLimitMetadata
{
    /// <summary>2 MiB — twice the graph-workflow cap, proportionate to the larger node cap.</summary>
    /// <remarks>
    ///     The two shipped definitions are 3 KB over 3 nodes and 10 KB over 11, so roughly 900 bytes a node: the
    ///     500-node cap therefore describes a definition of well under half a megabyte even with per-node instructions
    ///     on every one of them. A body over this is not a definition an editor drew.
    /// </remarks>
    public const long MaxBytes = 2 * 1024 * 1024;

    public long? MaxRequestBodySize => MaxBytes;

    /// <summary>
    ///     What an oversized request is told, written as the <c>detail</c> of the shared
    ///     <c>RequestBodyTooLargeProblem</c> body.
    /// </summary>
    /// <remarks>
    ///     That is the SAME problem+json shape the host's own refusal writes and the one these routes declare, so the
    ///     caller parses one shape whichever half refused.
    /// </remarks>
    public static readonly string OversizedDetail =
        string.Create(CultureInfo.InvariantCulture,
            $"The request body is larger than the {MaxBytes / (1024 * 1024)} MB this node accepts for a workflow definition.");

    /// <summary>
    ///     Whether the request DECLARES more body than this node accepts.
    /// </summary>
    /// <remarks>
    ///     Content-Length is never the limit — a caller can omit or lie about it — which is what the metadata above is
    ///     for; this is the layer that answers with a message an operator can read instead of a bare host refusal. An
    ///     ABSENT Content-Length is therefore NOT a refusal: a chunked body declares no length at all, and reading that
    ///     null as "over the cap" would answer 413 to every streamed request, about a size nobody ever stated. The
    ///     streamed case belongs to the metadata above, which counts the bytes as they arrive.
    /// </remarks>
    public static bool IsOversized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ContentLength is > MaxBytes;
    }
}
