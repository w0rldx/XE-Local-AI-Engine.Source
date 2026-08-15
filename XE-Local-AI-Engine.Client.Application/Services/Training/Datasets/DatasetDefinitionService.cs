namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Globalization;
using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed record DatasetDefinitionDraft(string Name, DatasetDefinitionBodyV1 Body);

public interface IDatasetDefinitionService
{
    Task<TrainingDefinitionRecord> CreateAsync(DatasetDefinitionDraft draft, CancellationToken cancellationToken = default);

    Task<TrainingDefinitionRecord> UpdateAsync(Guid definitionId, long expectedVersion, DatasetDefinitionDraft draft, CancellationToken cancellationToken = default);

    Task<TrainingDefinitionRecord?> GetAsync(Guid definitionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingDefinitionRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid definitionId, long expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>
///     Dataset definition CRUD. Validates the body, then embeds a tool-schema snapshot taken from the live catalog with
///     the definition's OWN approval compose — the offer provider never consults <see cref="IToolApprovalPolicy" />, each
///     caller does (the <c>OrchestrationResolver.ProjectAllowedToolsAsync</c> pattern), so this service performs its own.
///     An edit bumps <c>DefinitionVersion</c>, which is what a generated dataset pins.
/// </summary>
public sealed class DatasetDefinitionService(
    ITrainingDatasetStore store,
    ILocalToolOfferProvider offerProvider,
    IToolApprovalPolicy approvalPolicy) : IDatasetDefinitionService
{
    private const int MaxTargetSampleCount = 2000;

    private readonly IToolApprovalPolicy _approvalPolicy = approvalPolicy ?? throw new ArgumentNullException(nameof(approvalPolicy));
    private readonly ILocalToolOfferProvider _offerProvider = offerProvider ?? throw new ArgumentNullException(nameof(offerProvider));
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<TrainingDefinitionRecord> CreateAsync(DatasetDefinitionDraft draft, CancellationToken cancellationToken = default)
    {
        var input = await BuildInputAsync(draft, cancellationToken).ConfigureAwait(false);
        return await _store.CreateDefinitionAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingDefinitionRecord> UpdateAsync(Guid definitionId,
        long expectedVersion,
        DatasetDefinitionDraft draft,
        CancellationToken cancellationToken = default)
    {
        var input = await BuildInputAsync(draft, cancellationToken).ConfigureAwait(false);
        return await _store.UpdateDefinitionAsync(definitionId, expectedVersion, input, cancellationToken).ConfigureAwait(false);
    }

    public Task<TrainingDefinitionRecord?> GetAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _store.GetDefinitionAsync(definitionId, cancellationToken);

    public Task<IReadOnlyList<TrainingDefinitionRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.ListDefinitionsAsync(cancellationToken);

    public Task DeleteAsync(Guid definitionId, long expectedVersion, CancellationToken cancellationToken = default) =>
        _store.DeleteDefinitionAsync(definitionId, expectedVersion, cancellationToken);

    /// <summary>Reads a persisted definition body. Throws <see cref="TrainingValidationException" /> on an unreadable payload.</summary>
    public static DatasetDefinitionBodyV1 ReadBody(ReadOnlyMemory<byte> definitionJson)
    {
        try
        {
            return JsonSerializer.Deserialize<DatasetDefinitionBodyV1>(definitionJson.Span, TrainingJson.Options)
                   ?? throw new TrainingValidationException("The dataset definition body is empty.");
        }
        catch (JsonException exception)
        {
            throw new TrainingValidationException($"The dataset definition body is not valid JSON: {exception.Message}");
        }
    }

    private async Task<TrainingDefinitionInput> BuildInputAsync(DatasetDefinitionDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Body is null)
        {
            throw new TrainingValidationException("A dataset definition requires a body.");
        }

        Validate(draft.Body);

        var snapshot = await SnapshotToolsAsync(draft.Body, cancellationToken).ConfigureAwait(false);
        var body = draft.Body with
        {
            SchemaVersion = 1,
            Tools = snapshot
        };
        return new TrainingDefinitionInput(draft.Name, TrainingDatasetKind.ToolCalling,
            JsonSerializer.SerializeToUtf8Bytes(body, TrainingJson.Options));
    }

    private static void Validate(DatasetDefinitionBodyV1 body)
    {
        if (string.IsNullOrWhiteSpace(body.TeacherModelName))
        {
            throw new TrainingValidationException("A dataset definition requires a node-local teacher model.");
        }

        if (!Enum.IsDefined(body.TeacherOutputMode))
        {
            throw new TrainingValidationException("The teacher output mode must be Constrained or ValidateAfter.");
        }

        if (body.HoldoutFraction is < DatasetDefinitionBodyV1.MinHoldoutFraction or > DatasetDefinitionBodyV1.MaxHoldoutFraction)
        {
            throw new TrainingValidationException("The hold-out fraction must be between 0.05 and 0.30.");
        }

        if (body.Temperature is < 0f or > 2f)
        {
            throw new TrainingValidationException("The teacher temperature must be between 0 and 2.");
        }

        if (body.SampleKinds.Count == 0)
        {
            throw new TrainingValidationException("A dataset definition requires at least one sample kind.");
        }

        var total = 0;
        foreach (var kind in body.SampleKinds)
        {
            if (string.IsNullOrWhiteSpace(kind.Kind) || kind.Kind.Length > 64)
            {
                throw new TrainingValidationException("A sample kind must be a non-empty name of at most 64 characters.");
            }

            if (kind.Count < 1)
            {
                throw new TrainingValidationException("A sample kind target count must be positive.");
            }

            total += kind.Count;
        }

        if (total > MaxTargetSampleCount)
        {
            throw new TrainingValidationException($"A dataset definition may target at most {MaxTargetSampleCount} samples.");
        }

        if (body.SampleKinds.Select(kind => (kind.Kind, kind.Label)).Distinct().Count() != body.SampleKinds.Count)
        {
            throw new TrainingValidationException("Sample kinds must be unique per label.");
        }

        if (body.BaseSeed is { } seed && !long.TryParse(seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new TrainingValidationException("The base seed must be a 64-bit integer carried as a string.");
        }

        if (body.CriticEnabled && string.IsNullOrWhiteSpace(body.CriticModelName))
        {
            throw new TrainingValidationException("A critic-enabled definition requires a node-local critic model.");
        }
    }

    private async Task<IReadOnlyList<DatasetToolSnapshotV1>> SnapshotToolsAsync(DatasetDefinitionBodyV1 body, CancellationToken cancellationToken)
    {
        var requested = body.Tools.Select(tool => tool.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0)
        {
            return [];
        }

        // The teacher is always node-local (invariant #5), so the offer is taken with isCloudModel: false.
        var offered = await _offerProvider.GetOfferedToolsAsync(body.TeacherModelName, isCloudModel: false, cancellationToken).ConfigureAwait(false);
        var byName = offered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var missing = requested.Where(name => !byName.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new TrainingValidationException($"The tool catalog does not offer: {string.Join(", ", missing)}.");
        }

        return requested.Order(StringComparer.Ordinal)
                        .Select(name => byName[name])
                        .Select(tool => new DatasetToolSnapshotV1(tool.Name,
                            tool.Description,
                            tool.ParameterSchema,
                            _approvalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval),
                            tool.Category))
                        .ToArray();
    }
}
