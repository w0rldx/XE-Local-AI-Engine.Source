namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

public sealed partial class WorkerEventDispatcher
{
    private async Task DispatchPlainInvocationAsync(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        _logger.LogInformation("WorkerEventDispatcher handling plain InvocationAssignedV2. InvocationId={InvocationId} ConversationId={ConversationId}",
            package.InvocationId,
            package.ConversationId);

        using var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty);

        try
        {
            await _remoteInvocationQueue.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Abandoning queued remote invocation {InvocationId}: the worker is draining for shutdown and will not start new queued work.", package.InvocationId);
            return;
        }

        try
        {
            await RunQueuedInvocationAsync(context, package).ConfigureAwait(false);
        }
        finally
        {
            _ = _remoteInvocationQueue.Release();
        }
    }

    private async Task RunQueuedInvocationAsync(InvocationExecutionContext context, RuntimePackage runtimePackage)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runtimePackage);

        InvocationState snapshot;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                _logger.LogWarning("Delaying invocation assignment for {InvocationId} because invocation {CurrentInvocationId} is still active.",
                    runtimePackage.InvocationId,
                    CurrentInvocation!.InvocationId);
            }

            CurrentInvocation = new InvocationState
            {
                InvocationId = runtimePackage.InvocationId,
                ConversationId = runtimePackage.ConversationId,
                Status = InvocationStatus.Assigned,
                StartedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                ModelUsed = runtimePackage.ModelProfile
            };

            snapshot = CurrentInvocation.Clone();
        }

        _logger.LogInformation("Dispatched invocation assignment for {InvocationId}.", runtimePackage.InvocationId);
        PublishStateChanged(snapshot);

        await RunInvocationWithRemotePersistenceAsync(context, runtimePackage).ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs a platform-served invocation while persisting its chat content to node SQLite with Origin=Remote.
    ///     The dispatcher stays thin: it opens a persistence session (ensure-conversation + user/assistant rows),
    ///     fans this invocation's <see cref="InvocationStateChanged" /> deltas into the shared pump via the session,
    ///     then terminalizes. All persistence translation lives in the coordinator/pump, not here.
    /// </summary>
    private async Task RunInvocationWithRemotePersistenceAsync(InvocationExecutionContext context, RuntimePackage runtimePackage)
    {
        NodeChatRemotePersistenceSession? session;

        try
        {
            session = await _remotePersistenceCoordinator.BeginAsync(runtimePackage, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Persistence is best-effort relative to the agent run: never block/fail a platform invocation
            // because the node-local mirror could not be written. Run without persistence in that case.
            _logger.LogError(exception, "Failed to begin remote persistence for invocation {InvocationId}; running without node-local persistence.", runtimePackage.InvocationId);
            await RunInvocationAsync(context).ConfigureAwait(false);
            return;
        }

        if (session is null)
        {
            // The assistant row reached a terminal status before it could be marked streaming (e.g. an early cancel), so
            // there is nothing to persist into. Run the invocation without the node-local mirror rather than driving the
            // pump against a terminal row.
            _logger.LogInformation("Remote persistence session not opened for invocation {InvocationId} (assistant row already terminal); running without node-local persistence.",
                runtimePackage.InvocationId);
            await RunInvocationAsync(context).ConfigureAwait(false);
            return;
        }

        var stateChannel = Channel.CreateUnbounded<InvocationState>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        void OnInvocationStateChanged(object? _, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == runtimePackage.InvocationId)
            {
                stateChannel.Writer.TryWrite(args.State);
            }
        }

        InvocationStateChanged += OnInvocationStateChanged;
        var persistenceTask = DrainRemotePersistenceAsync(session, stateChannel.Reader, runtimePackage.InvocationId);

        try
        {
            await RunInvocationAsync(context).ConfigureAwait(false);
        }
        finally
        {
            InvocationStateChanged -= OnInvocationStateChanged;
            stateChannel.Writer.TryComplete();
            await persistenceTask.ConfigureAwait(false);
        }
    }

    private async Task DrainRemotePersistenceAsync(NodeChatRemotePersistenceSession session,
        ChannelReader<InvocationState> stateReader,
        Guid invocationId)
    {
        var terminalPersisted = false;

        try
        {
            await foreach (var state in stateReader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                terminalPersisted = await session.ApplyAsync(state, CancellationToken.None).ConfigureAwait(false);
                if (terminalPersisted)
                {
                    break;
                }
            }

            if (!terminalPersisted)
            {
                // The run ended without a terminal state reaching us (process/stream loss). Terminalize the
                // node-local mirror as interrupted so it does not hang in a non-terminal state.
                await session.TerminalizeInterruptedAsync(false).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Remote persistence drain failed for invocation {InvocationId}.", invocationId);
        }
    }

    private async Task EmitInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed)
    {
        _logger.LogWarning("Emitting invocation key mismatch for {MessageId}. Reason: {Reason}, key: {NodeKeyIdUsed}",
            messageId,
            reason,
            nodeKeyIdUsed);
        await _hubMessageSender.Value.SendInvocationKeyMismatchAsync(messageId, reason, nodeKeyIdUsed, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EmitEncryptedFailureAsync(EncryptedRuntimePackageDto package, string error, FailureCategory failureCategory = FailureCategory.AgentRuntime)
    {
        if (failureCategory == FailureCategory.HashMismatch)
        {
            NodeMetrics.EnvelopeHashMismatchTotal.Add(delta: 1, new KeyValuePair<string, object?>("reason", error));
        }

        await _hubMessageSender.Value.SendEncryptedFailedAsync(new EncryptedFailedEnvelopeV1
        {
            ConversationId = package.ConversationId,
            MessageId = package.MessageId,
            EpochVersion = package.EpochVersion,
            FailureCategory = failureCategory.ToString(),
            Error = error
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunInvocationAsync(InvocationExecutionContext context)
    {
        var package = context.Package;

        _logger.LogInformation("Starting invocation execution. InvocationId={InvocationId} ConversationId={ConversationId} Model={Model}",
            package.InvocationId,
            package.ConversationId,
            package.ModelProfile);

        UpdateInvocation(package.InvocationId,
            static state =>
            {
                state.Status = InvocationStatus.Running;
                return state;
            });

        try
        {
            await _invocationRunner.RunAsync(context, CancellationToken.None).ConfigureAwait(false);

            UpdateInvocation(package.InvocationId,
                static state =>
                {
                    if (state.Status is InvocationStatus.Assigned or InvocationStatus.Running)
                    {
                        state.Status = InvocationStatus.Completed;
                        state.CompletedAt = DateTimeOffset.UtcNow;
                    }

                    return state;
                });

            _logger.LogInformation("Invocation execution completed successfully. InvocationId={InvocationId}", package.InvocationId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Invocation {InvocationId} failed before execution completed.", package.InvocationId);

            UpdateInvocation(package.InvocationId,
                state =>
                {
                    state.Status = InvocationStatus.Failed;
                    state.Error = exception.Message;
                    state.FailureCategory = FailureCategory.Unexpected;
                    state.CompletedAt = DateTimeOffset.UtcNow;
                    return state;
                });
        }
    }
}
