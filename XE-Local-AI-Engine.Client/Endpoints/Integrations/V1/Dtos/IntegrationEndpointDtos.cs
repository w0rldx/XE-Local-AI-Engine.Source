namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Wire projection of one integration trigger.
///     <para>
///         <see cref="AcceptedInputKinds" /> crosses the wire as a <c>string[]</c> of member names
///         (<c>["text","json"]</c>) rather than the <c>[Flags]</c> enum's integer sum: a bitwise union is not
///         expressible in an OpenAPI enum, and the summed integer is unreadable in a generated SDK. The array is both.
///     </para>
/// </summary>
public sealed class IntegrationTriggerView
{
    public required Guid Id { get; init; }

    /// <summary>The external name a caller addresses. Lowercase by contract, and not editable after create.</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required IntegrationTargetKind TargetKind { get; init; }

    public required Guid TargetAgentDefinitionId { get; init; }

    public required IntegrationSessionPolicy SessionPolicy { get; init; }

    /// <summary>The accepted input kinds as member names, lowercased: <c>text</c> and/or <c>json</c>.</summary>
    public required IReadOnlyList<string> AcceptedInputKinds { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    /// <summary>The optimistic concurrency token an update must echo back.</summary>
    public required long Version { get; init; }
}

/// <summary>Response envelope for <c>GET integrations/triggers</c>.</summary>
public sealed class ListIntegrationTriggersResponse
{
    public required IReadOnlyList<IntegrationTriggerView> Items { get; init; }
}

/// <summary>Body of <c>POST integrations/triggers</c>.</summary>
public sealed class CreateIntegrationTriggerRequest
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public IntegrationTargetKind TargetKind { get; init; } = IntegrationTargetKind.Agent;

    public required Guid TargetAgentDefinitionId { get; init; }

    public IntegrationSessionPolicy SessionPolicy { get; init; } = IntegrationSessionPolicy.PerInvocation;

    /// <summary>At least one of <c>text</c> / <c>json</c>. An unknown member name is a validation failure.</summary>
    public required IReadOnlyList<string> AcceptedInputKinds { get; init; }
}

/// <summary>
///     Body of <c>PUT integrations/triggers/{triggerId}</c>. <c>Name</c> is absent on purpose: it is the external
///     contract a caller addresses, so renaming a live trigger is a delete-and-create decision rather than an edit.
/// </summary>
public sealed class UpdateIntegrationTriggerRequest
{
    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public required Guid TargetAgentDefinitionId { get; init; }

    public IntegrationSessionPolicy SessionPolicy { get; init; } = IntegrationSessionPolicy.PerInvocation;

    public required IReadOnlyList<string> AcceptedInputKinds { get; init; }

    /// <summary>The <c>Version</c> the caller read. A mismatch is a 409, never a silent overwrite.</summary>
    public required long ExpectedVersion { get; init; }
}

/// <summary>
///     Wire projection of one <c>xeint_</c> credential. Carries no secret by construction — the node keeps only a
///     digest — so it is safe on any Operator-gated surface.
/// </summary>
public sealed class IntegrationApiKeyView
{
    public required Guid Id { get; init; }

    /// <summary>
    ///     The integrator identity. Two keys sharing this value are one integrator, which is what makes a credential
    ///     rotation keep the sessions and in-flight executions the replaced key owned.
    /// </summary>
    public required Guid PrincipalId { get; init; }

    /// <summary>The non-secret display prefix (<c>xeint_</c> plus eight characters).</summary>
    public required string KeyPrefix { get; init; }

    public required string Label { get; init; }

    /// <summary><see langword="null" /> means the key may invoke EVERY trigger.</summary>
    public IReadOnlyList<Guid>? AllowedTriggerIds { get; init; }

    public required long CreatedAtUtc { get; init; }

    public long? LastUsedAtUtc { get; init; }

    /// <summary>Non-null on a revoked credential. The row survives revocation because execution rows reference its prefix.</summary>
    public long? RevokedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET integrations/keys</c>.</summary>
public sealed class ListIntegrationApiKeysResponse
{
    public required IReadOnlyList<IntegrationApiKeyView> Items { get; init; }
}

/// <summary>Body of <c>POST integrations/keys</c>.</summary>
public sealed class GenerateIntegrationApiKeyRequest
{
    public required string Label { get; init; }

    /// <summary>Omit — or send <see langword="null" /> — to let the key invoke every trigger.</summary>
    public IReadOnlyList<Guid>? AllowedTriggerIds { get; init; }

    /// <summary>
    ///     Omit to mint a NEW integrator identity. Supplying an existing principal rotates or adds a credential for
    ///     that integrator, so the new key inherits everything the old one owned.
    /// </summary>
    public Guid? PrincipalId { get; init; }
}

/// <summary>
///     Response of <c>POST integrations/keys</c>. <see cref="Key" /> is the ONLY time the plaintext exists outside the
///     caller: every later read returns <see cref="View" /> alone.
/// </summary>
public sealed class GenerateIntegrationApiKeyResponse
{
    public required string Key { get; init; }

    public required IntegrationApiKeyView View { get; init; }
}
