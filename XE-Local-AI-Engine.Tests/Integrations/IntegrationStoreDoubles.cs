namespace XE_Local_AI_Engine.Tests.Integrations;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     In-memory <see cref="IIntegrationApiKeyStore" />. Real behaviour rather than a mock because the key service's
///     interesting properties — a digest that never round-trips, revocation that keeps the row, many keys sharing a
///     principal — are all statements about what the store ends up holding.
/// </summary>
internal sealed class FakeIntegrationApiKeyStore : IIntegrationApiKeyStore
{
    private readonly List<IntegrationApiKeySnapshot> _rows = [];

    /// <summary>Every row as stored, newest last. The suite reads it to assert the plaintext never landed anywhere.</summary>
    public IReadOnlyList<IntegrationApiKeySnapshot> Rows => _rows;

    public Task<IntegrationApiKeySnapshot> CreateAsync(IntegrationApiKeyCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var snapshot = new IntegrationApiKeySnapshot(command.KeyId,
            command.PrincipalId,
            command.KeyPrefix,
            command.KeyHash,
            command.Label,
            command.AllowedTriggerIdsJson,
            CreatedAtUtc: 1,
            LastUsedAtUtc: null,
            RevokedAtUtc: null);
        _rows.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<IntegrationApiKeySnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationApiKeySnapshot>>(_rows.ToArray());

    public Task<IntegrationApiKeySnapshot?> GetByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => string.Equals(row.KeyPrefix, keyPrefix, StringComparison.Ordinal)));

    public Task<bool> TouchLastUsedAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(Replace(keyId, row => row with
        {
            LastUsedAtUtc = atUtc
        }));

    public Task<bool> RevokeAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(Replace(keyId, row => row with
        {
            RevokedAtUtc = atUtc
        }));

    private bool Replace(Guid keyId, Func<IntegrationApiKeySnapshot, IntegrationApiKeySnapshot> mutate)
    {
        var index = _rows.FindIndex(row => row.Id == keyId);
        if (index < 0)
        {
            return false;
        }

        _rows[index] = mutate(_rows[index]);
        return true;
    }
}

/// <summary>
///     In-memory <see cref="IIntegrationTriggerStore" />. <see cref="CreateAsync" /> enforces the unique name the real
///     schema does, so the service's duplicate-name path is exercised against the same failure the database produces.
/// </summary>
internal sealed class FakeIntegrationTriggerStore : IIntegrationTriggerStore
{
    private readonly List<IntegrationTriggerSnapshot> _rows = [];

    public IReadOnlyList<IntegrationTriggerSnapshot> Rows => _rows;

    /// <summary>
    ///     Makes the next <see cref="GetByNameAsync" /> answer "no such name" whatever the rows say, so a suite can
    ///     drive the window between the service's pre-check and its insert — the window the unique index closes.
    /// </summary>
    public bool HideNextNameLookup { get; set; }

    public Task<IntegrationTriggerSnapshot> CreateAsync(IntegrationTriggerCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_rows.Any(row => string.Equals(row.Name, command.Name, StringComparison.Ordinal)))
        {
            // The real schema answers a duplicate name with a unique-index violation, which surfaces as this type.
            throw new DbUpdateException($"Duplicate trigger name '{command.Name}'.");
        }

        var snapshot = new IntegrationTriggerSnapshot(command.TriggerId,
            command.Name,
            command.DisplayName,
            command.Description,
            command.Enabled,
            command.TargetKind,
            command.TargetAgentDefinitionId,
            command.SessionPolicy,
            command.AcceptedInputKinds,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Version: 1);
        _rows.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<bool> UpdateAsync(IntegrationTriggerUpdateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var index = _rows.FindIndex(row => row.Id == command.TriggerId);
        if (index < 0 || _rows[index].Version != command.ExpectedVersion)
        {
            return Task.FromResult(false);
        }

        _rows[index] = _rows[index] with
        {
            DisplayName = command.DisplayName,
            Description = command.Description,
            Enabled = command.Enabled,
            TargetAgentDefinitionId = command.TargetAgentDefinitionId,
            SessionPolicy = command.SessionPolicy,
            AcceptedInputKinds = command.AcceptedInputKinds,
            UpdatedAtUtc = 2,
            Version = command.ExpectedVersion + 1
        };
        return Task.FromResult(true);
    }

    public Task<IntegrationTriggerSnapshot?> GetByIdAsync(Guid triggerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => row.Id == triggerId));

    public Task<IntegrationTriggerSnapshot?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (HideNextNameLookup)
        {
            HideNextNameLookup = false;
            return Task.FromResult<IntegrationTriggerSnapshot?>(null);
        }

        return Task.FromResult(_rows.SingleOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationTriggerSnapshot>>(_rows.OrderBy(row => row.Name, StringComparer.Ordinal).ToArray());

    public Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default)
    {
        var index = _rows.FindIndex(row => row.Id == triggerId);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        _rows.RemoveAt(index);
        return Task.FromResult(true);
    }

    /// <summary>Seeds a row directly, so a suite can arrange a trigger without going through the service.</summary>
    public IntegrationTriggerSnapshot Seed(string name,
        Guid agentDefinitionId,
        bool enabled = true,
        IntegrationSessionPolicy sessionPolicy = IntegrationSessionPolicy.PerInvocation,
        IntegrationInputKinds acceptedInputKinds = IntegrationInputKinds.Text | IntegrationInputKinds.Json)
    {
        var snapshot = new IntegrationTriggerSnapshot(Guid.NewGuid(),
            name,
            name,
            Description: null,
            enabled,
            IntegrationTargetKind.Agent,
            agentDefinitionId,
            sessionPolicy,
            acceptedInputKinds,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Version: 1);
        _rows.Add(snapshot);
        return snapshot;
    }
}
