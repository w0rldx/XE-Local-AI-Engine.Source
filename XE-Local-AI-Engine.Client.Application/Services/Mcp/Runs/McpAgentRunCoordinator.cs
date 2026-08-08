namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Accepts durable runs with a cheap duplicate lookup before binding resolution; the store remains authoritative by
///     repeating duplicate and quota checks in its serialized admission transaction.
/// </summary>
internal sealed class McpAgentRunCoordinator : IMcpAgentRunCoordinator
{
    private const string InvalidRequestCode = "invalid_request";
    private const string RequestIdConflictCode = "request_id_conflict";
    private const string ResultExpiredCode = "result_expired";
    private const string CapacityExceededCode = "capacity_exceeded";

    private readonly McpAgentRunCancellationRegistry _cancellations;
    private readonly McpAgentRunRequestFingerprint _fingerprint;
    private readonly ILogger<McpAgentRunCoordinator> _logger;
    private readonly McpAgentRunMetrics _metrics;
    private readonly McpAgentRunOptions _options;
    private readonly IMcpExecutionBindingResolver _resolver;
    private readonly ISelectedFolderResolver _workspaceResolver;
    private readonly IMcpAgentRunStore _store;
    private readonly TimeProvider _timeProvider;

    public McpAgentRunCoordinator(IMcpAgentRunStore store,
        IMcpExecutionBindingResolver resolver,
        McpAgentRunRequestFingerprint fingerprint,
        McpAgentRunCancellationRegistry cancellations,
        McpAgentRunMetrics metrics,
        ISelectedFolderResolver workspaceResolver,
        IOptions<McpAgentRunOptions> options,
        TimeProvider timeProvider,
        ILogger<McpAgentRunCoordinator> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _workspaceResolver = workspaceResolver ?? throw new ArgumentNullException(nameof(workspaceResolver));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<McpAgentRunStartResult> StartAsync(McpAgentRunStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Task)
            || request.Binding is null
            || Encoding.UTF8.GetByteCount(request.Task) > _options.MaxTaskUtf8Bytes
            || Encoding.UTF8.GetByteCount(request.Binding.Instructions ?? string.Empty) > _options.MaxInstructionsUtf8Bytes)
        {
            return Reject(InvalidRequestCode, "Cannot start: the request identifier or bounded input is invalid.");
        }

        var requestFingerprint = _fingerprint.Compute(request);
        try
        {
            var existing = await _store.GetAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return MapExisting(existing, requestFingerprint);
            }

            var resolution = await _resolver.ResolveAsync(request.Binding, cancellationToken).ConfigureAwait(false);
            if (resolution.Binding is not { } binding)
            {
                return Reject(resolution.FailureCode ?? McpExecutionFailureCodes.InternalFailure, resolution.DisplayMessage);
            }

            if (Encoding.UTF8.GetByteCount(binding.Instructions) > _options.MaxInstructionsUtf8Bytes)
            {
                return Reject(InvalidRequestCode, "Cannot start: the resolved agent instructions exceed the durable run limit.");
            }

            var isWorkspaceCoder = McpExecutionBindingPolicy.IsExactReadOnlyWorkspaceCoder(binding);
            if ((isWorkspaceCoder && request.WorkspaceId is null)
                || (!isWorkspaceCoder && request.WorkspaceId is not null))
            {
                return Reject(McpAgentRunFailureCodes.WorkspaceNotAuthorized,
                    "Cannot start: the selected workspace is not authorized.");
            }

            if (request.WorkspaceId is { } workspaceId
                && !await IsWorkspaceAuthorizedAsync(workspaceId, cancellationToken).ConfigureAwait(false))
            {
                return Reject(McpAgentRunFailureCodes.WorkspaceNotAuthorized,
                    "Cannot start: the selected workspace is not authorized.");
            }

            var now = _timeProvider.GetUtcNow();
            var admission = await _store.AdmitAsync(new McpAgentRunAdmissionRequest(request.RequestId,
                    requestFingerprint,
                    request.Task,
                    binding.Instructions,
                    binding.AgentDefinitionId,
                    binding.AgentDefinitionVersion,
                    binding.ModelId,
                    NullIfWhiteSpace(request.Binding.ModelOverrideId),
                    request.WorkspaceId,
                    Convert.FromHexString(binding.BindingFingerprint),
                    now.ToUnixTimeMilliseconds()),
                cancellationToken).ConfigureAwait(false);

