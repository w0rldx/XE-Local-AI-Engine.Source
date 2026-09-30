namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     A <see cref="DelegatingAIFunction" /> that swaps ONLY the model-visible <see cref="AIFunctionDeclaration.JsonSchema" /> for
///     its llama.cpp-compilable form, forwarding name, description, additional properties and invocation to the inner
///     function.
/// </summary>
/// <remarks>
///     It exists solely so the OpenAI adapter serialises a compilable <c>tools</c> array, and is created inside
///     <c>DeferredLlamaServerChatClient</c> on a CLONE of the caller's <see cref="ChatOptions" />, so it is never
///     visible to the layers that resolve and execute tools: the function-invocation middleware, its approval
///     detection and <c>ApprovalRequiredAIFunction</c>'s outermost-type contract all operate on the caller's own
///     untouched tool list. This wrapper is therefore never invoked and never validates an argument.
/// </remarks>
internal sealed class GrammarSafeSchemaAIFunction : DelegatingAIFunction
{
    private readonly JsonElement _jsonSchema;

    internal GrammarSafeSchemaAIFunction(AIFunction innerFunction, JsonElement jsonSchema)
        : base(innerFunction)
    {
        _jsonSchema = jsonSchema;
    }

    public override JsonElement JsonSchema => _jsonSchema;
}
