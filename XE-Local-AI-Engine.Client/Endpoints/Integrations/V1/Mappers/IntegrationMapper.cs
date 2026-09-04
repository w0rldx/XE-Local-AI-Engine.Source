namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using ServiceIntegrationApiKeyView = XE_Local_AI_Engine.Client.Services.Integrations.IntegrationApiKeyView;
using ServiceIntegrationSessionDto = XE_Local_AI_Engine.Client.Services.Integrations.IntegrationSessionDto;

/// <summary>
///     Record ↔ DTO for the integration admin family, and the ONE place the <c>[Flags] IntegrationInputKinds</c> enum
///     is translated to and from its <c>string[]</c> wire form.
/// </summary>
internal static class IntegrationMapper
{
    /// <summary>The wire name of <see cref="IntegrationInputKinds.Text" />.</summary>
    public const string TextInputKind = "text";

    /// <summary>The wire name of <see cref="IntegrationInputKinds.Json" />.</summary>
    public const string JsonInputKind = "json";

    public static IntegrationTriggerView ToView(IntegrationTriggerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new IntegrationTriggerView
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            DisplayName = snapshot.DisplayName,
            Description = snapshot.Description,
            Enabled = snapshot.Enabled,
            TargetKind = snapshot.TargetKind,
            TargetAgentDefinitionId = snapshot.TargetAgentDefinitionId,
            SessionPolicy = snapshot.SessionPolicy,
            AcceptedInputKinds = ToWireInputKinds(snapshot.AcceptedInputKinds),
            CreatedAtUtc = snapshot.CreatedAtUtc,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
            Version = snapshot.Version
        };
    }

    public static IntegrationExecutionEventDto ToEventDto(IntegrationExecutionEventSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new IntegrationExecutionEventDto
        {
            ExecutionId = snapshot.ExecutionId,
            Sequence = snapshot.Sequence,
            // The store decrypted it; nothing downstream ever sees the stored byte[].
            EventType = snapshot.EventType,
            DetailJson = snapshot.DetailJson,
            OccurredAtUtc = snapshot.OccurredAtUtc
        };
    }

    public static IntegrationApiKeyView ToView(ServiceIntegrationApiKeyView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new IntegrationApiKeyView
        {
            Id = view.Id,
            PrincipalId = view.PrincipalId,
            KeyPrefix = view.KeyPrefix,
            Label = view.Label,
            AllowedTriggerIds = view.AllowedTriggerIds,
            CreatedAtUtc = view.CreatedAt.ToUnixTimeMilliseconds(),
            LastUsedAtUtc = view.LastUsedAt?.ToUnixTimeMilliseconds(),
            RevokedAtUtc = view.RevokedAt?.ToUnixTimeMilliseconds()
        };
    }

    public static IntegrationSessionResponse ToResponse(ServiceIntegrationSessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new IntegrationSessionResponse
        {
            Id = session.Id,
            TriggerId = session.TriggerId,
            TriggerName = session.TriggerName,
            AgentDefinitionId = session.AgentDefinitionId,
            Status = session.Status,
            CreatedAtUtc = session.CreatedAtUtc,
            LastActivityUtc = session.LastActivityUtc,
            ExecutionCount = session.ExecutionCount
        };
    }

    public static IntegrationExecutionSummaryDto ToSummary(IntegrationExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new IntegrationExecutionSummaryDto
        {
            Id = snapshot.Id,
            TriggerId = snapshot.TriggerId,
            SessionId = snapshot.SessionId,
            Status = snapshot.Status,
            ReceivedAtUtc = snapshot.ReceivedAtUtc,
            StartedAtUtc = snapshot.StartedAtUtc,
            EndedAtUtc = snapshot.EndedAtUtc,
            FailureCategory = snapshot.FailureCategory,
            FailureSummary = snapshot.FailureSummary,
            OutputCount = snapshot.OutputCount
        };
    }

    public static IntegrationExecutionDetailDto ToDetail(IntegrationExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new IntegrationExecutionDetailDto
        {
            Execution = ToSummary(snapshot),
            PrincipalId = snapshot.PrincipalId,
            KeyPrefix = snapshot.KeyPrefix,
            RequestId = snapshot.RequestId,
            InvocationId = snapshot.InvocationId,
            OutputBytes = snapshot.OutputBytes,
            LastSequence = snapshot.LastSequence,
            Version = snapshot.Version,
            StopRequestedAtUtc = snapshot.StopRequestedAtUtc
        };
    }

    /// <summary>The wire member names of a flags value, in declaration order.</summary>
    public static IReadOnlyList<string> ToWireInputKinds(IntegrationInputKinds kinds)
    {
        var names = new List<string>(capacity: 2);
        if (kinds.HasFlag(IntegrationInputKinds.Text))
        {
            names.Add(TextInputKind);
        }

        if (kinds.HasFlag(IntegrationInputKinds.Json))
        {
            names.Add(JsonInputKind);
        }

        return names;
    }

    /// <summary>
    ///     Folds the wire array back into the flags value. An unrecognised member yields <see langword="null" />, which
    ///     the validator turns into a 400 — silently dropping it would save a trigger accepting less than the operator
    ///     asked for.
    /// </summary>
    public static IntegrationInputKinds? FromWireInputKinds(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return null;
        }

        var kinds = default(IntegrationInputKinds);
        foreach (var name in names)
        {
            if (string.Equals(name, TextInputKind, StringComparison.OrdinalIgnoreCase))
            {
                kinds |= IntegrationInputKinds.Text;
            }
            else if (string.Equals(name, JsonInputKind, StringComparison.OrdinalIgnoreCase))
            {
                kinds |= IntegrationInputKinds.Json;
            }
            else
            {
                return null;
            }
        }

        return kinds;
    }
}
