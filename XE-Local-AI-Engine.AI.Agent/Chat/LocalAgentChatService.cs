namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Sessions;
using XE_Local_AI_Engine.AI.Agent.Tools;

internal sealed class LocalAgentChatService : ILocalAgentChatService
{
    private readonly IChatClient _chatClient;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly ILogger<LocalAgentChatService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LocalChatAgentOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly IAgentToolRegistry _toolRegistry;
    private int _activeSend;

    private ChatClientAgent? _agent;
    private bool _disposed;
    private AgentSession? _session;

    public LocalAgentChatService(IChatClient chatClient,
        IOptions<LocalChatAgentOptions> options,
        IAgentInstructionProvider instructionProvider,
        IAgentToolRegistry toolRegistry,
        ILogger<LocalAgentChatService> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);

        _instructionProvider = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options.Value;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        SelectedModel = _options.DefaultModel;
    }

    public string SelectedModel { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.LogDebug("Disposing local agent chat service.");

        await _stateLock.WaitAsync();
        try
        {
            await DisposeSessionAsync();
            _agent = null;
        }
        finally
        {
            _stateLock.Release();
            _stateLock.Dispose();
        }
    }

    public async Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            var hadSession = await DisposeSessionAsync();
            if (hadSession)
            {
                _logger.LogInformation("AgentSessionReset {Reason} {ModelId}", "UserCleared", SelectedModel);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async IAsyncEnumerable<string> SendMessageAsync(string userMessage, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfDisposed();

        if (Interlocked.CompareExchange(ref _activeSend, 1, 0) != 0)
        {
            throw new InvalidOperationException("A send operation is already in progress.");
        }

        ChatClientAgent agent;
        AgentSession session;
        var modelId = SelectedModel;
        var tokenCount = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            (agent, session) = await EnsureAgentAndSessionAsync(cancellationToken);

            using var activity = AgentActivitySource.Instance.StartActivity("AgentRun");
            activity?.SetTag("agent.model_id", modelId);
            activity?.SetTag("agent.name", _options.AgentName);

            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["ModelId"] = modelId,
                ["AgentName"] = _options.AgentName,
                ["SessionId"] = session.GetHashCode().ToString()
            });

            _logger.LogInformation("AgentRunStarted {ModelId} {AgentName}", modelId, _options.AgentName);

            var runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                ModelId = modelId
            });

            await using var updates = agent.RunStreamingAsync(userMessage, session, runOptions, cancellationToken)
                                           .Select(static update => update.Text)
                                           .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                string? text;

                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }

                    text = updates.Current;
                }
                catch (Exception exception)
                {
                    stopwatch.Stop();
                    _logger.LogError(exception, "AgentRunFailed {ModelId} {AgentName} {DurationMs}", modelId, _options.AgentName, stopwatch.ElapsedMilliseconds);
                    throw;
                }

                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                tokenCount++;
                yield return text;
            }

            stopwatch.Stop();
            _logger.LogInformation("AgentRunCompleted {ModelId} {AgentName} {TokenCount} {DurationMs}", modelId, _options.AgentName, tokenCount, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            Interlocked.Exchange(ref _activeSend, 0);
        }
    }

    public async Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfDisposed();

        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(SelectedModel, modelId, StringComparison.Ordinal))
            {
                return;
            }

            SelectedModel = modelId;
            var hadSession = await DisposeSessionAsync();
            _agent = null;
            if (hadSession)
            {
                _logger.LogInformation("AgentSessionReset {Reason} {ModelId}", "ModelChanged", SelectedModel);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<(ChatClientAgent Agent, AgentSession Session)> EnsureAgentAndSessionAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            _agent ??= CreateAgent();

            if (_session is null)
            {
                _session = await _agent.CreateSessionAsync(cancellationToken);
                _logger.LogInformation("AgentSessionCreated {ModelId} {AgentName}", SelectedModel, _options.AgentName);
            }

            return (_agent, _session);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private ChatClientAgent CreateAgent()
    {
        IList<AITool> tools = _options.EnableTools ? [.. _toolRegistry.GetLocalChatTools()] : [];

        return new ChatClientAgent(_chatClient,
            _options.AgentName,
            _instructionProvider.GetLocalChatInstructions(),
            "XE Local AI Engine local chat agent.",
            tools,
            _loggerFactory,
            _serviceProvider);
    }

    private async Task<bool> DisposeSessionAsync()
    {
        if (_session is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            _logger.LogInformation("AgentSessionReset {Reason} {ModelId}", "Dispose", SelectedModel);
            _session = null;
            return true;
        }

        _session = null;
        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
