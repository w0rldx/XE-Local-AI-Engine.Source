namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

internal sealed class InvocationGenerationRejectedException(string sanitizedReason) : InvalidOperationException(sanitizedReason);
