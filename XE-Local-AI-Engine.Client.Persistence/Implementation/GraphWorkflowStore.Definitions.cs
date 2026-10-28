namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class GraphWorkflowStore
{
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
            Kind = command.Kind,
            AllowsActions = command.AllowsActions,
            TriggerSources = command.TriggerSources,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.GraphWorkflowDefinitions.Add(definition);
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // Cleared, or the definition stays tracked as Added and every later write in this scope trips over it.
            _dbContext.ChangeTracker.Clear();
            throw new GraphWorkflowDefinitionConflictException($"A graph workflow definition already exists for id '{command.DefinitionId}'.", exception);
        }
        catch (DbUpdateException)
        {
            // Anything else — a missing table, a read-only file, a full disk — is not "it already exists", and saying so would answer 409 to a broken node and hide
            // the real fault. Cleared for the same reason as above, then left to travel with its own stack.
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

            // The node count is denormalized so the list never decrypts a blob. A new graph without one would leave the PREVIOUS graph's count beside it, and the
            // list would then report a number for a document that no longer has it — the one lie this column exists to make impossible.
            graphNodeCount = command.NodeCount
                             ?? throw new ArgumentException("A graph workflow definition edit that carries a graph must carry its node count with it.",
                                 nameof(command));
            graph = Utf8(command.GraphJson);
        }
        else if (command.NodeCount is not null)
        {
            // The mirror refusal, for the same reason: a count written WITHOUT the graph it counts leaves the stored document beside a number that is not its own,
            // and the list reports it without opening the blob that would contradict it. The count is derived, not editable, and travels with what it derives from.
            throw new ArgumentException("A graph workflow definition edit that carries a node count must carry the graph it counts with it.",
                nameof(command));
        }

        if (command.NodeCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A definition node count cannot be negative.");
        }

        var definition = await LoadAsync(command.DefinitionId, cancellationToken);
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

        if (command.AllowsActions is { } allowsActions)
        {
            definition.AllowsActions = allowsActions;
        }

        if (command.TriggerSources is { } triggerSources)
        {
            definition.TriggerSources = triggerSources;
        }

        if (graph is not null)
        {
            // Hash, node count and schema version are written together with the graph, every time: that is what lets
            // the list promise never to decrypt a blob and still tell the truth about it.
            definition.GraphJson = graph;
            definition.GraphHash = HashPayload(graph);
            definition.NodeCount = graphNodeCount;

            // The schema version, unlike the node count, may be left to the stored one: this node understands exactly one version and the parser refuses every
            // other, so a graph that reached here IS it. The day a second version ships this becomes a lie, which is why it is spelled out rather than defaulted.
            definition.SchemaVersion = command.SchemaVersion ?? definition.SchemaVersion;
            definition.Kind = command.Kind ?? definition.Kind;
        }

        definition.Version++;
        definition.UpdatedAtUtc = Now();
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The version check above answers the common stale PUT with the numbers the caller sent. This is the other half: two edits that each read version N both
            // pass that check, and only the token stops the later one from overwriting the earlier without either caller ever learning of the other.
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
                        .Select(entity => new GraphWorkflowDefinitionSummary
                        {
                            Id = entity.Id,
                            Name = entity.Name,
                            Description = entity.Description,
                            GraphHash = entity.GraphHash,
                            NodeCount = entity.NodeCount,
                            SchemaVersion = entity.SchemaVersion,
                            Kind = entity.Kind,
                            AllowsActions = entity.AllowsActions,
                            TriggerSources = entity.TriggerSources,
                            Version = entity.Version,
                            CreatedAtUtc = entity.CreatedAtUtc,
                            UpdatedAtUtc = entity.UpdatedAtUtc
                        })
                        .ToListAsync(cancellationToken);

    public async Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.GraphWorkflowDefinitions.AsNoTracking()
                                         .SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken)
                         ?? throw new GraphWorkflowNotFoundException($"Graph workflow definition '{definitionId}' was not found.");
        return Snapshot(definition);
    }

    public async Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var definition = await LoadAsync(definitionId, cancellationToken);

            // Inside the transaction, which is the only place the answer can be true: EF opens SQLite transactions as BEGIN IMMEDIATE, so a writer that starts a
            // run while this delete is in flight blocks on the writer lock. It holds only while run start re-checks the definition in its own insert transaction.
            var live = await _dbContext.GraphWorkflowRuns.AsNoTracking()
                                       .AnyAsync(entity => entity.DefinitionId == definitionId && LiveRunStatuses.Contains(entity.Status), cancellationToken);
            if (live)
            {
                throw new GraphWorkflowDefinitionConflictException($"Graph workflow definition '{definitionId}' cannot be deleted while one of its runs is still live.");
            }

            _ = _dbContext.GraphWorkflowDefinitions.Remove(definition);
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<GraphWorkflowDefinition> LoadAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await _dbContext.GraphWorkflowDefinitions.SingleOrDefaultAsync(entity => entity.Id == definitionId, cancellationToken)
        ?? throw new GraphWorkflowNotFoundException($"Graph workflow definition '{definitionId}' was not found.");

    private static GraphWorkflowDefinitionSnapshot Snapshot(GraphWorkflowDefinition definition) =>
        new()
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            GraphJson = Text(definition.GraphJson),
            GraphHash = definition.GraphHash,
            NodeCount = definition.NodeCount,
            SchemaVersion = definition.SchemaVersion,
            Kind = definition.Kind,
            AllowsActions = definition.AllowsActions,
            TriggerSources = definition.TriggerSources,
            Version = definition.Version,
            CreatedAtUtc = definition.CreatedAtUtc,
            UpdatedAtUtc = definition.UpdatedAtUtc
        };

    /// <summary>
    ///     SHA-256 of the graph's bytes, lowercase hex, computed here — beside the blob it describes — so a hash and
    ///     the document it names can never drift apart.
    /// </summary>
    private static string HashPayload(byte[] payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));
}
