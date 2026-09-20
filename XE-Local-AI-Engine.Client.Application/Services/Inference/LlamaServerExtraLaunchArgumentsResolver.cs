namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Store-backed <see cref="ILlamaServerExtraLaunchArgumentsResolver" />: turns the persisted per-model override
///     into the sanitized extra flags the supervisor appends on a cold spawn. Registered LAST so it wins over the
///     provider's empty default.
/// </summary>
/// <remarks>
///     The app-managed flags — reachability (model path / host / port) and memory-fit placement (context / GPU-layer /
///     KV / flash-attn / parallel / batch) — are stripped via <see cref="LlamaLaunchArgumentParser.ParseSanitized" />,
///     a defense-in-depth backstop to the write-path rejection. Singleton on the cold spawn path: <see cref="IModelLaunchArgumentsStore" /> is SCOPED,
///     so the resolver takes a fresh <see cref="IServiceScopeFactory" /> scope per call, mirroring <see cref="InferenceProfileResolver" />. This path
///     must NEVER throw: a store read failure degrades to no extra args, the model launching on the bundled defaults.
/// </remarks>
public sealed class LlamaServerExtraLaunchArgumentsResolver : ILlamaServerExtraLaunchArgumentsResolver
{
    private static readonly IReadOnlyList<string> None = [];

    private readonly ILogger<LlamaServerExtraLaunchArgumentsResolver> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public LlamaServerExtraLaunchArgumentsResolver(IServiceScopeFactory scopeFactory,
        ILogger<LlamaServerExtraLaunchArgumentsResolver> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IModelLaunchArgumentsStore>();
            var raw = await store.GetRawArgumentsAsync(modelName, ct);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return None;
            }

            var sanitized = LlamaLaunchArgumentParser.ParseSanitized(raw);
            if (sanitized.Count > 0)
            {
                _logger.LogInformation("Applying {Count} operator-supplied extra llama-server argument(s) for model {ModelName} ({Role}).",
                    sanitized.Count, modelName, role);
            }

            return sanitized;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never break the spawn over an override read — degrade to the bundled defaults.
            _logger.LogWarning(ex, "Failed to resolve the per-model extra llama-server arguments for {ModelName}; launching without them.", modelName);
            return None;
        }
    }
}
