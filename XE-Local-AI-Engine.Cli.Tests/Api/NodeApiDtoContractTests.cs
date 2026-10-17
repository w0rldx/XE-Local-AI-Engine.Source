namespace XE_Local_AI_Engine.Cli.Tests.Api;

using System.Reflection;
using System.Text.Json;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Pins every hand-declared REST DTO member to a property of its schema in the committed OpenAPI document.</summary>
/// <remarks><c>auth/vault/unlock</c> belongs to the locked pre-host and is not in the document, so it has no row.</remarks>
[Category(TestCategories.Unit)]
public sealed class NodeApiDtoContractTests
{
    private const string Chat = "XE_Local_AI_EngineClientEndpointsLocalChatV1";
    private const string Models = "XE_Local_AI_EngineClientEndpointsLocalModelsV1";
    private const string AuthSchemas = "XE_Local_AI_EngineClientEndpointsAuthV1";

    [Test]
    [Arguments(typeof(ConversationList), Chat + "ListNodeChatConversationsResponse")]
    [Arguments(typeof(ConversationSummary), Chat + "NodeChatConversationSummaryResponse")]
    [Arguments(typeof(CreateConversationRequest), Chat + "CreateNodeChatConversationRequest")]
    [Arguments(typeof(Conversation), Chat + "NodeChatConversationResponse")]
    [Arguments(typeof(ChatMessage), Chat + "NodeChatMessageResponse")]
    [Arguments(typeof(CancelMessageRequest), Chat + "CancelNodeChatMessageRequest")]
    [Arguments(typeof(CancelMessageResponse), Chat + "NodeChatCancelMessageResponse")]
    [Arguments(typeof(ResolveApprovalRequest), Chat + "ResolveToolApprovalRequest")]
    [Arguments(typeof(ResolveQuestionRequest), Chat + "ResolveUserQuestionRequest")]
    [Arguments(typeof(QuestionAnswer), Chat + "ResolveUserQuestionAnswerDto")]
    [Arguments(typeof(LocalModelList), Models + "ListLocalModelsResponse")]
    [Arguments(typeof(LocalModel), Models + "LocalModelResponse")]
    [Arguments(typeof(RunningModelList), Models + "RunningLocalModelsResponse")]
    [Arguments(typeof(RunningModel), Models + "RunningLocalModelResponse")]
    [Arguments(typeof(AgentDefinitionList), "XE_Local_AI_EngineClientEndpointsAgentsV1ListAgentDefinitionsResponse")]
    [Arguments(typeof(AgentDefinition), "XE_Local_AI_EngineClientEndpointsAgentsV1AgentDefinitionResponse")]
    [Arguments(typeof(NodeInfo), "XE_Local_AI_EngineClientEndpointsDiagnosticsV1NodeInfoResponse")]
    [Arguments(typeof(NodeAuthStatus), AuthSchemas + "NodeAuthStatusResponse")]
    [Arguments(typeof(LoginRequest), AuthSchemas + "NodeLoginRequest")]
    [Arguments(typeof(TokenPair), AuthSchemas + "NodeAccessTokenResponse")]
    public async Task EveryMember_IsAPropertyOfItsOpenApiSchema(Type dto, string schemaName)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(OpenApiPath()));
        AssertEx.True(document.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty(schemaName, out var schema), $"schema {schemaName}");
        var properties = schema.GetProperty("properties").EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);

        var members = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(static property => !(property.DeclaringType == typeof(TokenPair) && property.Name == nameof(TokenPair.Origin)))
                         .Select(static property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
                         .ToList();

        AssertEx.NotEmpty(members);
        foreach (var member in members)
        {
            AssertEx.True(properties.Contains(member), $"{dto.Name}.{member} is not a property of {schemaName}.");
        }
    }

    [Test]
    public async Task TheClientPaths_ExistInTheOpenApiDocument()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(OpenApiPath()));
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
                 {
                     "auth/status", "auth/login", "auth/refresh", "chat/conversations", "chat/conversations/{conversationId}", "chat/cancel",
                     "chat/approvals/resolve", "chat/questions/resolve", "models", "models/running", "agents", "diagnostics/node-info"
                 })
        {
            AssertEx.True(paths.TryGetProperty("/" + NodeHttp.ApiPrefix + path, out _), path);
        }
    }

    private static string OpenApiPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XE-Local-AI-Engine.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(AssertEx.NotNull(directory, "repository root").FullName, "XE-Local-AI-Engine.Client.React", "openapi", "v1.json");
    }
}
