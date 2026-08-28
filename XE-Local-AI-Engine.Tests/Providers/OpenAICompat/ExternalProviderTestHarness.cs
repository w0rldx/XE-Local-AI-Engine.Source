namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using System.Net;
using System.Text;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Shared fixtures for the external-provider tests: an in-memory registry and a recording transport, so the whole
///     production stack (endpoint guard → OpenAI adapter → reasoning rewriting) can be driven end to end and the REAL
///     wire request inspected, with no network and no encrypted store.
/// </summary>
internal static class ExternalProviderTestData
{
    public const string ConnectionId = "unsloth-box";
    public const string WireId = "unsloth/Qwen3.8-27B-GGUF";
    public const string ModelId = $"ext:{ConnectionId}/{WireId}";

    public static ExternalProviderConnectionDescriptor Connection(string? id = null,
        string baseUrl = "http://127.0.0.1:18099/v1",
        ExternalProviderLocality locality = ExternalProviderLocality.Local)
    {
        return new ExternalProviderConnectionDescriptor
        {
            Id = id ?? ConnectionId,
            DisplayName = "Unsloth box",
            BaseUrl = new Uri(baseUrl, UriKind.Absolute),
            Locality = locality,
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    public static ExternalProviderModelDescriptor Model(bool supportsEffort = false,
        string? defaultEffort = null,
        int? contextLength = null,
        bool supportsTools = false,
        bool supportsVision = false,
        bool supportsReasoning = false)
    {
        return new ExternalProviderModelDescriptor
        {
            WireId = WireId,
            DisplayName = "Qwen3.8 27B",
            ContextLength = contextLength,
            SupportsTools = supportsTools,
            SupportsVision = supportsVision,
            SupportsReasoning = supportsReasoning,
            SupportsReasoningEffort = supportsEffort,
            DefaultReasoningEffort = defaultEffort
        };
    }
}

/// <summary>In-memory <see cref="IExternalProviderRegistry" />; the registry contract's only test double.</summary>
internal sealed class FakeExternalProviderRegistry : IExternalProviderRegistry
{
    private readonly Dictionary<string, string?> _apiKeys = new(StringComparer.Ordinal);
    private readonly List<ExternalProviderModelRegistration> _registrations = [];

    public int ResolveCallCount { get; private set; }

    /// <summary>
    ///     The generation every binding is stamped with. <see cref="Replace" /> bumps it, mirroring the real registry's
    ///     epoch, so a test can reconfigure a connection mid-invocation and the pin check sees a moved generation.
    /// </summary>
    public long Generation { get; private set; }

    public FakeExternalProviderRegistry Add(ExternalProviderConnectionDescriptor connection,
        ExternalProviderModelDescriptor model,
        string? apiKey = null)
    {
        _registrations.Add(new ExternalProviderModelRegistration(connection, model));
        _apiKeys[connection.Id] = apiKey;
        return this;
    }

    public void Replace(ExternalProviderConnectionDescriptor connection, ExternalProviderModelDescriptor model, string? apiKey = null)
    {
        _registrations.Clear();
        _apiKeys.Clear();
        Generation++;
        _ = Add(connection, model, apiKey);
    }

    public Task<IReadOnlyList<ExternalProviderModelRegistration>> ListRegistrationsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ExternalProviderModelRegistration>>([.. _registrations]);
    }

    public Task<ExternalProviderModelRegistration?> TryResolveAsync(string modelId, CancellationToken ct)
    {
        ResolveCallCount++;
        var canonical = ExternalModelId.Canonicalize(modelId);
        return Task.FromResult(_registrations.FirstOrDefault(registration =>
            string.Equals(registration.ModelId, canonical, StringComparison.Ordinal)));
    }

    public async Task<ExternalProviderBinding?> TryResolveBindingAsync(string modelId, CancellationToken ct)
    {
        return (await TryResolveTransportBindingAsync(modelId, ct))?.Binding;
    }

    public async Task<ExternalProviderTransportBinding?> TryResolveTransportBindingAsync(string modelId, CancellationToken ct)
    {
        // Resolved through the same path a caller takes, so the endpoint and the key are always from one generation —
        // the property the production registry guarantees and these tests must not accidentally relax.
        if (await TryResolveAsync(modelId, ct) is not { } registration)
        {
            return null;
        }

        return new ExternalProviderTransportBinding(new ExternalProviderBinding(Generation, registration),
            _apiKeys.GetValueOrDefault(registration.Connection.Id));
    }
}

/// <summary>One request as it actually left the process, captured after every production policy has run.</summary>
internal sealed record RecordedRequest(Uri? Uri, string? Body, string? Authorization, bool HasAuthorizationHeader);

/// <summary>
///     Records outbound requests and replays canned responses. It hands out a FRESH handler per call because the
///     production chat client owns and disposes the transport it is given; the recorder itself outlives them.
/// </summary>
internal sealed class OpenAiWireRecorder
{
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>Produces the response for the n-th request (0-based), so a multi-turn exchange can be scripted.</summary>
    public Func<int, HttpResponseMessage> Responder { get; set; } = static _ => Completion("ok");

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public RecordedRequest LastRequest => _requests[^1];

    public HttpMessageHandler CreateHandler()
    {
        return new RecordingHandler(this);
    }

    /// <summary>A canned non-streaming chat completion, optionally carrying extra JSON inside the assistant message.</summary>
    public static HttpResponseMessage Completion(string content, string? extraMessageJson = null)
    {
        var extra = extraMessageJson is null ? string.Empty : "," + extraMessageJson;
        var payload = "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"m\","
                      + $"\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":{JsonString(content)}{extra}}},\"finish_reason\":\"stop\"}}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>A canned SSE stream built from raw delta JSON fragments, terminated the way a real server terminates it.</summary>
    public static HttpResponseMessage Stream(params string[] deltaJson)
    {
        var builder = new StringBuilder();
        foreach (var delta in deltaJson)
        {
            _ = builder.Append("data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",")
                       .Append("\"choices\":[{\"index\":0,\"delta\":")
                       .Append(delta)
                       .Append("}]}\n\n");
        }

        _ = builder.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    private static string JsonString(string value)
    {
        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    private sealed class RecordingHandler(OpenAiWireRecorder recorder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            int index;
            lock (recorder._requests)
            {
                index = recorder._requests.Count;
                recorder._requests.Add(new RecordedRequest(request.RequestUri,
                    body,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.Contains("Authorization")));
            }

            return recorder.Responder(index);
        }
    }
}