            await _metrics.RefreshAsync(_store, CancellationToken.None).ConfigureAwait(false);
            return MapAdmission(admission);
        }
        catch (Exception exception)
        {
            RecordStoreFailure("start", exception);
            throw;
        }
    }

    private async Task<bool> IsWorkspaceAuthorizedAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await _workspaceResolver.ResolveAsync(workspaceId.ToString("D"), cancellationToken).ConfigureAwait(false);
            return resolved.Id == workspaceId;
        }
        catch (SelectedFolderValidationException)
        {
            return false;
        }
    }

    public async Task<McpAgentRunView?> GetAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var run = await _store.GetAsync(requestId, cancellationToken).ConfigureAwait(false);
            return run is null ? null : ToView(run);
        }
        catch (Exception exception)
        {
            RecordStoreFailure("get", exception);
            throw;
        }
    }

    public async Task<IReadOnlyList<McpAgentRunView>> ListAsync(int? limit,
        McpAgentRunStatus? status,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit ?? _options.DefaultListLimit, 1, _options.MaxListLimit);
        try
        {
            var runs = await _store.ListAsync(boundedLimit, status, cancellationToken).ConfigureAwait(false);
            return runs.Select(ToView).ToArray();
        }
        catch (Exception exception)
        {
            RecordStoreFailure("list", exception);
            throw;
        }
    }

    public async Task<McpAgentRunCancelResult> CancelAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return new McpAgentRunCancelResult(McpAgentRunCancelKind.NotFound, null, "Run not found.");
        }

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var current = await _store.GetAsync(requestId, cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    return new McpAgentRunCancelResult(McpAgentRunCancelKind.NotFound, null, "Run not found.");
                }

                var stopped = await _store.RequestStopAsync(requestId,
                    current.Version,
                    McpAgentRunStopReason.UserCancellation,
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    cancellationToken).ConfigureAwait(false);

                if (stopped.Kind == McpAgentRunStopKind.VersionConflict)
                {
                    continue;
                }

                var result = MapCancel(stopped);
                if (stopped.Kind is McpAgentRunStopKind.Requested or McpAgentRunStopKind.AlreadyRequested)
                {
                    _cancellations.Signal(requestId, stopped.Run?.ClaimToken);
                }

                if (stopped.Kind == McpAgentRunStopKind.Requested)
                {
                    await _metrics.RefreshAsync(_store, CancellationToken.None).ConfigureAwait(false);
                }

                _metrics.RecordStop("user", McpAgentRunText.ToLowercaseInvariant(stopped.Kind));
                return result;
            }

            _metrics.RecordStop("user", "version_conflict");
            return new McpAgentRunCancelResult(McpAgentRunCancelKind.Conflict, null, "Run state changed; read it and retry.");
        }
        catch (Exception exception)
        {
            RecordStoreFailure("cancel", exception);
            throw;
        }
    }

    private McpAgentRunStartResult MapExisting(McpAgentRunRecord existing, byte[] requestFingerprint)
    {
        if (!CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint.Span, requestFingerprint))
        {
            _metrics.RecordLifecycle("request_id_conflict");
            return Reject(RequestIdConflictCode, "Cannot start: the request identifier belongs to a different request.");
        }

        if (existing.PayloadExpired)
        {
            _metrics.RecordLifecycle("result_expired");
            return new McpAgentRunStartResult(McpAgentRunStartKind.ResultExpired,
                ToView(existing),
                ResultExpiredCode,
                "The retained result for this request has expired.");
        }

        _metrics.RecordLifecycle("existing");
        return new McpAgentRunStartResult(McpAgentRunStartKind.Existing, ToView(existing), null, "Existing run returned.");
    }

    private McpAgentRunStartResult MapAdmission(McpAgentRunAdmissionResult admission)
    {
        var view = admission.Run is null ? null : ToView(admission.Run);
        switch (admission.Kind)
        {
            case McpAgentRunAdmissionKind.Accepted:
                _metrics.RecordLifecycle("accepted");
                return new McpAgentRunStartResult(McpAgentRunStartKind.Accepted, view, null, "Run accepted.");
            case McpAgentRunAdmissionKind.Existing:
                _metrics.RecordLifecycle("existing");
                return new McpAgentRunStartResult(McpAgentRunStartKind.Existing, view, null, "Existing run returned.");
            case McpAgentRunAdmissionKind.ResultExpired:
                _metrics.RecordLifecycle("result_expired");
                return new McpAgentRunStartResult(McpAgentRunStartKind.ResultExpired,
                    view,
                    ResultExpiredCode,
                    "The retained result for this request has expired.");
            case McpAgentRunAdmissionKind.RequestIdConflict:
                _metrics.RecordLifecycle("request_id_conflict");
                return Reject(RequestIdConflictCode, "Cannot start: the request identifier belongs to a different request.");
            case McpAgentRunAdmissionKind.CapacityExceeded:
                _metrics.RecordQuota(McpAgentRunText.ToLowercaseInvariant(admission.CapacityKind));
                return new McpAgentRunStartResult(McpAgentRunStartKind.CapacityExceeded,
                    null,
                    CapacityExceededCode,
                    "Cannot start: the durable run ledger is at capacity.");
            default:
                return Reject(McpExecutionFailureCodes.InternalFailure, "Cannot start the run.");
        }
    }

    private static McpAgentRunCancelResult MapCancel(McpAgentRunStopResult result)
    {
        var view = result.Run is null ? null : ToView(result.Run);
        return result.Kind switch
        {
            McpAgentRunStopKind.Requested => new(McpAgentRunCancelKind.Requested, view, "Cancellation recorded."),
            McpAgentRunStopKind.AlreadyRequested => new(McpAgentRunCancelKind.AlreadyRequested, view, "Cancellation was already recorded."),
            McpAgentRunStopKind.AlreadyTerminal => new(McpAgentRunCancelKind.AlreadyTerminal, view, "Run is already terminal."),
            McpAgentRunStopKind.NotFound => new(McpAgentRunCancelKind.NotFound, null, "Run not found."),
            _ => new(McpAgentRunCancelKind.Conflict, view, "Run state changed; read it and retry.")
        };
    }

    internal static McpAgentRunView ToView(McpAgentRunRecord run) =>
        new(run.RequestId,
            run.Status,
            run.Version,
            run.StopReason,
            run.ModelId,
            run.AgentDefinitionId,
            run.WorkspaceId,
            run.Result,
            run.DisplayMessage,
            run.FailureCode,
            run.CreatedAtUtc,
            run.ClaimedAtUtc,
            run.CompletedAtUtc,
            run.PayloadExpiresAtUtc,
            run.CompactedAtUtc,
            run.PayloadExpired);

    private static McpAgentRunStartResult Reject(string failureCode, string displayMessage) =>
        new(McpAgentRunStartKind.Rejected, null, failureCode, displayMessage);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void RecordStoreFailure(string operation, Exception exception)
    {
        NodeSqliteContention.Record("raw", exception, _logger);
        _logger.LogError(exception, "Durable MCP agent run store operation {Operation} failed.", operation);
    }
}
