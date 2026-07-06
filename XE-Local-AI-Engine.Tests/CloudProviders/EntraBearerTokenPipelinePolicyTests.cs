namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.ClientModel.Primitives;
using Azure.Core;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the outbound Entra ID bearer-token pipeline policy: a fresh token is fetched per call and set as
///     <c>Authorization: Bearer &lt;token&gt;</c>, the requested scope is propagated to the credential, and it
///     composes with <see cref="CustomHeaderPipelinePolicy" /> without either policy clobbering the other's header.
/// </summary>
public sealed class EntraBearerTokenPipelinePolicyTests
{
    [Test]
    public void Process_WhenCalled_SetsBearerAuthorizationHeader()
    {
        var credential = new RecordingTokenCredential("token-value");
        var policy = new EntraBearerTokenPipelinePolicy(credential, "api://backend/.default");
        using var message = CreateMessage();
        var pipeline = new PipelinePolicy[]
        {
            policy,
            new TerminalPolicy()
        };

        policy.Process(message, pipeline, currentIndex: 0);

        AssertEx.True(message.Request.Headers.TryGetValue("Authorization", out var authorization));
        AssertEx.Equal("Bearer token-value", authorization);
    }

    [Test]
    public async Task ProcessAsync_WhenCalled_SetsBearerAuthorizationHeader()
    {
        var credential = new RecordingTokenCredential("async-token-value");
        var policy = new EntraBearerTokenPipelinePolicy(credential, "api://backend/.default");
        using var message = CreateMessage();
        var pipeline = new PipelinePolicy[]
        {
            policy,
            new TerminalPolicy()
        };

        await policy.ProcessAsync(message, pipeline, currentIndex: 0);

        AssertEx.True(message.Request.Headers.TryGetValue("Authorization", out var authorization));
        AssertEx.Equal("Bearer async-token-value", authorization);
    }

    [Test]
    public void Process_WhenCalled_PropagatesConfiguredScopeToCredential()
    {
        var credential = new RecordingTokenCredential("token-value");
        var policy = new EntraBearerTokenPipelinePolicy(credential, "api://backend/.default");
        using var message = CreateMessage();
        var pipeline = new PipelinePolicy[]
        {
            policy,
            new TerminalPolicy()
        };

        policy.Process(message, pipeline, currentIndex: 0);

        AssertEx.True(credential.LastRequestContext.HasValue);
        AssertEx.Equal(1, credential.LastRequestContext!.Value.Scopes.Length);
        AssertEx.Equal("api://backend/.default", credential.LastRequestContext.Value.Scopes[0]);
    }

    [Test]
    public void Process_WhenCalledTwice_FetchesAFreshTokenEachTime()
    {
        var credential = new RecordingTokenCredential("token-value");
        var policy = new EntraBearerTokenPipelinePolicy(credential, "api://backend/.default");
        var pipeline = new PipelinePolicy[]
        {
            policy,
            new TerminalPolicy()
        };

        using (var first = CreateMessage())
        {
            policy.Process(first, pipeline, currentIndex: 0);
        }

        using (var second = CreateMessage())
        {
            policy.Process(second, pipeline, currentIndex: 0);
        }

        AssertEx.Equal(2, credential.CallCount);
    }

    [Test]
    public void Process_ComposesWithCustomHeaderPolicy_WithoutClobberingEitherHeader()
    {
        var credential = new RecordingTokenCredential("token-value");
        var bearerPolicy = new EntraBearerTokenPipelinePolicy(credential, "api://backend/.default");
        var headerPolicy = new CustomHeaderPipelinePolicy([("X-Tenant", "tenant-a")]);
        using var message = CreateMessage();
        var pipeline = new PipelinePolicy[]
        {
            headerPolicy,
            bearerPolicy,
            new TerminalPolicy()
        };

        headerPolicy.Process(message, pipeline, currentIndex: 0);

        AssertEx.True(message.Request.Headers.TryGetValue("Authorization", out var authorization) && authorization == "Bearer token-value");
        AssertEx.True(message.Request.Headers.TryGetValue("X-Tenant", out var tenant) && tenant == "tenant-a");
    }

    private static PipelineMessage CreateMessage()
    {
        var pipeline = ClientPipeline.Create(new ClientPipelineOptions());
        var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri = new Uri("https://example.openai.azure.com/");
        return message;
    }

    private sealed class TerminalPolicy : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            // Terminal: does not delegate further.
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTokenCredential(string tokenValue) : TokenCredential
    {
        public int CallCount { get; private set; }

        public TokenRequestContext? LastRequestContext { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestContext = requestContext;
            return new AccessToken(tokenValue, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }
}
