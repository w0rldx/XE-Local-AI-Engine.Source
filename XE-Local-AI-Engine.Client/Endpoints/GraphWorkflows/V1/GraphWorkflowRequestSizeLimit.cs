namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using System.Globalization;
using Microsoft.AspNetCore.Http.Metadata;

/// <summary>
///     The request-body cap for the four routes that carry a document — create, update and validate, which carry a
///     graph, and start-run, which carries an input. Without one they inherit Kestrel's 30 MB default, and a body that
///     size is parsed, walked by the runtime's parser and hashed before the node cap ever gets a chance to refuse it.
///     <para>
///         Two mechanisms, as elsewhere in this tree: the metadata is what the HOST enforces before the body is read,
///         and <see cref="IsOversized" /> is the cheap early exit the handler makes for itself. The metadata is
///         Kestrel-side, so the in-memory test host neither honours nor disproves it; the early exit is the half that
///         is provable without a real connection.
///     </para>
/// </summary>
internal sealed class GraphWorkflowRequestSizeLimit : IRequestSizeLimitMetadata
{
    /// <summary>
    ///     1 MiB. A 200-node graph carrying per-node instructions and response schemas is tens of KiB, and even the
    ///     option ceiling of 10 000 nodes fits inside this with room to spare — so a body over it is not a graph an
    ///     editor drew.
    /// </summary>
    public const long MaxBytes = 1024 * 1024;

    public long? MaxRequestBodySize => MaxBytes;

    /// <summary>
    ///     What an oversized request is told, written as the <c>detail</c> of the shared
    ///     <c>RequestBodyTooLargeProblem</c> body — the SAME problem+json shape the host's own refusal writes and the
    ///     one these routes declare, so the caller parses one shape whichever half refused.
    /// </summary>
    public static readonly string OversizedDetail =
        string.Create(CultureInfo.InvariantCulture,
            $"The request body is larger than the {MaxBytes / (1024 * 1024)} MB this node accepts for a graph.");

    /// <summary>
    ///     Whether the request DECLARES more body than this node accepts. Content-Length is never the limit — a
    ///     caller can omit or lie about it — which is what the metadata above is for; this is the layer that answers
    ///     with a message an operator can read instead of a bare host refusal.
    ///     <para>
    ///         An ABSENT Content-Length is therefore NOT a refusal. A chunked body declares no length at all, and
    ///         reading that null as "over the cap" would answer 413 to every streamed request, about a size nobody
    ///         ever stated. The streamed case belongs to the metadata above, which counts the bytes as they arrive.
    ///     </para>
    /// </summary>
    public static bool IsOversized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ContentLength is > MaxBytes;
    }
}
