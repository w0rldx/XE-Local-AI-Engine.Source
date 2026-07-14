namespace XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>read_document</c> (ClientLocal). JSON-in / JSON-out: reads one
///     knowledge-base document's detail plus its ordered chunks through the scoped
///     <see cref="IKnowledgeDocumentCatalogService" /> resolved from a FRESH DI scope per call. The returned content is
///     bounded (<see cref="MaxContentChars" />) so a huge document cannot be dumped unbounded into the model context;
///     truncation is flagged in the payload. Read-only, so it auto-runs; gated by
///     <c>KnowledgeBase:AgentToolsEnabled</c>.
/// </summary>
internal sealed class ReadDocumentToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Upper bound on the total chunk-content characters returned, so a large document is not dumped whole.</summary>
    private const int MaxContentChars = 50_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _toolsEnabled;

    public ReadDocumentToolHandler(IServiceScopeFactory scopeFactory, IOptions<KnowledgeBaseOptions> options)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _toolsEnabled = options.Value.AgentToolsEnabled;
    }

    public string ToolName => ReadDocumentToolDefinition.ToolName;

    public string Description => ReadDocumentToolDefinition.Description;

    public string ParameterSchema => ReadDocumentToolDefinition.ParameterSchema;

    public bool RequiresApproval => false;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (!_toolsEnabled)
        {
            return "The knowledge-base tools are disabled on this node (KnowledgeBase:AgentToolsEnabled=false).";
        }

        cancellationToken.ThrowIfCancellationRequested();

        ReadDocumentToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ReadDocumentToolRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"read_document arguments were not valid JSON: {exception.Message}";
        }

        if (request is null || string.IsNullOrWhiteSpace(request.DocumentId) || !Guid.TryParse(request.DocumentId, out var documentId))
        {
            return "read_document requires a valid 'documentId'.";
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentCatalogService>();
        var detail = await catalog.GetAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return JsonSerializer.Serialize(new
                {
                    note = "No knowledge-base document exists with that documentId."
                },
                SerializerOptions);
        }

        var chunks = new List<object>(detail.Chunks.Count);
        var usedChars = 0;
        var truncated = false;
        foreach (var chunk in detail.Chunks)
        {
            if (usedChars + chunk.Content.Length > MaxContentChars)
            {
                truncated = true;
                break;
            }

            usedChars += chunk.Content.Length;
            chunks.Add(new
            {
                chunkIndex = chunk.ChunkIndex,
                section = chunk.HeadingPath,
                // Document text is DATA, not instructions: flag and fence it (budget still measures raw chunk length).
                contentTrust = UntrustedContentFraming.UntrustedTrustLabel,
                content = UntrustedContentFraming.Wrap(chunk.Content)
            });
        }

        var payload = new
        {
            documentId = detail.DocumentId,
            title = detail.DisplayName,
            status = detail.Status.ToString(),
            chunkCount = detail.ChunkCount,
            returnedChunks = chunks.Count,
            truncated,
            chunks
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
