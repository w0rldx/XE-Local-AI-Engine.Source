namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

/// <summary><c>read_upstream_artifact</c>: one page of an artifact this workflow step was handed, read back past what its objective could inline.</summary>
/// <remarks>
///     It reads only what the step's turn scope carries, which the workflow lane built from the node run's recorded consumption, so
///     no argument can reach another run, node or path. The bytes are verified against the recorded digest and size exactly as the
///     objective's own read is, and a mismatch is a refusal, never content. See docs/wiki/25-dev-workflows.md ("Upstream artifacts in the objective").
/// </remarks>
internal sealed class ReadUpstreamArtifactToolHandler : WorkSessionToolHandler<ReadUpstreamArtifactRequest>
{
    private const string EndOfArtifact = "null (end of artifact)";

    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly int _configuredResultCharacters;

    public ReadUpstreamArtifactToolHandler(IServiceScopeFactory scopeFactory,
        IOptions<WorkSessionOptions> options,
        INodeRuntimeSettings runtimeSettings,
        IWorkSessionEventPublisher publisher,
        IDevWorkflowArtifactBlobStore blobs,
        IOptions<AgentToolPipelineOptions> pipelineOptions,
        ILogger<ReadUpstreamArtifactToolHandler> logger) : base(scopeFactory, options, runtimeSettings, publisher, logger)
    {
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _configuredResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
    }

    public override string ToolName => WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName;

    public override string Description => WorkSessionToolDefinitions.ReadUpstreamArtifact.Description;

    public override string ParameterSchema => WorkSessionToolDefinitions.ReadUpstreamArtifact.ParameterSchema;

    protected override string ExampleArguments => WorkSessionToolDefinitions.ReadUpstreamArtifact.ExampleArguments;

    protected override string? Validate(ReadUpstreamArtifactRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return $"{ToolName} needs the 'name' of an upstream artifact, exactly as the objective lists it.";
        }

        if (Exceeds(request.Name, WorkSessionToolDefinitions.ReferenceMaxLength))
        {
            return Exceeded("name", WorkSessionToolDefinitions.ReferenceMaxLength);
        }

        return request.Offset < 0 ? $"{ToolName} argument 'offset' must be zero or more." : null;
    }

    protected override async Task<WorkSessionToolOutcome> ExecuteCoreAsync(ReadUpstreamArtifactRequest request,
        AgentWorkSessionSnapshot session,
        IAgentWorkSessionStore store,
        CancellationToken cancellationToken)
    {
        var readable = WorkSessionTurnScope.ReadableArtifacts;
        if (readable.Count == 0)
        {
            return Say("This step was handed no upstream artifacts, so there is nothing to read.");
        }

        var named = readable.Where(artifact => string.Equals(artifact.Name, request.Name, StringComparison.Ordinal)
                                               && (request.Producer is null || string.Equals(artifact.Producer, request.Producer, StringComparison.Ordinal)))
                            .ToList();
        if (named.Count == 0)
        {
            return Say($"No upstream artifact handed to this step is named '{request.Name}'{(request.Producer is null ? "" : $" from '{request.Producer}'")}. "
                       + $"Readable here: {string.Join(", ", readable.Select(static artifact => $"\"{artifact.Name}\" (from '{artifact.Producer}')"))}.");
        }

        if (named.Count > 1)
        {
            // Promoted names are the session's own, so two producers can hand one step the same name; guessing would read the wrong document.
            return Say($"'{request.Name}' names {named.Count} upstream artifacts, made by {string.Join(", ", named.Select(static artifact => $"'{artifact.Producer}'"))}. "
                       + "Call again with 'producer' set to one of them.");
        }

        var target = named[0];
        if (!ArtifactMediaTypes.IsText(target.MediaType))
        {
            return Say($"{target.Kind} '{target.Name}' from '{target.Producer}' (version {target.Version}) is {target.MediaType}, {target.SizeBytes} bytes. "
                       + "It is not text, so it cannot be read as text.");
        }

        // simplified: each page re-reads and decodes the whole blob, bounded by DevWorkflowOptions.MaxArtifactBytes; cache per step if pages get hot.
        var read = await _blobs.ReadAsync(target.RunId, target.ArtifactId, target.ContentSha256, target.SizeBytes, cancellationToken);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            Logger.LogWarning("Development workflow artifact {ArtifactId} of run {RunId} was not handed to {ToolName}: {Status}.", target.ArtifactId, target.RunId, ToolName, read.Status);
            return Say($"The stored bytes of '{target.Name}' did not verify ({read.Status}), so nothing was read. Do not assume what it said.");
        }

        var content = Encoding.UTF8.GetString(read.Content.Span);
        var offset = request.Offset ?? 0;
        if (offset > content.Length)
        {
            return Say($"Offset {offset} is past the end: '{target.Name}' has {content.Length} characters.");
        }

        // The result wrapper cuts the middle of anything over the effective budget while nextOffset would still skip past it, so a page
        // and its header fit under that budget. The widest header (every number at the total, the end-of-artifact cursor) is the reserve.
        var budget = ToolResultBudgetScope.Resolve(_configuredResultCharacters);
        var room = budget - Header(target, content.Length, content.Length, content.Length, EndOfArtifact).Length;
        if (room < 1)
        {
            return Say($"This step's tool-result budget ({budget} characters) leaves no room for a page of '{target.Name}', so nothing was read.");
        }

        var length = Math.Min(Math.Min(WorkSessionToolDefinitions.ReadUpstreamArtifact.PageCharacters, room), content.Length - offset);
        if (offset + length < content.Length && length > 1 && char.IsHighSurrogate(content[offset + length - 1]))
        {
            // Never split a surrogate pair across two pages.
            length--;
        }

        var next = offset + length < content.Length ? (offset + length).ToString(CultureInfo.InvariantCulture) : EndOfArtifact;
        return Say(string.Concat(Header(target, offset, length, content.Length, next), content.AsSpan(offset, length)));
    }

    private static string Header(WorkSessionReadableArtifact target, int offset, int length, int total, string next) =>
        string.Create(CultureInfo.InvariantCulture,
            $"name=\"{target.Name}\" producer=\"{target.Producer}\" offset={offset} length={length} total={total} nextOffset={next}{Environment.NewLine}{Environment.NewLine}");

    private static WorkSessionToolOutcome Say(string message) =>
        new()
        {
            Message = message
        };
}
