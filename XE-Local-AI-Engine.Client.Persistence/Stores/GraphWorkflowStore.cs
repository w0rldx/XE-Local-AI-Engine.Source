namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The definition half of the Graph Workflow substrate: one file, because this store has one family. The run,
///     node-run and event paths land beside it when the run engine ships.
/// </summary>
internal sealed class GraphWorkflowStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IGraphWorkflowStore
{
    /// <summary>The run statuses that still pin a definition. Anything else has finished and kept its own pinned graph.</summary>
    private static readonly GraphWorkflowRunStatus[] LiveRunStatuses =
    [
        GraphWorkflowRunStatus.Pending,
        GraphWorkflowRunStatus.Running,
        GraphWorkflowRunStatus.WaitingForApproval,
        GraphWorkflowRunStatus.Cancelling
    ];

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<GraphWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Name, nameof(command.Name));
        EnsureNotBlank(command.GraphJson, nameof(command.GraphJson));
        if (command.NodeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A definition node count cannot be negative.");
        }

        var graph = Utf8(command.GraphJson);
        var now = Now();
        var definition = new GraphWorkflowDefinition
        {
            Id = command.DefinitionId,
            Name = command.Name,
            Description = command.Description,
            GraphJson = graph,
            GraphHash = HashPayload(graph),
            NodeCount = command.NodeCount,
            SchemaVersion = command.SchemaVersion,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.GraphWorkflowDefinitions.Add(definition);
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // Cleared, or the definition stays tracked as Added and every later write in this scope trips over it.
            _dbContext.ChangeTracker.Clear();
            throw new GraphWorkflowDefinitionConflictException($"A graph workflow definition already exists for id '{command.DefinitionId}'.", exception);
        }
        catch (DbUpdateException)
        {
            // Anything else — a missing table, a read-only file, a full disk — is not "it already exists", and saying
            // so would answer 409 to a broken node and hide the real fault. Cleared for the same reason as above, then
            // left to travel with its own stack.
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        return Snapshot(definition);
    }

    public async Task<GraphWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Name is not null)
        {
            EnsureNotBlank(command.Name, nameof(command.Name));
        }

        byte[]? graph = null;
        var graphNodeCount = 0;
        if (command.GraphJson is not null)
        {
            EnsureNotBlank(command.GraphJson, nameof(command.GraphJson));

            // The node count is denormalized so the list never decrypts a blob. A new graph without one would leave
            // the PREVIOUS graph's count beside it, and the list would then report a number for a document that no
            // longer has it — the one lie this column exists to make impossible.
            graphNodeCount = command.NodeCount
                             ?? throw new ArgumentException("A graph workflow definition edit that carries a graph must carry its node count with it.",
                                 nameof(command));
            graph = Utf8(command.GraphJson);
        }

        if (command.NodeCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A definition node count cannot be negative.");
        }

        var definition = await LoadAsync(command.DefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition.Version != command.ExpectedVersion)
        {
            throw new GraphWorkflowDefinitionConflictException($"The definition version is stale (expected {command.ExpectedVersion}, current {definition.Version}).");
        }

        if (command.Name is not null)
        {
            definition.Name = command.Name;
        }

        if (command.Description is not null)
        {
            definition.Description = command.Description;
        }

        if (graph is not null)
        {
            // Hash, node count and schema version are written together with the graph, every time: that is what lets
            // the list promise never to decrypt a blob and still tell the truth about it.
            definition.GraphJson = graph;
            definition.GraphHash = HashPayload(graph);
            definition.NodeCount = graphNodeCount;

            // The schema version, unlike the node count, may be left to the stored one: this node understands exactly
            // one version and the parser refuses every other, so a graph that reached here IS that version and the
            // stored value already says so. The day a second version ships, this line becomes a lie and the command
            // has to carry it — which is why it is spelled out rather than defaulted quietly.
            definition.SchemaVersion = command.SchemaVersion ?? definition.SchemaVersion;
        }
        else if (command.NodeCount is { } nodeCount)
        {
            definition.NodeCount = nodeCount;
        }

        definition.Version++;
        definition.UpdatedAtUtc = Now();
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The version check above answers the common stale PUT with the numbers the caller sent. This is the other
            // half: two edits that each read version N both pass that check, and only the token stops the later one
            // from overwriting the earlier without either caller ever learning of the other.
            _dbContext.ChangeTracker.Clear();
            throw new GraphWorkflowDefinitionConflictException("The definition was changed by another writer before this edit could be written, so its version is stale.",
                exception);
        }

        return Snapshot(definition);
    }

    public async Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        // Projected server-side without graph_json, so no definition blob is ever decrypted to draw the picker.
        await _dbContext.GraphWorkflowDefinitions.AsNoTracking()
                        .OrderBy(entity => entity.Name)
                        .ThenBy(entity => entity.Id)
                        .Select(entity => new GraphWorkflowDefinitionSummary(entity.Id,
                            entity.Name,
                            entity.Description,
                            entity.GraphHash,
                            entity.NodeCount,
                            entity.SchemaVersion,
                            entity.Version,
                            entity.CreatedAtUtc,
                            entity.UpdatedAtUtc))
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

    public async Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.GraphWorkflowDefinitions.AsNoTracking()
                                         .SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken)
                                         .ConfigureAwait(false)
                         ?? throw new GraphWorkflowNotFoundException($"Graph workflow definition '{definitionId}' was not found.");
        return Snapshot(definition);
    }

    public async Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definition = await LoadAsync(definitionId, cancellationToken).ConfigureAwait(false);

            // Inside the transaction, which is the only place the answer is true: EF opens SQLite transactions as
            // BEGIN IMMEDIATE, so a run that starts while this delete is in flight blocks on the writer lock and is
            // either counted here or committed after a delete that has already been refused.
            var live = await _dbContext.GraphWorkflowRuns.AsNoTracking()
                                       .AnyAsync(entity => entity.DefinitionId == definitionId && LiveRunStatuses.Contains(entity.Status), cancellationToken)
                                       .ConfigureAwait(false);
            if (live)
            {
                throw new GraphWorkflowDefinitionConflictException($"Graph workflow definition '{definitionId}' cannot be deleted while one of its runs is still live.");
            }

            _ = _dbContext.GraphWorkflowDefinitions.Remove(definition);
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<GraphWorkflowDefinition> LoadAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await _dbContext.GraphWorkflowDefinitions.SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken).ConfigureAwait(false)
        ?? throw new GraphWorkflowNotFoundException($"Graph workflow definition '{definitionId}' was not found.");

    private static GraphWorkflowDefinitionSnapshot Snapshot(GraphWorkflowDefinition definition) =>
        new(definition.Id,
            definition.Name,
            definition.Description,
            Text(definition.GraphJson),
            definition.GraphHash,
            definition.NodeCount,
            definition.SchemaVersion,
            definition.Version,
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc);

    /// <summary>
    ///     SHA-256 of the graph's bytes, lowercase hex, computed here — beside the blob it describes — so a hash and
    ///     the document it names can never drift apart.
    /// </summary>
    private static string HashPayload(byte[] payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    /// <summary>
    ///     Whether a failed write was the unique index refusing a duplicate. SQLite reports both PRIMARY KEY (1555) and
    ///     UNIQUE (2067) as extended codes of the generic constraint error, and only those two mean "already exists".
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    private static void EnsureNotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static byte[] Utf8(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) =>
        Encoding.UTF8.GetString(value);
}
