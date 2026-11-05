namespace XE_Local_AI_Engine.Tests.Integration;

using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     An external connection to a private AI gateway, end to end over a real socket against the fake gateway.
/// </summary>
/// <remarks>
///     Saved through the administration service into the real encrypted store, resolved by the real registry, and sent by
///     the real probe service, model provider and chat client; the fake's request log is the evidence of what left the node.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class ExternalGatewayIntegrationTests
{
    private const string ConnectionId = "demo-gateway";
    private const string WireId = "gateway-demo-model";
    private const string ModelId = $"ext:{ConnectionId}/{WireId}";
    private const string ResponsesWireId = "gateway-responses-model";
    private const string ResponsesModelId = $"ext:{ConnectionId}/{ResponsesWireId}";
    private const string Token = "demo-bearer-token";
    private const string PlainHeader = "X-Demo-Tenant";
    private const string PlainValue = "demo-tenant";
    private const string SecretHeader = "X-Demo-Gateway-Key";
    private const string SecretValue = "demo-gateway-secret";
    private const string LettersOnlyKey = "lettersonlykeyvalue";
    private const string LettersOnlyHeaderSecret = "lettersonlyheadersecret";

    [Test]
    public async Task AuthenticatedGateway_EveryCallPathCarriesTheBearerAndBothHeaders()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token,
            RequiredHeaders = new Dictionary<string, string>
            {
                [PlainHeader] = PlainValue,
                [SecretHeader] = SecretValue
            }
        }, Token, [Plain(PlainValue), Secret(SecretValue)]);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        var ct = deadline.Token;

        // The editor's round trip: the secret row comes back blank and the probe carries the stored value.
        var probe = await new ExternalProviderProbeService(rig.Store, NullLogger<ExternalProviderProbeService>.Instance)
            .ProbeAsync(new ExternalProviderProbeQuery(ConnectionId, BaseUrl: null, ApiKey: null, Headers: [Plain(PlainValue), Secret(value: null)]), ct);
        AssertEx.Equal(ExternalProviderProbeOutcome.Answered, probe.Outcome);
        AssertEx.Null(probe.Error, $"The probe was rejected: {probe.Error}");
        AssertEx.Contains(probe.Models, model => model.Id == WireId);

        var health = await new ExternalOpenAiModelProvider(rig.Registry, TimeProvider.System).CheckHealthAsync(ct);
        AssertEx.True(health.IsHealthy, "The health probe did not pass the gateway's checks.");

        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        var reply = await client.GetResponseAsync("hello", cancellationToken: ct);
        AssertEx.Equal("Hello from the fake gateway.", reply.Text);

        var streamed = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync("hello", cancellationToken: ct))
        {
            streamed.Add(update.Text);
        }

        AssertEx.Equal("Hello from the fake gateway.", string.Concat(streamed));

        rig.Gateway.State.SetScript(new FakeOpenAiGatewayScript
        {
            ToolCall = new FakeOpenAiGatewayToolCall
            {
                Name = "calculator",
                Arguments = """{"expression":"12*9"}"""
            }
        });
        var toolTurn = await client.GetResponseAsync("what is 12*9?", new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string expression) => expression, "calculator")]
        }, ct);
        var call = toolTurn.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
        AssertEx.Equal("calculator", call.Name);
        AssertEx.Equal("12*9", call.Arguments?["expression"]?.ToString());

        var requests = rig.Gateway.RecordedRequests;
        AssertEx.Equal("/v1/models,/v1/models,/v1/chat/completions,/v1/chat/completions,/v1/chat/completions",
            string.Join(',', requests.Select(request => request.Path)));
        AssertEx.Equal("False,True,False", string.Join(',', requests.Skip(2).Select(request => request.Stream)));
        AssertEx.Equal(expected: 1, requests[^1].ToolCount);
        foreach (var request in requests)
        {
            AssertEx.Equal("Bearer " + Token, request.Headers.GetValueOrDefault("Authorization"), $"{request.Path} lost the bearer.");
            AssertEx.Equal(PlainValue, request.Headers.GetValueOrDefault(PlainHeader), $"{request.Path} lost the plain header.");
            AssertEx.Equal(SecretValue, request.Headers.GetValueOrDefault(SecretHeader), $"{request.Path} lost the secret header.");
        }
    }

    [Test]
    public async Task KeylessGateway_SendsTheHeadersAndNoAuthorization()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredHeaders = new Dictionary<string, string>
            {
                [SecretHeader] = SecretValue
            }
        }, apiKey: null, [Plain(PlainValue), Secret(SecretValue)]);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);

        var reply = await client.GetResponseAsync("hello", cancellationToken: deadline.Token);

        AssertEx.Equal("Hello from the fake gateway.", reply.Text);
        var request = rig.Gateway.RecordedRequests.Single();
        AssertEx.False(request.Headers.ContainsKey("Authorization"), "A keyless connection must send no Authorization header at all.");
        AssertEx.Equal(PlainValue, request.Headers.GetValueOrDefault(PlainHeader));
        AssertEx.Equal(SecretValue, request.Headers.GetValueOrDefault(SecretHeader));
    }

    [Test]
    public async Task SecretHeaderRotation_TakesEffectOnTheNextSendThroughTheSameClient()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token
        }, Token, [Secret("demo-secret-v1")]);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);

        _ = await client.GetResponseAsync("before", cancellationToken: deadline.Token);
        var rotated = await rig.Administration.SaveConnectionAsync(Request(rig.Gateway, apiKey: null, [Secret("demo-secret-v2")]), deadline.Token);
        _ = await client.GetResponseAsync("after", cancellationToken: deadline.Token);

        AssertEx.True(rotated is ExternalProviderWriteResult.Committed, $"The rotation was not committed: {rotated}");
        var sent = rig.Gateway.RecordedRequests.Select(request => request.Headers.GetValueOrDefault(SecretHeader));
        AssertEx.Equal("demo-secret-v1,demo-secret-v2", string.Join(',', sent));

        // A blank ApiKey on the rotation save keeps the stored key, so the bearer survives the header edit.
        AssertEx.Equal("Bearer " + Token, rig.Gateway.RecordedRequests[^1].Headers.GetValueOrDefault("Authorization"));
    }

    [Test]
    public async Task ResponsesModel_BesideAChatCompletionsModel_RoutesPerSurfaceAndReplaysReasoningStatelessly()
    {
        IReadOnlyList<StoredExternalProviderHeader> headers = [Plain(PlainValue), Secret(SecretValue)];
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token,
            RequiredHeaders = new Dictionary<string, string>
            {
                [PlainHeader] = PlainValue,
                [SecretHeader] = SecretValue
            },
            Models =
            [
                new FakeOpenAiGatewayModel
                {
                    Id = WireId,
                    ContextLength = 32768
                },
                new FakeOpenAiGatewayModel
                {
                    Id = ResponsesWireId,
                    ContextLength = 32768
                }
            ]
        }, Token, headers, TwoModels(ExternalProviderApiSurface.Responses));
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        var ct = deadline.Token;
        using var chatClient = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        using var responsesClient = new ExternalOpenAiChatClient(rig.Registry, ResponsesModelId);

        AssertEx.Equal("Hello from the fake gateway.", (await chatClient.GetResponseAsync("hello", cancellationToken: ct)).Text);
        AssertEx.Equal("Hello from the fake gateway.", (await responsesClient.GetResponseAsync("hello", cancellationToken: ct)).Text);

        var streamed = new List<string>();
        await foreach (var update in responsesClient.GetStreamingResponseAsync("hello", cancellationToken: ct))
        {
            streamed.Add(update.Text);
        }

        AssertEx.Equal("Hello from the fake gateway.", string.Concat(streamed));

        // The tool loop is stateless: the second request must carry the reasoning item back, ahead of the tool result.
        rig.Gateway.State.SetScript(new FakeOpenAiGatewayScript
        {
            ToolCall = new FakeOpenAiGatewayToolCall
            {
                Name = "calculator",
                Arguments = """{"expression":"12*9"}"""
            }
        });
        using var toolLoop = new FunctionInvokingChatClient(responsesClient);
        var toolTurn = await toolLoop.GetResponseAsync("what is 12*9?", new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string expression) => expression == "12*9" ? "108" : "?", "calculator")]
        }, ct);
        AssertEx.Equal("Hello from the fake gateway.", toolTurn.Text);

        // Flipping the declared surface back must rebuild the cached endpoint, so the same client now speaks Chat Completions.
        var flipped = await rig.Administration.SaveConnectionAsync(Request(rig.Gateway, apiKey: null, headers, TwoModels(ExternalProviderApiSurface.ChatCompletions)), ct);
        AssertEx.True(flipped is ExternalProviderWriteResult.Committed, $"The surface flip was not committed: {flipped}");
        AssertEx.Equal("Hello from the fake gateway.", (await responsesClient.GetResponseAsync("hello", cancellationToken: ct)).Text);

        var requests = rig.Gateway.RecordedRequests;
        AssertEx.Equal("/v1/chat/completions,/v1/responses,/v1/responses,/v1/responses,/v1/responses,/v1/chat/completions",
            string.Join(',', requests.Select(request => request.Path)));
        AssertEx.Equal($"{WireId},{ResponsesWireId},{ResponsesWireId},{ResponsesWireId},{ResponsesWireId},{ResponsesWireId}",
            string.Join(',', requests.Select(request => request.Model)));
        AssertEx.Equal("False,False,True,False,False,False", string.Join(',', requests.Select(request => request.Stream)));
        foreach (var request in requests)
        {
            AssertEx.Equal("Bearer " + Token, request.Headers.GetValueOrDefault("Authorization"), $"{request.Path} lost the bearer.");
            AssertEx.Equal(PlainValue, request.Headers.GetValueOrDefault(PlainHeader), $"{request.Path} lost the plain header.");
            AssertEx.Equal(SecretValue, request.Headers.GetValueOrDefault(SecretHeader), $"{request.Path} lost the secret header.");
        }

        foreach (var request in requests.Where(request => request.Path == "/v1/responses"))
        {
            using var body = JsonDocument.Parse(AssertEx.NotNull(request.Body));
            AssertEx.Equal(JsonValueKind.False, body.RootElement.GetProperty("store").ValueKind, "every Responses send must be stateless.");
            AssertEx.Contains(body.RootElement.GetProperty("include").EnumerateArray().Select(item => item.GetString()), "reasoning.encrypted_content");
            AssertEx.False(body.RootElement.TryGetProperty("previous_response_id", out _), "a stateless send must not reference a stored response.");
        }

        using var replay = JsonDocument.Parse(AssertEx.NotNull(requests[4].Body));
        var input = replay.RootElement.GetProperty("input").EnumerateArray().ToList();
        var reasoningAt = input.FindIndex(item => item.GetProperty("type").GetString() == "reasoning"
                                                  && item.TryGetProperty("encrypted_content", out var blob)
                                                  && blob.GetString() == FakeOpenAiGatewayServer.EncryptedReasoning);
        var outputAt = input.FindIndex(item => item.GetProperty("type").GetString() == "function_call_output");
        AssertEx.True(reasoningAt >= 0, $"The tool turn did not replay the encrypted reasoning: {requests[4].Body}");
        AssertEx.True(outputAt > reasoningAt, $"The replayed reasoning must precede the tool result: {requests[4].Body}");
        // MEAI JSON-encodes a string tool result, so the wire carries "108" with its quotes.
        AssertEx.Contains(input[outputAt].GetProperty("output").GetString(), "108");
    }

    [Test]
    [Arguments(FakeOpenAiGatewayFailure.Unauthorized, FailureCategory.ProviderAuthFailed, null)]
    [Arguments(FakeOpenAiGatewayFailure.UnknownModel, FailureCategory.ModelUnavailable, null)]
    [Arguments(FakeOpenAiGatewayFailure.RateLimited, FailureCategory.ProviderRateLimited, "retry after 7")]
    [Arguments(FakeOpenAiGatewayFailure.TokenQuotaExceeded, FailureCategory.ProviderRateLimited, "retry after about 31 days. (token_quota_exceeded: project-quota)")]
    public async Task GatewayRejection_IsClassifiedForTheOperator(FakeOpenAiGatewayFailure failure, FailureCategory expected, string? fragment)
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token
        }, Token, [Secret(SecretValue)]);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        rig.Gateway.State.EnqueueFailure(failure);

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () => await client.GetResponseAsync("hello", cancellationToken: deadline.Token));
        var classification = InvocationFailureClassifier.MapFailure(thrown);

        AssertEx.Equal(expected, classification.Category);
        if (fragment is not null)
        {
            AssertEx.Contains(classification.Message, fragment);
        }

        AssertEx.False(classification.Message.Contains(Token, StringComparison.Ordinal) || classification.Message.Contains(SecretValue, StringComparison.Ordinal),
            "The operator-facing message must never carry a credential.");
    }

    // A policy gateway's 403 for a model the connection may not use names the model, not the key, on both API surfaces and
    // on the streaming send a chat turn makes, and the ids its body echoes stay out of the operator-facing message.
    [Test]
    [Arguments(ExternalProviderApiSurface.ChatCompletions, false)]
    [Arguments(ExternalProviderApiSurface.ChatCompletions, true)]
    [Arguments(ExternalProviderApiSurface.Responses, false)]
    [Arguments(ExternalProviderApiSurface.Responses, true)]
    public async Task GatewayModelNotAssigned_IsModelUnavailableWithTheReasonAndNoEchoedIds(ExternalProviderApiSurface surface, bool streaming)
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token,
            Models =
            [
                new FakeOpenAiGatewayModel
                {
                    Id = WireId,
                    ContextLength = 32768
                },
                new FakeOpenAiGatewayModel
                {
                    Id = ResponsesWireId,
                    ContextLength = 32768
                }
            ]
        }, Token, [Secret(SecretValue)], TwoModels(surface));
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ResponsesModelId);
        rig.Gateway.State.EnqueueFailure(FakeOpenAiGatewayFailure.ModelNotAssigned);

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () =>
        {
            if (streaming)
            {
                await client.GetStreamingResponseAsync("hello", cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token);
            }
            else
            {
                await client.GetResponseAsync("hello", cancellationToken: deadline.Token);
            }
        });
        var classification = InvocationFailureClassifier.MapFailure(thrown);

        AssertEx.True(rig.Gateway.RecordedRequests[^1].Stream == streaming, "The send did not take the requested streaming mode.");
        AssertEx.Equal(surface == ExternalProviderApiSurface.Responses ? "/v1/responses" : "/v1/chat/completions", rig.Gateway.RecordedRequests[^1].Path);
        AssertEx.Equal(FailureCategory.ModelUnavailable, classification.Category);
        AssertEx.Equal("The provider does not offer the selected model to this connection (Model is not assigned to this project.). Check the model id on the connection.",
            classification.Message);
        foreach (var echoed in (string[])[FakeOpenAiGatewayServer.ModelNotAssignedTenantId, FakeOpenAiGatewayServer.ModelNotAssignedProjectId, ResponsesWireId, Token])
        {
            AssertEx.False(classification.Message.Contains(echoed, StringComparison.Ordinal), $"The message surfaced '{echoed}': {classification.Message}");
        }
    }

    // The enterprise gateway's error shapes keep their body detail through the real SDK's streaming send on both API surfaces,
    // so each one reaches the operator with its own fix rather than "Retry" or "replace the key".
    [Test]
    [Arguments(FakeOpenAiGatewayFailure.NoRoute, ExternalProviderApiSurface.ChatCompletions, FailureCategory.ProviderUnreachable,
        "The gateway has no route for this request. Check the connection's base URL; it usually ends in /openai/v1. (apim_policy_error: Unable to match incoming request to an operation.)")]
    [Arguments(FakeOpenAiGatewayFailure.NoRoute, ExternalProviderApiSurface.Responses, FailureCategory.ProviderUnreachable,
        "The gateway has no route for this request. Check the connection's base URL; it usually ends in /openai/v1. (apim_policy_error: Unable to match incoming request to an operation.)")]
    [Arguments(FakeOpenAiGatewayFailure.ApiNotSupported, ExternalProviderApiSurface.ChatCompletions, FailureCategory.ModelUnavailable,
        "The provider does not offer the selected model on this API. Switch the model's API surface (Chat Completions or Responses) on the connection. (api_not_supported: Requested API is currently not supported)")]
    [Arguments(FakeOpenAiGatewayFailure.PermissionDenied, ExternalProviderApiSurface.Responses, FailureCategory.ModelUnavailable,
        "The gateway's backend is not permitted to use this model or operation. Contact the gateway admin. (PermissionDenied)")]
    [Arguments(FakeOpenAiGatewayFailure.InvalidToken, ExternalProviderApiSurface.ChatCompletions, FailureCategory.ProviderAuthFailed,
        "The provider rejected the token; it may have expired. Get a new token and replace it in the connection settings. (invalid_token: Access token is expired or invalid.)")]
    public async Task GatewayErrorShape_StreamingSend_IsClassifiedWithItsFix(FakeOpenAiGatewayFailure failure, ExternalProviderApiSurface surface,
        FailureCategory expectedCategory, string expectedMessage)
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token,
            Models =
            [
                new FakeOpenAiGatewayModel
                {
                    Id = WireId,
                    ContextLength = 32768
                },
                new FakeOpenAiGatewayModel
                {
                    Id = ResponsesWireId,
                    ContextLength = 32768
                }
            ]
        }, Token, [Secret(SecretValue)], TwoModels(surface));
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ResponsesModelId);
        rig.Gateway.State.EnqueueFailure(failure);

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () =>
            await client.GetStreamingResponseAsync("hello", cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token));
        var classification = InvocationFailureClassifier.MapFailure(thrown);

        AssertEx.Equal(surface == ExternalProviderApiSurface.Responses ? "/v1/responses" : "/v1/chat/completions", rig.Gateway.RecordedRequests[^1].Path);
        AssertEx.Equal(expectedCategory, classification.Category);
        AssertEx.Equal(expectedMessage, classification.Message);
    }

    // A required-header refusal whose header name contains "Model" is still a credential failure, not a model refusal.
    [Test]
    public async Task GatewayMissingModelNamedHeader_IsProviderAuthFailed()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token,
            RequiredHeaders = new Dictionary<string, string>
            {
                ["X-Model-Token"] = "model-token-value"
            }
        }, Token, []);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () => await client.GetResponseAsync("hello", cancellationToken: deadline.Token));
        var classification = InvocationFailureClassifier.MapFailure(thrown);

        AssertEx.Equal(FailureCategory.ProviderAuthFailed, classification.Category);
        AssertEx.Contains(classification.Message, "(missing_required_header: Missing or invalid required header 'X-Model-Token'.)");
    }

    // This gateway's real denial bodies (flat error code, message, optional reason) through the real SDK's streaming send: a working
    // key is never blamed for a project, membership, tenant or quota denial, and each code and reason reaches the operator.
    [Test]
    [Arguments(401, """{"error":"api-key-revoked","message":"The API key has been revoked."}""", FailureCategory.ProviderAuthFailed, true,
        "(api-key-revoked: The API key has been revoked.)")]
    [Arguments(401, """{"error":"api-key-expired","message":"The API key has expired."}""", FailureCategory.ProviderAuthFailed, true,
        "(api-key-expired: The API key has expired.)")]
    [Arguments(403, """{"error":"project-archived","message":"The project is archived."}""", FailureCategory.ProviderAuthFailed, false,
        "(project-archived: The project is archived.)")]
    [Arguments(403, """{"error":"project-not-started","message":"The project has not started yet."}""", FailureCategory.ProviderAuthFailed, false,
        "(project-not-started: The project has not started yet.)")]
    [Arguments(403, """{"error":"not-a-member","message":"The user is not a member of the project."}""", FailureCategory.ProviderAuthFailed, false,
        "(not-a-member: The user is not a member of the project.)")]
    [Arguments(403, """{"error":"user-disabled","message":"The user is disabled."}""", FailureCategory.ProviderAuthFailed, false,
        "(user-disabled: The user is disabled.)")]
    [Arguments(403, """{"error":"tenant-mismatch","message":"The token's tenant is not served by this gateway."}""", FailureCategory.ProviderAuthFailed, false,
        "(tenant-mismatch: The token's tenant is not served by this gateway.)")]
    [Arguments(403, """{"error":"token_quota_exceeded","message":"Token limit is exceeded. Try again in 3540 seconds.","reason":"project-quota"}""", FailureCategory.ProviderAuthFailed, false,
        "(token_quota_exceeded: Token limit is exceeded. Try again in 3540 seconds. (reason: project-quota))")]
    [Arguments(403, """{"error":"model-not-approved","message":"The model is not approved for the project."}""", FailureCategory.ModelUnavailable, false,
        "(model-not-approved: The model is not approved for the project.)")]
    [Arguments(403, """{"error":"disabled-project","message":"The model is currently not available for the project."}""", FailureCategory.ModelUnavailable, false,
        "(disabled-project: The model is currently not available for the project.)")]
    public async Task GatewayDenialBody_StreamingSend_NamesTheCauseAndBlamesTheKeyOnlyWhenItIs(int status, string body, FailureCategory expectedCategory, bool blamesTheKey, string detail)
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = Token
        }, Token, []);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        rig.Gateway.State.EnqueueRawResponse(status, body);

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () =>
            await client.GetStreamingResponseAsync("hello", cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token));
        var classification = InvocationFailureClassifier.MapFailure(thrown);

        AssertEx.Equal(expectedCategory, classification.Category);
        AssertEx.Contains(classification.Message, detail);
        AssertEx.Equal(blamesTheKey, classification.Message.Contains("Replace the API key", StringComparison.Ordinal));
    }

    // A credential of letters alone passes the word grammar, and a body the grammar refuses falls through to the SDK's own message, which quotes
    // the body raw. Neither may reach the operator or the exception chain a log prints; the status and its fixed text remain.
    [Test]
    [MatrixDataSource]
    public async Task GatewayEchoingACredential_ReachesNeitherTheOperatorNorTheChain([Matrix(400, 401, 403, 404, 429, 500)] int status,
        [Matrix("""{"error":{"message":"Invalid key %s"}}""", """{"message":"Invalid key %s"}""", """{"reason":"Invalid key %s"}""", """{"error":{"code":"%s"}}""", """{"error":"%s"}""",
            """{"error":{"message":"Key: sk/%s/x"}}""", """{"seen":["\u006c%s"]}""", "Invalid key %s")]
        string shape,
        [Matrix(LettersOnlyKey, LettersOnlyHeaderSecret)] string secret)
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            RequiredBearerToken = LettersOnlyKey
        }, LettersOnlyKey, [Secret(LettersOnlyHeaderSecret)]);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        // The JSON-escaped shape spells the secret's first letter, an l in both, as an escape.
        rig.Gateway.State.EnqueueRawResponse(status, shape.Replace("%s", shape.Contains("\\u", StringComparison.Ordinal) ? secret[1..] : secret, StringComparison.Ordinal));

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () => await client.GetResponseAsync("hello", cancellationToken: deadline.Token));
        var classification = InvocationFailureClassifier.MapFailure(thrown);

        AssertEx.False(classification.Message.Contains(secret, StringComparison.OrdinalIgnoreCase), $"The operator was shown the credential: {classification.Message}");
        AssertEx.False(thrown.ToString().Contains(secret, StringComparison.OrdinalIgnoreCase), $"The chain a log prints carried the credential: {thrown}");
        AssertEx.Equal(FailureCategoryOf(status), classification.Category);
    }

    // A streaming send, and the secret values of a rotation: the client is rebuilt with what it now sends.
    [Test]
    public async Task GatewayEchoingARotatedSecret_StreamingSend_IsStillDropped()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions(), apiKey: null, [Secret("firstsecretvalue")]);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        _ = await client.GetResponseAsync("before", cancellationToken: deadline.Token);

        var rotated = await rig.Administration.SaveConnectionAsync(Request(rig.Gateway, apiKey: null, [Secret("secondsecretvalue")]), deadline.Token);
        AssertEx.True(rotated is ExternalProviderWriteResult.Committed, $"The rotation was not committed: {rotated}");
        rig.Gateway.State.EnqueueRawResponse(401, """{"message":"Invalid key secondsecretvalue"}""");

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () =>
            await client.GetStreamingResponseAsync("after", cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token));

        AssertEx.False(thrown.ToString().Contains("secondsecretvalue", StringComparison.Ordinal), $"The chain carried the rotated secret: {thrown}");
        AssertEx.Equal(FailureCategory.ProviderAuthFailed, InvocationFailureClassifier.MapFailure(thrown).Category);
    }

    // A plain header is no credential: a reason quoting the project keeps its detail, and a key the body does not carry changes nothing.
    [Test]
    public async Task GatewayQuotingAPlainHeaderValue_KeepsTheDetail()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions(), LettersOnlyKey, [Plain("projectalpha"), Secret(LettersOnlyHeaderSecret)]);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        rig.Gateway.State.EnqueueRawResponse(403, """{"error":"project-archived","message":"Project projectalpha is archived."}""");

        var thrown = await AssertEx.ThrowsAsync<Exception>(async () => await client.GetResponseAsync("hello", cancellationToken: deadline.Token));

        AssertEx.Contains(InvocationFailureClassifier.MapFailure(thrown).Message, "(project-archived: Project projectalpha is archived.)");
    }

    // Test connection on the same body: the reason shows at probe time instead of after saving and sending.
    [Test]
    public async Task GatewayMissingProjectHeader_ProbeQuotesTheReason()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions(), apiKey: null, []);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        rig.Gateway.State.EnqueueRawResponse(400, """{"error":"missing_project_id","message":"Missing x-project-id header"}""");

        var probe = await new ExternalProviderProbeService(rig.Store, NullLogger<ExternalProviderProbeService>.Instance)
            .ProbeAsync(new ExternalProviderProbeQuery(ConnectionId, BaseUrl: null, ApiKey: null), deadline.Token);

        AssertEx.Contains(AssertEx.NotNull(probe.Error), "(missing_project_id: Missing x-project-id header)");
    }

    // What the runner's finish-reason rule stands on, through the real SDK adapters: a normal stream ends with a reason on both API
    // surfaces, a stream cut off cleanly ends without one, and a whole JSON body answering a stream reads as an empty stream.
    [Test]
    [Arguments(ExternalProviderApiSurface.ChatCompletions)]
    [Arguments(ExternalProviderApiSurface.Responses)]
    public async Task NormalStream_EndsWithAFinishReason(ExternalProviderApiSurface surface)
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions
        {
            Models =
            [
                new FakeOpenAiGatewayModel
                {
                    Id = WireId,
                    ContextLength = 32768
                },
                new FakeOpenAiGatewayModel
                {
                    Id = ResponsesWireId,
                    ContextLength = 32768
                }
            ]
        }, apiKey: null, [], TwoModels(surface));
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ResponsesModelId);

        var reply = await client.GetStreamingResponseAsync("hello", cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token);

        AssertEx.False(string.IsNullOrEmpty(reply.Text));
        AssertEx.True(reply.FinishReason is not null, "A normal stream ended without a finish reason.");
    }

    [Test]
    public async Task StreamCutOffCleanly_EndsWithTextAndNoFinishReason()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions(), apiKey: null, []);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        rig.Gateway.State.EnqueueFailure(FakeOpenAiGatewayFailure.PartialStream);

        var reply = await client.GetStreamingResponseAsync("hello", cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token);

        AssertEx.Equal("partial stream", reply.Text);
        AssertEx.True(reply.FinishReason is null, $"Unexpected finish reason {reply.FinishReason}.");
    }

    [Test]
    public async Task WholeJsonBodyAnsweringAStream_ReadsAsAnEmptyStream()
    {
        await using var rig = await Rig.StartAsync(new FakeOpenAiGatewayOptions(), apiKey: null, []);
        using var deadline = new CancellationTokenSource(TestBudgets.Contended);
        using var client = new ExternalOpenAiChatClient(rig.Registry, ModelId);
        rig.Gateway.State.EnqueueRawResponse(200,
            """{"id":"chatcmpl-1","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"whole"},"finish_reason":"stop"}]}""");

        var reply = await client.GetStreamingResponseAsync("hello", cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token);

        AssertEx.Equal(string.Empty, reply.Text);
        AssertEx.True(reply.FinishReason is null, $"Unexpected finish reason {reply.FinishReason}.");
    }

    private static FailureCategory FailureCategoryOf(int status)
    {
        return status switch
        {
            400 => FailureCategory.Unexpected,
            401 or 403 => FailureCategory.ProviderAuthFailed,
            404 => FailureCategory.ModelUnavailable,
            429 => FailureCategory.ProviderRateLimited,
            _ => FailureCategory.ProviderUnreachable
        };
    }

    private static StoredExternalProviderHeader Plain(string value)
    {
        return new StoredExternalProviderHeader
        {
            Name = PlainHeader,
            Value = value
        };
    }

    private static StoredExternalProviderHeader Secret(string? value)
    {
        return new StoredExternalProviderHeader
        {
            Name = SecretHeader,
            Value = value,
            IsSecret = true
        };
    }

    private static IReadOnlyList<ExternalProviderModelSaveRequest> TwoModels(ExternalProviderApiSurface responsesModelSurface)
    {
        return
        [
            new ExternalProviderModelSaveRequest
            {
                WireId = WireId,
                SupportsTools = true,
                ContextLength = 32768
            },
            new ExternalProviderModelSaveRequest
            {
                WireId = ResponsesWireId,
                SupportsTools = true,
                SupportsReasoning = true,
                ContextLength = 32768,
                ApiSurface = responsesModelSurface
            }
        ];
    }

    private static ExternalProviderConnectionSaveRequest Request(FakeOpenAiGatewayServer gateway,
        string? apiKey,
        IReadOnlyList<StoredExternalProviderHeader> headers,
        IReadOnlyList<ExternalProviderModelSaveRequest>? models = null)
    {
        return new ExternalProviderConnectionSaveRequest
        {
            Id = ConnectionId,
            DisplayName = "Demo gateway",
            BaseUrl = gateway.BaseAddress.AbsoluteUri,
            Locality = ExternalProviderLocality.Cloud,
            ApiKey = apiKey,
            Models = models
                     ??
                     [
                         new ExternalProviderModelSaveRequest
                         {
                             WireId = WireId,
                             SupportsTools = true,
                             ContextLength = 32768
                         }
                     ],
            Headers = headers
        };
    }

    /// <summary>The fake gateway plus the node's real store, registry and administration service over a temp data directory.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDirectory _dataDirectory;

        private Rig(FakeOpenAiGatewayServer gateway, TempDirectory dataDirectory)
        {
            Gateway = gateway;
            _dataDirectory = dataDirectory;
            Store = new ExternalProviderStore(new EphemeralDataProtectionProvider(),
                new FakeNodeDataDirectory(dataDirectory.Path),
                NullLogger<ExternalProviderStore>.Instance);
            Registry = new ExternalProviderRegistry(Store);
            Administration = new ExternalProviderAdministrationService(Store,
                Registry,
                Substitute.For<IExternalProviderReconciler>(),
                Substitute.For<ILocalChatClientCacheInvalidator>(),
                NullLogger<ExternalProviderAdministrationService>.Instance);
        }

        public FakeOpenAiGatewayServer Gateway { get; }

        public ExternalProviderStore Store { get; }

        public ExternalProviderRegistry Registry { get; }

        public ExternalProviderAdministrationService Administration { get; }

        public static async Task<Rig> StartAsync(FakeOpenAiGatewayOptions options,
            string? apiKey,
            IReadOnlyList<StoredExternalProviderHeader> headers,
            IReadOnlyList<ExternalProviderModelSaveRequest>? models = null)
        {
            var gateway = await FakeOpenAiGatewayServer.StartAsync(options);
            var rig = new Rig(gateway, new TempDirectory("xe-gateway"));
            try
            {
                var saved = await rig.Administration.SaveConnectionAsync(Request(gateway, apiKey, headers, models));
                AssertEx.True(saved is ExternalProviderWriteResult.Committed, $"The connection was not saved: {saved}");
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Gateway.DisposeAsync();
            Store.Dispose();
            _dataDirectory.Dispose();
        }
    }
}
