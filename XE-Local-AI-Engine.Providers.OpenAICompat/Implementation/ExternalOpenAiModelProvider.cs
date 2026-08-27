namespace XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;

using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     The single <see cref="ILocalModelProvider" /> serving EVERY operator-registered external OpenAI-compatible
///     connection — a multiplexer, not one provider per connection.
/// </summary>
/// <remarks>
///     <para>
///         WHY one registration: the provider resolver snapshots the registered <see cref="ILocalModelProvider" /> set
///         in its constructor, so a provider-per-connection design would require rebuilding the container every time an
///         operator adds a connection. Here the connection is recovered from the model id
///         (<c>ext:{connectionId}/{wireId}</c>) and looked up live in the registry, so adding, editing or removing a
///         connection needs no restart and no DI change.
///     </para>
///     <para>
///         WHY the lifecycle operations split the way they do: <see cref="WarmModelAsync" /> and
///         <see cref="UnloadModelAsync" /> are benign no-ops because the keep-warm background service calls warm
///         generically for whatever model is selected — throwing there would turn "an external model is the default"
///         into a recurring background failure. <see cref="PullModelAsync" /> and <see cref="DeleteModelAsync" /> DO
///         throw, because silently succeeding at deleting a model the node does not own would be a lie the UI would
///         then render as a completed deletion.
///     </para>
/// </remarks>
public sealed class ExternalOpenAiModelProvider : ILocalModelProvider
{
    /// <summary>
    ///     Bounds the connect-time reachability probe. Deliberately much shorter than a chat deadline: health is polled
    ///     for a status surface, and an unreachable endpoint must report unreachable quickly rather than stall the page.
    /// </summary>
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IExternalProviderRegistry _registry;
    private readonly Func<HttpMessageHandler>? _transportHandlerFactory;

    /// <param name="registry">The read-only registry of connections and their registered models.</param>
    /// <param name="transportHandlerFactory">
    ///     Test seam supplying the innermost HTTP handler so the assembled stack (endpoint guard included) can be driven
    ///     without live network I/O. <see langword="null" /> in production.
    /// </param>
    public ExternalOpenAiModelProvider(IExternalProviderRegistry registry, Func<HttpMessageHandler>? transportHandlerFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _transportHandlerFactory = transportHandlerFactory;
    }

    /// <inheritdoc />
    public string ProviderName => ExternalProviderConstants.ProviderName;

    /// <inheritdoc />
    /// <remarks>
    ///     Probes every configured connection's <c>GET {base}/models</c> — the one endpoint that is near-universal
    ///     across OpenAI-compatible servers. A node with NO external connection is healthy, not unhealthy: nothing is
    ///     configured, so nothing is broken.
    /// </remarks>
    public async Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        var connections = await ListConnectionsAsync(ct).ConfigureAwait(false);
        if (connections.Count == 0)
        {
            return BuildHealth(isHealthy: true, ["No external connections are configured."]);
        }

        var probes = await Task.WhenAll(connections.Select(connection => ProbeConnectionAsync(connection, ct))).ConfigureAwait(false);
        return BuildHealth(probes.All(probe => probe.IsReachable), [.. probes.Select(probe => probe.Diagnostic)]);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Descriptors are built purely from the operator's DECLARATIONS — no probe. Nothing about an OpenAI-compatible
    ///     endpoint can be trusted to advertise tool, vision or reasoning support, so a claimed capability here is
    ///     always one a human asserted. <see cref="LocalModelDescriptor.IsAvailable" /> is true for every registered
    ///     model: availability is "the operator registered it", and reachability is the health surface's job.
    /// </remarks>
    public async Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
    {
        var registrations = await _registry.ListRegistrationsAsync(ct).ConfigureAwait(false);
        return [.. registrations.Select(ToDescriptor)];
    }

