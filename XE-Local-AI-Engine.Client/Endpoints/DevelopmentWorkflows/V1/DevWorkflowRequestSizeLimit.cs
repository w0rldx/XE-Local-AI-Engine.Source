namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Http.Metadata;

/// <summary>
///     The request-body cap for the two routes that carry a definition graph — create and update. Without one they
///     inherit Kestrel's 30 MB default, and a body that size is bound, parsed and walked by the runtime's own parser
///     before the node cap ever gets a chance to refuse it.
///     <para>
///         Two mechanisms, as on the graph-workflow side: the metadata is what the HOST enforces before the body is
///         read, and <see cref="RefuseIfOversized" /> is the cheap early exit the handler makes for itself. The
///         metadata is Kestrel-side, so the in-memory test host neither honours nor disproves it; the early exit is the
///         half that is provable without a real connection.
///     </para>
/// </summary>
internal sealed class DevWorkflowRequestSizeLimit : IRequestSizeLimitMetadata
{
    /// <summary>
    ///     2 MiB — twice the graph-workflow cap, proportionate to the larger node cap. The two shipped definitions are
    ///     3 KB over 3 nodes and 10 KB over 11, so roughly 900 bytes a node; the 500-node cap therefore describes a
    ///     definition of well under half a megabyte even with per-node instructions on every one of them. A body over
    ///     this is not a definition an editor drew.
    /// </summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    public long? MaxRequestBodySize => MaxBytes;

    /// <summary>
    ///     Whether the request DECLARES more body than this node accepts, recording the refusal on
    ///     <paramref name="errors" /> when it does. Content-Length is never the limit — a caller can omit or lie about
    ///     it — which is what the metadata above is for; this is the layer that answers with a message an operator can
    ///     read instead of a bare host refusal.
    ///     <para>
    ///         An ABSENT Content-Length is therefore NOT a refusal. A chunked body declares no length at all, and
    ///         reading that null as "over the cap" would answer 413 to every streamed request, about a size nobody ever
    ///         stated. The streamed case belongs to the metadata above, which counts the bytes as they arrive.
    ///     </para>
    /// </summary>
    public static bool RefuseIfOversized(HttpRequest request, IValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(errors);

        if (request.ContentLength is not > MaxBytes)
        {
            return false;
        }

        errors.AddError($"The request body is larger than the {MaxBytes / (1024 * 1024)} MB this node accepts for a workflow definition.");
        return true;
    }
}
