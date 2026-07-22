namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Exclusive lease that prevents llama-server starts while a managed runtime is being swapped.</summary>
public interface ILlamaServerRuntimeMutationLease : IAsyncDisposable;
