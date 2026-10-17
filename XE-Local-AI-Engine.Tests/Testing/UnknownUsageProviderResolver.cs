namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>A usage-provider resolver that attributes nothing, for persistence tests that never read an envelope's provider.</summary>
internal sealed class UnknownUsageProviderResolver : IUsageProviderResolver
{
    public static Lazy<IUsageProviderResolver> Instance { get; } = new(new UnknownUsageProviderResolver());

    public Task<string> ResolveAsync(string? modelName, CancellationToken cancellationToken = default) =>
        Task.FromResult(AgentUsageProviders.Unknown);
}
