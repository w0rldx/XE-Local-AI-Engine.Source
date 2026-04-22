namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalChatInvocationServiceTests
{
    [Test]
    public async Task GetSnapshotAsync_ReturnsDefaultStateFromOptions()
    {
        using var service = CreateSut(out _, out _);

        var snapshot = await service.GetSnapshotAsync();

        AssertEx.Equal("qwen3.5:0.8b", snapshot.SelectedModel);
        AssertEx.Equal(1, snapshot.AgentDefinitionVersion);
        AssertEx.True(snapshot.ToolsEnabled);
        AssertEx.NotEqual(Guid.Empty, snapshot.ConversationId);
    }

    [Test]
    public async Task SetModelAsync_UpdatesSelectedModelWithoutResettingConversation()
    {
        using var service = CreateSut(out _, out _);
        var originalConversationId = service.ConversationId;

        await service.SetModelAsync("llama3.2:3b");
        var snapshot = await service.GetSnapshotAsync();

        AssertEx.Equal("llama3.2:3b", service.SelectedModel);
        AssertEx.Equal("llama3.2:3b", snapshot.SelectedModel);
        AssertEx.Equal(originalConversationId, snapshot.ConversationId);
    }

    [Test]
    public async Task ResetConversationAsync_RegeneratesConversationIdAndPreservesModel()
    {
        using var service = CreateSut(out _, out _);
        var originalConversationId = service.ConversationId;

        await service.SetModelAsync("llama3.2:3b");
        await service.ResetConversationAsync();
        var snapshot = await service.GetSnapshotAsync();

        AssertEx.NotEqual(originalConversationId, snapshot.ConversationId);
        AssertEx.Equal("llama3.2:3b", snapshot.SelectedModel);
    }

    [Test]
    public async Task SendMessageAsync_BuildsLoopbackRuntimePackageAndAssignsDispatcher()
    {
        using var service = CreateSut(out var invocationRunner, out var eventDispatcher);
        InvocationExecutionContext? capturedContext = null;
        invocationRunner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            capturedContext = callInfo.Arg<InvocationExecutionContext>();
                            return Task.CompletedTask;
                        });

        var invocationId = await service.SendMessageAsync("hello");

        var context = AssertEx.NotNull(capturedContext);
        AssertEx.Equal(invocationId, context.Package.InvocationId);
        AssertEx.Equal(service.ConversationId, context.Package.ConversationId);
        AssertEx.Equal(service.SelectedModel, context.Package.ModelProfile);
        AssertEx.Contains(AssertEx.NotNull(context.Package.RequestedCapabilities), LocalChatLoopbackDefaults.RequestedCapability);
        AssertEx.Equal(MessageRole.User, context.Package.ConversationContext[0].Role);
        AssertEx.Equal("hello", context.Package.ConversationContext[0].Content);
        await eventDispatcher.Received(1).ReportInvocationAssignedAsync(Arg.Is<RuntimePackage>(package => package.InvocationId == invocationId));
    }

    [Test]
    public async Task SendMessageAsync_WhenDispatcherCompletes_AppendsAssistantMessageToNextInvocationContext()
    {
        using var service = CreateSut(out var invocationRunner, out var eventDispatcher);
        InvocationExecutionContext? firstContext = null;
        InvocationExecutionContext? secondContext = null;
        var callCount = 0;

        invocationRunner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            callCount++;
                            var context = callInfo.Arg<InvocationExecutionContext>();

                            if (callCount == 1)
                            {
                                firstContext = context;
                                eventDispatcher.CurrentInvocation.Returns(new InvocationState
                                {
                                    InvocationId = context.Package.InvocationId,
                                    ConversationId = context.Package.ConversationId,
                                    Status = InvocationStatus.Completed,
                                    StreamedContent = "Hi there",
                                    StartedAt = DateTimeOffset.UtcNow,
                                    LastUpdatedAt = DateTimeOffset.UtcNow,
                                    ModelUsed = context.Package.ModelProfile
                                });
                            }
                            else
                            {
                                secondContext = context;
                            }

                            return Task.CompletedTask;
                        });

        await service.SendMessageAsync("hello");
        await service.SendMessageAsync("how are you?");

        AssertEx.NotNull(firstContext);
        var followUpContext = AssertEx.NotNull(secondContext);
        AssertEx.Equal(3, followUpContext.Package.ConversationContext.Count);
        AssertEx.Equal("hello", followUpContext.Package.ConversationContext[0].Content);
        AssertEx.Equal(MessageRole.Assistant, followUpContext.Package.ConversationContext[1].Role);
        AssertEx.Equal("Hi there", followUpContext.Package.ConversationContext[1].Content);
        AssertEx.Equal("how are you?", followUpContext.Package.ConversationContext[2].Content);
    }

    private static LocalChatInvocationService CreateSut(out IInvocationRunner invocationRunner, out IWorkerEventDispatcher eventDispatcher)
    {
        invocationRunner = Substitute.For<IInvocationRunner>();
        eventDispatcher = Substitute.For<IWorkerEventDispatcher>();

        return new LocalChatInvocationService(Options.Create(new LocalChatAgentOptions
            {
                AgentName = "XeLocalAgent",
                DefaultModel = "qwen3.5:0.8b",
                InstructionsResource = "XE_Local_AI_Engine.AI.Agent.Instructions.LocalChatDefault.txt",
                EnableTools = true
            }),
            new LocalChatRuntimePackageBuilder(),
            invocationRunner,
            eventDispatcher);
    }
}
