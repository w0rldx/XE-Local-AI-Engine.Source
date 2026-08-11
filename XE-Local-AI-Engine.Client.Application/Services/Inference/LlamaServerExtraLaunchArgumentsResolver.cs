namespace XE_Local_AI_Engine.Client.Services.Inference;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Store-backed <see cref="ILlamaServerExtraLaunchArgumentsResolver" />. Replaces the provider's empty default
///     (registered LAST so it wins) and turns the persisted per-model override into the sanitized extra flags the
///     supervisor appends on a cold spawn. Reserved process-contract flags (model path / host / port) are stripped via
///     <see cref="LlamaLaunchArgumentParser.ParseSanitized" /> as a defense-in-depth backstop to the write-path rejection.
/// </summary>
/// <remarks>
///     <para>
///         Singleton on the cold spawn path. <see cref="IModelLaunchArgumentsStore" /> is SCOPED, so the resolver
///         resolves it through a fresh <see cref="IServiceScopeFactory" /> scope per call rather than capturing the
///         scoped store in a singleton — mirroring <see cref="InferenceProfileResolver" />.
///     </para>
///     <para>
///         This path must NEVER throw: a store read failure degrades to "no extra args" (the model launches on the
///         bundled defaults), never an exception out of the supervisor's spawn.
///     </para>
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
            var raw = await store.GetRawArgumentsAsync(modelName, ct).ConfigureAwait(false);
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
