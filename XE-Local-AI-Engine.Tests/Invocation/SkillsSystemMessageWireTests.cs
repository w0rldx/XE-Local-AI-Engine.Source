namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>An agent with a skill must reach llama-server with exactly one, leading system message.</summary>
/// <remarks>
///     The skills provider's listing (sent by the OpenAI adapter from <c>ChatOptions.Instructions</c>) plus the seed
///     system prompt make two, and Qwen3.5's template raises "System message must be at the beginning" on the second.
///     Driven through the real agent, skills provider, production chat pipeline and llama-server wire adapter.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class SkillsSystemMessageWireTests
{
    private const string Persona = "You are the r4 persona.";
    private const string SkillBody = "Skill body.";

    [Test]
    public async Task SkillAgent_LlamaServerWireBody_CarriesExactlyOneSystemMessage_First()
    {
        using var capture = new CapturingChatClient();
        await using var services = BuildPipeline(capture);
        var agent = InvocationAgentFactory.BuildApprovalValidatedAgent(services.GetRequiredService<IChatClient>(),
            "skills-wire",
            "One system message on the skills path.",
            [],
            NullLoggerFactory.Instance,
            services,
            skillsProvider: InvocationSkillsProvider.Create([
                new InvocationSkill
                {
                    Name = "demo",
                    Description = "A demo skill.",
                    Body = SkillBody
                }
            ]));

        _ = await agent.RunAsync([new ChatMessage(ChatRole.System, Persona), new ChatMessage(ChatRole.User, "Say hi.")]);

        var body = await LlamaGrammarToolOffer.CaptureLlamaServerWireBodyAsync(AssertEx.NotNull(capture.Messages), capture.Options);
        using var document = JsonDocument.Parse(body);
        var roles = document.RootElement.GetProperty("messages").EnumerateArray().Select(static message => message.GetProperty("role").GetString()).ToList();
        AssertEx.Equal(expected: 1, roles.Count(static role => role == "system"), $"the wire must carry one system message, got roles [{string.Join(", ", roles)}]");
        AssertEx.Equal("system", roles[0]);

        var system = document.RootElement.GetProperty("messages")[0].GetProperty("content").ToString();
        AssertEx.Contains(system, Persona, message: "the persona must survive the fold");
        AssertEx.Contains(system, "demo", message: "the skill listing must survive the fold");
    }

    private static ServiceProvider BuildPipeline(IChatClient inner)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _ = services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _ = services.AddSingleton(inner);
        _ = services.DecorateChatClientPipeline();
        return services.BuildServiceProvider();
    }

    // The provider: records what the pipeline hands it and answers.
    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? Messages { get; private set; }

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = [.. messages];
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}
