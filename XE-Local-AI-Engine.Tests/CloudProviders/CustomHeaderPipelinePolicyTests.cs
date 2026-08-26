namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.ClientModel.Primitives;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the outbound custom-header pipeline policy: every resolved header is set on the request, and a reserved
///     name is defensively skipped case-insensitively even if it slipped past save-time validation.
/// </summary>
public sealed class CustomHeaderPipelinePolicyTests
{
    [Test]
    public void Process_WhenHeadersSet_AppendsAllToRequest()
    {
        var policy = new CustomHeaderPipelinePolicy([new ResolvedCustomHeader("X-Alpha", "one"), new ResolvedCustomHeader("X-Beta", "two")]);
        using var message = CreateMessage();
        var pipeline = new PipelinePolicy[]
        {
            policy,
            new TerminalPolicy()
        };

        policy.Process(message, pipeline, currentIndex: 0);

        AssertEx.True(message.Request.Headers.TryGetValue("X-Alpha", out var alpha) && alpha == "one");
        AssertEx.True(message.Request.Headers.TryGetValue("X-Beta", out var beta) && beta == "two");
    }

    [Test]
    public void Process_WhenReservedName_SkipsIt()
    {
        // "authorization" is reserved (case-insensitive) and must never be overridden by an operator header.
        var policy = new CustomHeaderPipelinePolicy([new ResolvedCustomHeader("authorization", "attacker"), new ResolvedCustomHeader("X-Ok", "ok")]);
        using var message = CreateMessage();
        var pipeline = new PipelinePolicy[]
        {
            policy,
            new TerminalPolicy()
        };

        policy.Process(message, pipeline, currentIndex: 0);

        AssertEx.False(message.Request.Headers.TryGetValue("authorization", out _));
        AssertEx.True(message.Request.Headers.TryGetValue("X-Ok", out var ok) && ok == "ok");
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
}
