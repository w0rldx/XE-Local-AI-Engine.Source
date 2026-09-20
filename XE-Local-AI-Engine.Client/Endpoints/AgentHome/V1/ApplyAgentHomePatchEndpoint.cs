namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

using FastEndpoints;
using FluentValidation.Results;
using XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Lands the previewed patch on the host: the one route that writes to the operator's own folders, and so
///     reachable from nowhere but an authenticated operator session — no tool names
///     <see cref="INodePatchApplyService" />, so the model can neither call it nor ask for it.
/// </summary>
/// <remarks>
///     The approval is bound to what was read: <see cref="AgentHomePatchApplyRequest.PatchSha256" /> is the hash the
///     preview reported, and bytes that no longer hash to it are a 409 rather than a silent apply of a different
///     diff that happens to check clean. The full re-validation still runs underneath.
/// </remarks>
public sealed class ApplyAgentHomePatchEndpoint : Endpoint<AgentHomePatchApplyRequest, AgentHomePatchApplyResponse>
{
    /// <summary>
    ///     The error name a partially-applied result carries, so a client can tell "nothing happened" from "some of it
    ///     did" without reading prose. The general rejections stay unnamed, as everywhere else.
    /// </summary>
    private const string PartiallyAppliedErrorName = "partiallyApplied";

    private readonly INodePatchApplyService _service;

    public ApplyAgentHomePatchEndpoint(INodePatchApplyService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.AgentHomePatch.Apply);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(AgentHomePatchApplyRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var result = await _service.ApplyApprovedAsync(new NodePatchApplyRequest
            {
                RunId = req.RunId,
                ExpectedPatchSha256 = req.PatchSha256
            },
            ct);

        if (result.PatchMissing)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!result.Applied)
        {
            // The service's own strings, unedited: they are already redacted, and rewording them here would mean
            // maintaining a second vocabulary for the same refusals.
            foreach (var rejection in result.Rejections)
            {
                AddError(rejection);
            }

            if (result.PartiallyApplied)
            {
                // Folder-relative, like everything else the service reports. An operator whose apply died half way
                // needs to know WHICH folders already moved before they retry or revert.
                AddError(new ValidationFailure(PartiallyAppliedErrorName,
                    "Part of the patch was written to the host before the apply failed: "
                    + string.Join("; ", result.AppliedFiles.Select(file => $"{file.Alias}/{file.RelativePath}"))));
            }

            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);
            return;
        }

        await Send.OkAsync(result.ToResponse(), ct);
    }
}
