namespace XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>read_surrounding_chunks</c> (ClientLocal). JSON-in / JSON-out:
///     returns the neighbor window around a matched chunk through the scoped <see cref="IContextExpansionService" />
///     resolved from a FRESH DI scope per call. The anchor is identified by <c>documentId</c> + <c>chunkIndex</c> (the
///     coordinates a search hit carries); <c>before</c>/<c>after</c> select how many neighbors on each side. Read-only,
///     so it auto-runs; gated by <c>KnowledgeBase:AgentToolsEnabled</c>.
/// </summary>
internal sealed class ReadSurroundingChunksToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private const int DefaultNeighbors = 1;
    private const int MaxNeighbors = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _toolsEnabled;

    public ReadSurroundingChunksToolHandler(IServiceScopeFactory scopeFactory, IOptions<KnowledgeBaseOptions> options)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _toolsEnabled = options.Value.AgentToolsEnabled;
    }

    public string ToolName => ReadSurroundingChunksToolDefinition.ToolName;

    public string Description => ReadSurroundingChunksToolDefinition.Description;

    public string ParameterSchema => ReadSurroundingChunksToolDefinition.ParameterSchema;

    public bool RequiresApproval => false;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (!_toolsEnabled)
        {
            return "The knowledge-base tools are disabled on this node (KnowledgeBase:AgentToolsEnabled=false).";
        }

        cancellationToken.ThrowIfCancellationRequested();

        ReadSurroundingChunksToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ReadSurroundingChunksToolRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"read_surrounding_chunks arguments were not valid JSON: {exception.Message}";
        }

        if (request is null || string.IsNullOrWhiteSpace(request.DocumentId) || !Guid.TryParse(request.DocumentId, out var documentId))
        {
            return "read_surrounding_chunks requires a valid 'documentId'.";
        }

        if (request.ChunkIndex is not { } chunkIndex || chunkIndex < 0)
        {
            return "read_surrounding_chunks requires a non-negative 'chunkIndex'.";
        }

        var before = Math.Clamp(request.Before ?? DefaultNeighbors, 0, MaxNeighbors);
        var after = Math.Clamp(request.After ?? DefaultNeighbors, 0, MaxNeighbors);

        // ExpandAsync uses a symmetric window; fetch the wider side and then trim to the requested asymmetric bounds.
        var window = Math.Max(before, after);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var expansion = scope.ServiceProvider.GetRequiredService<IContextExpansionService>();
        var neighbors = await expansion.ExpandAsync(documentId, chunkIndex, window, cancellationToken).ConfigureAwait(false);

        var lowerBound = chunkIndex - before;
        var upperBound = chunkIndex + after;
        var payload = new
        {
            documentId,
            chunkIndex,
            chunks = neighbors
                .Where(neighbor => neighbor.ChunkIndex >= lowerBound && neighbor.ChunkIndex <= upperBound)
                .Select(static neighbor => new
                {
                    chunkId = neighbor.ChunkId,
                    chunkIndex = neighbor.ChunkIndex,
                    section = neighbor.HeadingPath,
                    content = neighbor.Content
                })
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