    /// <inheritdoc />
    public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        throw new ExternalProviderOperationNotSupportedException(
            "External models are served by their connection's endpoint and cannot be pulled onto this node.");
    }

    /// <inheritdoc />
    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        throw new ExternalProviderOperationNotSupportedException(
            "External models are removed by unregistering them on their connection, not by deleting local weights.");
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A no-op by design. There is no local process to pay a cold start for, and the keep-warm background service
    ///     warms whatever model is selected without asking which runtime serves it.
    /// </remarks>
    public Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>A no-op by design: the node holds no weights for an external model, so there is nothing to release.</remarks>
    public Task UnloadModelAsync(string modelName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Reports the operator-DECLARED context length as the effective window. There is no launched process to read a
    ///     real window from, and the declaration is the only figure that exists — without it the turn budgeter falls
    ///     back to its conservative default, which would silently truncate history on a large-window endpoint.
    /// </remarks>
    public async Task<LocalModelRuntimeInfo?> GetRuntimeInfoAsync(string modelName, CancellationToken ct)
    {
        var registration = await _registry.TryResolveAsync(modelName, ct).ConfigureAwait(false);
        return registration?.Model.ContextLength is > 0 ? new LocalModelRuntimeInfo(registration.Model.ContextLength.Value) : null;
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(LocalModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!string.Equals(selection.ProviderName, ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The selection targets provider '{selection.ProviderName}', not '{ProviderName}'.", nameof(selection));
        }

        return new ExternalOpenAiChatClient(_registry, selection.ModelName, _transportHandlerFactory);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Embeddings are node-local ONLY. Playbook and knowledge text is embedded before it is ever shown to a chat
    ///     model, so routing it through an operator-registered endpoint would send corpus content the user never chose
    ///     to send. A declared-Local connection does not change that: the seam has no way to prove the declaration.
    /// </remarks>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
    {
        throw new NotSupportedException("External OpenAI-compatible connections do not serve embeddings; embeddings stay on the node's local runtime.");
    }

    private static LocalModelDescriptor ToDescriptor(ExternalProviderModelRegistration registration)
    {
        var model = registration.Model;
        var capabilities = new List<string>(capacity: 4)
        {
            "completion"
        };
        if (model.SupportsTools)
        {
            capabilities.Add("tools");
        }

        if (model.SupportsReasoning)
        {
            capabilities.Add("thinking");
        }

        if (model.SupportsVision)
        {
            capabilities.Add("vision");
        }

        return new LocalModelDescriptor
        {
            ModelName = registration.ModelId,
            ProviderName = ExternalProviderConstants.ProviderName,
            IsAvailable = true,
            // Size and install time are properties of node-local weights; an external model has neither on this node.
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = model.ContextLength,
            IsToolCapable = model.SupportsTools,
            IsReasoningCapable = model.SupportsReasoning,
            // Native reasoning is a llama.cpp chat-template concept (the graded think switch a harmony-family template
            // lacks). External reasoning is declared, and the graded path here is the typed reasoning_effort field, so
            // the native flag stays false and never diverts an external model out of the graded branch.
            IsNativeReasoningCapable = false,
            // Vacuously true: the budget marker is a llama-server field this provider never emits, so nothing here can
            // silently lose a cap. Reporting false would make the UI warn about an enforcement gap that does not exist.
            ReasoningBudgetEnforceable = true,
            IsMultimodalCapable = model.SupportsVision,
            Capabilities = capabilities
        };
    }

    private async Task<IReadOnlyList<ExternalProviderConnectionDescriptor>> ListConnectionsAsync(CancellationToken ct)
    {
        var registrations = await _registry.ListRegistrationsAsync(ct).ConfigureAwait(false);
        return
        [
            .. registrations.Select(registration => registration.Connection)
                            .DistinctBy(connection => connection.Id, StringComparer.Ordinal)
        ];
    }

    private async Task<ConnectionProbe> ProbeConnectionAsync(ExternalProviderConnectionDescriptor connection, CancellationToken ct)
    {
        // Diagnostics are surfaced to the operator, so they name the connection's DISPLAY name and never its base URL,
        // key, or the raw exception text (which can embed both).
        try
        {
            var baseAddress = OpenAICompatibleBaseAddress.Normalize(connection.BaseUrl);

            // The handler chain transfers into the HttpClient (disposeHandler: true), which this scope disposes; the
            // guard disposes the inner handler in turn. CA2000 cannot follow that ownership transfer.
#pragma warning disable CA2000
            var inner = _transportHandlerFactory?.Invoke()
                        ?? new SocketsHttpHandler
                        {
                            ConnectTimeout = HealthProbeTimeout,
                            AllowAutoRedirect = false
                        };
            using var httpClient = new HttpClient(new ExternalEndpointGuardHandler(baseAddress, inner), disposeHandler: true)
            {
                Timeout = HealthProbeTimeout
            };
#pragma warning restore CA2000

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "models"));
            var apiKey = await _registry.GetApiKeyAsync(connection.Id, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new ConnectionProbe(IsReachable: true, $"'{connection.DisplayName}' responded to the model listing.")
                : new ConnectionProbe(IsReachable: false, $"'{connection.DisplayName}' rejected the model listing with status {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Includes the probe's own timeout (surfaced as a cancellation that is NOT the caller's) and every
            // transport failure. Health is a status surface: an unreachable endpoint is a verdict, not a fault.
            return new ConnectionProbe(IsReachable: false, $"'{connection.DisplayName}' is unreachable.");
        }
    }

    private ModelProviderHealth BuildHealth(bool isHealthy, IReadOnlyList<string> diagnostics)
    {
        return new ModelProviderHealth
        {
            ProviderName = ProviderName,
            IsHealthy = isHealthy,
            ObservedAt = DateTimeOffset.UtcNow,
            Diagnostics = diagnostics
        };
    }

    private readonly record struct ConnectionProbe(bool IsReachable, string Diagnostic);
}
