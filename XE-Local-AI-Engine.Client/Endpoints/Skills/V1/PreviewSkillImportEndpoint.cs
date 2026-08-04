namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Http.Metadata;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Phase 1 of the third-party skill import: parse, guard and report, writing nothing. The operator reviews the
///     returned report and only then calls <see cref="CommitSkillImportEndpoint" /> with its token.
/// </summary>
/// <remarks>
///     Every input here is attacker-authored — an archive an operator was handed, a repository anyone can publish to.
///     The dry run is the point: no row exists until a human has seen what the content actually says.
/// </remarks>
public sealed class PreviewSkillImportEndpoint(ISkillImportService importService, SkillImportOptions options)
    : Endpoint<SkillImportPreviewRequest, SkillImportPreviewResponse>
{
    private readonly ISkillImportService _importService = importService ?? throw new ArgumentNullException(nameof(importService));
    private readonly int _maxArchiveBytes = (options ?? throw new ArgumentNullException(nameof(options))).MaxArchiveBytes;

    public override void Configure()
    {
        Post(LocalApiRoutes.Skills.ImportPreview);
        AllowFileUploads();
        // Declare the multipart body so FastEndpoints documents it in OpenAPI (and the request is not rejected with a
        // 415 for lacking a JSON body). All three sources ride the same form: one content type, one binding path.
        Description(builder => builder.Accepts<SkillImportPreviewRequest>("multipart/form-data"));
        // Kestrel's default body cap (30 MB) sits BELOW the configured archive cap, so without this the server would
        // refuse an archive the import pipeline is configured to accept — and refuse it with a bare 413 from the host,
        // nowhere near a message an operator can act on. The handler still checks the length itself: this metadata is
        // only honoured where a body-size feature exists.
        Options(builder => builder.WithMetadata(new SkillImportRequestSizeLimit(_maxArchiveBytes)));
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SkillImportPreviewRequest req, CancellationToken ct)
    {
        try
        {
            var preview = req.Source switch
            {
                SkillImportSourceKind.Upload => await PreviewUploadAsync(req, ct).ConfigureAwait(false),
                SkillImportSourceKind.Paste => await PreviewPasteAsync(req, ct).ConfigureAwait(false),
                SkillImportSourceKind.GitHub => await PreviewGitHubAsync(req, ct).ConfigureAwait(false),
                _ => UnknownSource()
            };

            if (preview is null)
            {
                // A null return means the request did not carry the payload its source names; each branch has already
                // put the specific error on the response.
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            }

            await Send.OkAsync(preview.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (SkillImportException exception)
        {
            // Every guard in the pipeline fails closed through this exception and its message is written to be shown:
            // it names the rule that was broken and never echoes an entry path, a resource name or any imported text.
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }

    private SkillImportPreview? UnknownSource()
    {
        AddError("The import source is not recognised.");
        return null;
    }

    private async Task<SkillImportPreview?> PreviewUploadAsync(SkillImportPreviewRequest req, CancellationToken ct)
    {
        var file = req.File ?? (Files.Count > 0 ? Files[0] : null);
        if (file is null || file.Length == 0)
        {
            AddError("An archive file is required.");
            return null;
        }

        if (file.Length > _maxArchiveBytes)
        {
            AddError($"The archive exceeds the maximum import size of {_maxArchiveBytes / (1024 * 1024)} MB.");
            return null;
        }

        await using var upload = file.OpenReadStream();
        using var buffer = new MemoryStream(capacity: (int)file.Length);
        await upload.CopyToAsync(buffer, ct).ConfigureAwait(false);

        return await _importService.PreviewArchiveAsync(buffer.ToArray(), ct).ConfigureAwait(false);
    }

    private async Task<SkillImportPreview?> PreviewPasteAsync(SkillImportPreviewRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Markdown))
        {
            AddError("A SKILL.md document is required.");
            return null;
        }

        return await _importService.PreviewMarkdownAsync(req.Markdown, ct).ConfigureAwait(false);
    }

    private async Task<SkillImportPreview?> PreviewGitHubAsync(SkillImportPreviewRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Owner) || string.IsNullOrWhiteSpace(req.Repository))
        {
            AddError("A repository owner and name are required.");
            return null;
        }

        return await _importService.PreviewGitHubRepositoryAsync(req.Owner, req.Repository, ct).ConfigureAwait(false);
    }
}

/// <summary>Endpoint metadata raising this route's request-body cap to the configured archive cap.</summary>
internal sealed class SkillImportRequestSizeLimit(long maxRequestBodySize) : IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize { get; } = maxRequestBodySize;
}
