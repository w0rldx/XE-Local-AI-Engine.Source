namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record SaveArtifactRequest(string? Name, string? MediaType, string? Kind, string? Text, string? Base64);

/// <summary>
///     <c>save_artifact</c>: the session's durable outputs.
///     <para>
///         Write order is load-bearing and is this handler's only guarantee: <b>blob first, row second</b>. A crash
///         between the two leaks one blob bounded by <c>MaxArtifactBytes</c>; the other order would leave a row pointing
///         at bytes that never existed, which nothing can recover from. Replacing an artifact of the same name is the
///         store's business — it hands back the superseded id and this handler sweeps those bytes after the commit.
///     </para>
/// </summary>
internal sealed class SaveArtifactToolHandler(IServiceScopeFactory scopeFactory,
    IOptions<WorkSessionOptions> options,
    IWorkSessionEventPublisher publisher,
    IWorkSessionArtifactBlobStore blobStore,
    ILogger<SaveArtifactToolHandler> logger) : WorkSessionToolHandler<SaveArtifactRequest>(scopeFactory, options, publisher, logger)
{
    private readonly IWorkSessionArtifactBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly ILogger<SaveArtifactToolHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override string ToolName => WorkSessionToolDefinitions.SaveArtifact.ToolName;

    public override string Description => WorkSessionToolDefinitions.SaveArtifact.Description;

    public override string ParameterSchema => WorkSessionToolDefinitions.SaveArtifact.ParameterSchema;

    protected override string ExampleArguments => WorkSessionToolDefinitions.SaveArtifact.ExampleArguments;

    protected override string? Validate(SaveArtifactRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return $"{ToolName} needs a non-empty 'name'.";
        }

        if (Exceeds(request.Name, WorkSessionToolDefinitions.NameMaxLength))
        {
            return Exceeded("name", WorkSessionToolDefinitions.NameMaxLength);
        }

        if (string.IsNullOrWhiteSpace(request.MediaType))
        {
            return $"{ToolName} needs a non-empty 'mediaType'.";
        }

        if (Exceeds(request.MediaType, WorkSessionToolDefinitions.NameMaxLength))
        {
            return Exceeded("mediaType", WorkSessionToolDefinitions.NameMaxLength);
        }

        if (string.IsNullOrWhiteSpace(request.Kind) || !Enum.TryParse<AgentWorkSessionArtifactKind>(request.Kind, out _))
        {
            return $"{ToolName} argument 'kind' must be one of Report, Note, File or Patch.";
        }

        var hasText = !string.IsNullOrEmpty(request.Text);
        var hasBase64 = !string.IsNullOrEmpty(request.Base64);
        return hasText == hasBase64
            ? $"{ToolName} needs exactly one of 'text' or 'base64'."
            : null;
    }

    protected override async Task<WorkSessionToolOutcome> ExecuteCoreAsync(SaveArtifactRequest request,
        AgentWorkSessionSnapshot session,
        IAgentWorkSessionStore store,
        CancellationToken cancellationToken)
    {
        byte[] content;
        if (!string.IsNullOrEmpty(request.Text))
        {
            content = Encoding.UTF8.GetBytes(request.Text);
        }
        else
        {
            try
            {
                content = Convert.FromBase64String(request.Base64!);
            }
            catch (FormatException)
            {
                return new WorkSessionToolOutcome($"{ToolName} argument 'base64' was not valid base64.");
            }
        }

        if (content.Length > Options.MaxArtifactBytes)
        {
            return new WorkSessionToolOutcome(string.Create(CultureInfo.InvariantCulture,
                $"{ToolName} content is {content.Length} bytes, over this node's {Options.MaxArtifactBytes}-byte limit. Save a shorter artifact, or split it."));
        }

        var artifactId = Guid.NewGuid();
        var written = await _blobStore.WriteAsync(session.Id, artifactId, content, cancellationToken).ConfigureAwait(false);
        var result = await store.AppendArtifactAsync(new AppendWorkSessionArtifactCommand(session.Id,
                    artifactId,
                    session.Version,
                    WorkSessionOperationId.For(session.Id, session.StepCount, $"artifact:{artifactId:N}"),
                    Enum.Parse<AgentWorkSessionArtifactKind>(request.Kind!),
                    request.Name!,
                    request.MediaType!,
                    written.ContentHash,
                    written.ByteCount,
                    written.OpaqueReference),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.SupersededArtifactId is { } supersededId)
        {
            // Best-effort by contract: the row is already gone, so a stubborn file is an orphan to sweep, not a failure
            // to hand back to the model.
            try
            {
                _blobStore.Delete(session.Id, supersededId);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Could not remove the replaced work session artifact blob {ArtifactId}.", supersededId);
            }
            catch (UnauthorizedAccessException exception)
            {
                _logger.LogWarning(exception, "Could not remove the replaced work session artifact blob {ArtifactId}.", supersededId);
            }
        }

        return new WorkSessionToolOutcome(string.Create(CultureInfo.InvariantCulture, $"Saved artifact '{request.Name}' ({written.ByteCount} bytes)."),
            result.Sequence,
            WorkSessionChangeKind.Artifact);
    }
}
