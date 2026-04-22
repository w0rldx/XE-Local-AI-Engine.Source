namespace XE_Local_AI_Engine.Client.Services.Chat;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed class LocalChatInvocationService : ILocalChatInvocationService, IDisposable
{
    private const int CurrentAgentDefinitionVersion = 1;

    private readonly List<ConversationMessageDto> _conversationContext = [];
    private readonly IWorkerEventDispatcher _eventDispatcher;
    private readonly IInvocationRunner _invocationRunner;
    private readonly string _resolvedSystemPrompt;
    private readonly ILocalChatRuntimePackageBuilder _runtimePackageBuilder;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private int _activeSend;
    private bool _disposed;

    public LocalChatInvocationService(IOptions<LocalChatAgentOptions> options,
        ILocalChatRuntimePackageBuilder runtimePackageBuilder,
        IInvocationRunner invocationRunner,
        IWorkerEventDispatcher eventDispatcher)
    {
        ArgumentNullException.ThrowIfNull(options);
        _runtimePackageBuilder = runtimePackageBuilder ?? throw new ArgumentNullException(nameof(runtimePackageBuilder));
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));

        var resolvedOptions = options.Value;
        SelectedModel = resolvedOptions.DefaultModel;
        ToolsEnabled = resolvedOptions.EnableTools;
        ConversationId = Guid.NewGuid();
        _resolvedSystemPrompt = LoadResolvedSystemPrompt(resolvedOptions);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateLock.Dispose();
    }

    public int AgentDefinitionVersion => CurrentAgentDefinitionVersion;

    public Guid ConversationId { get; private set; }

    public string SelectedModel { get; private set; }

    public bool ToolsEnabled { get; }

    public async ValueTask<LocalChatInvocationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new LocalChatInvocationSnapshot(ConversationId, SelectedModel, AgentDefinitionVersion, ToolsEnabled);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<Guid> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (Interlocked.CompareExchange(ref _activeSend, 1, 0) != 0)
        {
            throw new InvalidOperationException("A local invocation is already in progress.");
        }

        RuntimePackage package;
        var invocationId = Guid.NewGuid();
        var trimmedMessage = userMessage.Trim();

        try
        {
            await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _conversationContext.Add(new ConversationMessageDto
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = trimmedMessage,
                    SortOrder = _conversationContext.Count
                });

                package = _runtimePackageBuilder.Build(new LocalChatRuntimePackageRequest(invocationId,
                    ConversationId,
                    _resolvedSystemPrompt,
                    [.. _conversationContext],
                    SelectedModel,
                    AgentDefinitionVersion,
                    LocalChatLoopbackDefaults.ClientNodeId,
                    RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability]));
            }
            finally
            {
                _stateLock.Release();
            }

            await _eventDispatcher.ReportInvocationAssignedAsync(package).ConfigureAwait(false);

            using var context = InvocationExecutionContext.Create(package,
                Guid.NewGuid(),
                LocalChatLoopbackDefaults.EpochVersion,
                ReadOnlyMemory<byte>.Empty);

            await _invocationRunner.RunAsync(context, cancellationToken).ConfigureAwait(false);
            await AppendAssistantMessageAsync(invocationId, cancellationToken).ConfigureAwait(false);
            return invocationId;
        }
        finally
        {
            Interlocked.Exchange(ref _activeSend, 0);
        }
    }

    public async Task ResetConversationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConversationId = Guid.NewGuid();
            _conversationContext.Clear();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SelectedModel = modelId.Trim();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task AppendAssistantMessageAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var invocationState = _eventDispatcher.CurrentInvocation;
        if (invocationState?.InvocationId != invocationId ||
            invocationState.Status != InvocationStatus.Completed ||
            string.IsNullOrWhiteSpace(invocationState.StreamedContent))
        {
            return;
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _conversationContext.Add(new ConversationMessageDto
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.Assistant,
                Content = invocationState.StreamedContent,
                Thinking = string.IsNullOrWhiteSpace(invocationState.StreamedThinkingContent)
                    ? null
                    : invocationState.StreamedThinkingContent,
                ModelUsed = invocationState.ModelUsed,
                SortOrder = _conversationContext.Count
            });
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private static string LoadResolvedSystemPrompt(LocalChatAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstructionsResource);

        var assembly = typeof(LocalChatAgentOptions).Assembly;
        using var stream = assembly.GetManifestResourceStream(options.InstructionsResource);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded instructions resource '{options.InstructionsResource}' was not found.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
