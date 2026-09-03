namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using ServiceIntegrationApiKeyView = XE_Local_AI_Engine.Client.Services.Integrations.IntegrationApiKeyView;

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
