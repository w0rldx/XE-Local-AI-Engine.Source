namespace XE_Local_AI_Engine.Client.Services.Connection.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Events;

public sealed partial class WorkerHubConnection
{
    private void RegisterEventHandlers(HubConnection connection)
    {
        connection.On("CapabilitiesReportRequested", ReportCapabilitiesRequestedAsync);
        connection.On<JsonElement>("InvocationAssigned",
            raw =>
            {
                _logger.LogInformation("InvocationAssigned raw frame received. RawJson={RawJson}",
                    raw.GetRawText());

                EncryptedRuntimePackageDto package;
                try
                {
                    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        Converters =
                        {
                            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                        }
                    };
                    package = raw.Deserialize<EncryptedRuntimePackageDto>(options)
                              ?? throw new InvalidOperationException("Deserialized EncryptedRuntimePackageDto was null.");
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception,
                        "InvocationAssigned manual deserialization failed. RawJson={RawJson}",
                        raw.GetRawText());
                    return;
                }

                _logger.LogInformation("InvocationAssigned typed binding succeeded. InvocationId={InvocationId} ConversationId={ConversationId} MessageId={MessageId} EpochVersion={EpochVersion}",
                    package.InvocationId,
                    package.ConversationId,
                    package.MessageId,
                    package.EpochVersion);

                var handler = InvocationAssignedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("InvocationAssigned received but no handler subscribed. InvocationId={InvocationId}", package.InvocationId);
                    return;
                }

                _logger.LogDebug("Dispatching InvocationAssigned to subscribers. InvocationId={InvocationId}", package.InvocationId);
                handler.Invoke(this, new InvocationAssignedReceivedEventArgs(package));
            });
        connection.On<JsonElement>("InvocationAssignedV2",
            raw =>
            {
                _logger.LogInformation("InvocationAssignedV2 raw frame received. RawJson={RawJson}",
                    raw.GetRawText());

                InvocationAssignedEnvelope envelope;
                try
                {
                    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        Converters =
                        {
                            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                        }
                    };
                    envelope = raw.Deserialize<InvocationAssignedEnvelope>(options)
                               ?? throw new InvalidOperationException("Deserialized InvocationAssignedEnvelope was null.");
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception,
                        "InvocationAssignedV2 manual deserialization failed. RawJson={RawJson}",
                        raw.GetRawText());
                    return;
                }

                var handler = InvocationAssignedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("InvocationAssignedV2 received but no handler subscribed. StorageMode={StorageMode}", envelope.StorageMode);
                    return;
                }

                handler.Invoke(this, new InvocationAssignedReceivedEventArgs(envelope));
            });
        connection.On<ToolCallResultEvent>("ToolCallResult",
            evt =>
            {
                _logger.LogInformation("ToolCallResult received. RequestId={RequestId} HasError={HasError}",
                    evt.RequestId,
                    !string.IsNullOrWhiteSpace(evt.Error));

                var handler = ToolCallResultReceived;
                if (handler is null)
                {
                    _logger.LogWarning("ToolCallResult received but no handler subscribed. RequestId={RequestId}", evt.RequestId);
                    return;
                }

                _logger.LogDebug("Dispatching ToolCallResult to subscribers. RequestId={RequestId}", evt.RequestId);
                handler.Invoke(this, new ToolCallResultReceivedEventArgs(evt));
            });
        connection.On<DisconnectRequestedEvent>("DisconnectRequested",
            evt =>
            {
                _logger.LogInformation("DisconnectRequested received. Reason={Reason}", evt.Reason);

                var handler = DisconnectRequestedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("DisconnectRequested received but no handler subscribed. Reason={Reason}", evt.Reason);
                    return;
                }

                _logger.LogDebug("Dispatching DisconnectRequested to subscribers. Reason={Reason}", evt.Reason);
                handler.Invoke(this, new DisconnectRequestedReceivedEventArgs(evt));
            });
        connection.On<ApprovalResolvedEvent>("ApprovalResolved",
            evt =>
            {
                _logger.LogInformation("ApprovalResolved received. RequestId={RequestId} Approved={Approved}",
                    evt.RequestId,
                    evt.Approved);

                var handler = ApprovalResolvedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("ApprovalResolved received but no handler subscribed. RequestId={RequestId}", evt.RequestId);
                    return;
                }

                _logger.LogDebug("Dispatching ApprovalResolved to subscribers. RequestId={RequestId}", evt.RequestId);
                handler.Invoke(this, new ApprovalResolvedReceivedEventArgs(evt));
            });
        connection.On<InvocationCancelledEvent>("InvocationCancelled",
            evt =>
            {
                _logger.LogInformation("InvocationCancelled received. InvocationId={InvocationId} Reason={Reason}",
                    evt.InvocationId,
                    evt.Reason);

                var handler = InvocationCancelledReceived;
                if (handler is null)
                {
                    _logger.LogWarning("InvocationCancelled received but no handler subscribed. InvocationId={InvocationId}", evt.InvocationId);
                    return;
                }

                _logger.LogDebug("Dispatching InvocationCancelled to subscribers. InvocationId={InvocationId}", evt.InvocationId);
                handler.Invoke(this, new InvocationCancelledReceivedEventArgs(evt));
            });
        connection.On<Guid>("ConversationPurged",
            conversationId =>
            {
                _logger.LogInformation("ConversationPurged received. ConversationId={ConversationId}", conversationId);

                var handler = ConversationPurgedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("ConversationPurged received but no handler subscribed. ConversationId={ConversationId}", conversationId);
                    return;
                }

                _logger.LogDebug("Dispatching ConversationPurged to subscribers. ConversationId={ConversationId}", conversationId);
                handler.Invoke(this,
                    new ConversationPurgedReceivedEventArgs(new ConversationPurgedEvent
                    {
                        ConversationId = conversationId
                    }));
            });
    }

    private async Task ReportCapabilitiesRequestedAsync()
    {
        try
        {
            _logger.LogInformation("Capabilities report requested by central platform.");
            await _capabilityReporter.Value.ReportToApiAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after central platform request.");
        }
    }
}
