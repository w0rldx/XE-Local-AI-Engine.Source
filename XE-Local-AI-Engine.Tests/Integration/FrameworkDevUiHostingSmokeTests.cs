#if DEBUG
namespace XE_Local_AI_Engine.Tests.Integration;

using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class FrameworkDevUiHostingSmokeTests
{
    [Test]
    public async Task DebugDevelopmentHost_MapsResponsesConversationsAndDevUi()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Services.AddSingleton(Substitute.For<IChatClient>());
        builder.Services.AddSingleton<IAgentInstructionProvider>(new FixedInstructionProvider());
        builder.AddLocalAiAgentDevUi();
        builder.AddOpenAIResponses();
        builder.AddOpenAIConversations();
        builder.AddDevUI();

        await using var app = builder.Build();
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();

        var routes = ((IEndpointRouteBuilder)app).DataSources
                                                .SelectMany(static source => source.Endpoints)
                                                .OfType<RouteEndpoint>()
                                                .Select(static endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
                                                .ToArray();

        AssertEx.True(routes.Any(static route => route.Contains("responses", StringComparison.OrdinalIgnoreCase)),
            "The OpenAI Responses endpoint must be mapped before DevUI.");
        AssertEx.True(routes.Any(static route => route.Contains("conversations", StringComparison.OrdinalIgnoreCase)),
            "The OpenAI Conversations endpoint must be mapped before DevUI.");
        AssertEx.True(routes.Any(static route => route.Contains("devui", StringComparison.OrdinalIgnoreCase)),
            "The development-only DevUI endpoint must be mapped.");
    }

    private sealed class FixedInstructionProvider : IAgentInstructionProvider
    {
        public string GetLocalChatInstructions() => "DevUI smoke instructions.";

        public string GetBaseScaffold() => string.Empty;

        public string GetDefaultChatSystemPrompt() => "DevUI smoke instructions.";
    }
}
#endif
