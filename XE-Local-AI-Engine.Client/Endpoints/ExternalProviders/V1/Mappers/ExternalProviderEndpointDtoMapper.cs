namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Pure translation between the external-provider endpoint DTOs and the application layer's store contracts.
/// </summary>
/// <remarks>
///     The one rule this file exists to keep: the API key crosses INWARD only. A stored connection maps to a response
///     through <see cref="ToResponse(StoredExternalProviderConnection)" />, which has no key field to fill, so no
///     future edit here can leak one by accident.
/// </remarks>
internal static class ExternalProviderEndpointDtoMapper
{
    /// <summary>Projects the whole stored configuration onto the editor's read model.</summary>
    public static ExternalProviderConnectionsResponse ToResponse(this StoredExternalProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ExternalProviderConnectionsResponse
        {
            Revision = config.Revision,
            Connections = [.. config.Connections.Select(ToResponse)]
        };
    }

    /// <summary>Projects one stored connection onto its read model, reporting only the PRESENCE of an API key.</summary>
    public static ExternalProviderConnectionResponse ToResponse(this StoredExternalProviderConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new ExternalProviderConnectionResponse
        {
            Id = connection.Id,
            DisplayName = connection.DisplayName,
            BaseUrl = connection.BaseUrl,
            Locality = connection.Locality.ToString(),
            HasApiKey = !string.IsNullOrEmpty(connection.ApiKey),
            TimeoutSeconds = connection.TimeoutSeconds,
            Models = [.. connection.Models.Select(model => ToResponse(model, connection.Id))]
        };
    }

    /// <summary>
    ///     Maps the save DTO onto the store's request. The API key is passed through UNTOUCHED, including its absence:
    ///     the store's own merge is what distinguishes "keep the stored key" from "clear it", and normalizing a blank
    ///     to an empty string here would collapse that distinction before it ever reached the merge.
    /// </summary>
    public static ExternalProviderConnectionSaveRequest ToSaveRequest(this SaveExternalProviderConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ExternalProviderConnectionSaveRequest
        {
            Id = request.ConnectionId ?? string.Empty,
            DisplayName = request.DisplayName?.Trim() ?? string.Empty,
            BaseUrl = request.BaseUrl?.Trim() ?? string.Empty,
            Locality = ParseLocality(request.Locality),
            ApiKey = request.ApiKey,
            ClearApiKey = request.ClearApiKey,
            TimeoutSeconds = request.TimeoutSeconds,
            Models = [.. request.Models.Select(ToSaveRequest)],
            ExpectedRevision = request.ExpectedRevision
        };
    }

    /// <summary>Maps the probe result onto its response, collapsing the outcome enum to the wire's reachable flag.</summary>
    public static ExternalProviderProbeResponse ToResponse(this ExternalProviderProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ExternalProviderProbeResponse
        {
            Reachable = result.Outcome == ExternalProviderProbeOutcome.Answered,
            Error = result.Error,
            Models =
            [
                .. result.Models.Select(static model => new ExternalProviderProbeModelResponse
                {
                    Id = model.Id,
                    ContextLength = model.ContextLength
                })
            ]
        };
    }

    private static ExternalProviderModelResponse ToResponse(StoredExternalProviderModel model, string connectionId)
    {
        return new ExternalProviderModelResponse
        {
            WireId = model.WireId,
            ModelId = ExternalModelId.Format(connectionId, model.WireId),
            DisplayName = model.DisplayName,
            ContextLength = model.ContextLength,
            SupportsTools = model.SupportsTools,
            SupportsVision = model.SupportsVision,
            SupportsReasoning = model.SupportsReasoning,
            SupportsReasoningEffort = model.SupportsReasoningEffort,
            DefaultReasoningEffort = model.DefaultReasoningEffort
        };
    }

    private static ExternalProviderModelSaveRequest ToSaveRequest(SaveExternalProviderModelRequest model)
    {
        return new ExternalProviderModelSaveRequest
        {
            WireId = model.WireId?.Trim() ?? string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim(),
            ContextLength = model.ContextLength,
            SupportsTools = model.SupportsTools,
            SupportsVision = model.SupportsVision,
            SupportsReasoning = model.SupportsReasoning,
            SupportsReasoningEffort = model.SupportsReasoningEffort,
            DefaultReasoningEffort = string.IsNullOrWhiteSpace(model.DefaultReasoningEffort) ? null : model.DefaultReasoningEffort.Trim()
        };
    }

    /// <summary>
    ///     Parses the declared locality. An unparseable value maps to <see cref="ExternalProviderLocality.Cloud" /> —
    ///     the fail-closed direction — but the request validator rejects it first, so this branch is a backstop, never
    ///     the operator's experience.
    /// </summary>
    private static ExternalProviderLocality ParseLocality(string? locality)
    {
        return Enum.TryParse<ExternalProviderLocality>(locality?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : ExternalProviderLocality.Cloud;
    }
}
