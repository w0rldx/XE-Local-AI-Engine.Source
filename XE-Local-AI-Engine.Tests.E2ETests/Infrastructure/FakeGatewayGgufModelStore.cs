namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The browser host's installed-GGUF list, read from the fake gateway's model list, because the model catalog, the
///     capability resolver and the default-model policy read <see cref="IGgufModelStore" /> rather than a provider.
/// </summary>
/// <remarks>
///     Accepted ceiling: there are no files, so a footprint is unknown, a path is null and a download is refused. A row's
///     removal is not faked either: deletion runs on the on-disk registry, which never holds these names.
/// </remarks>
internal sealed class FakeGatewayGgufModelStore : IGgufModelStore
{
    private readonly FakeOpenAiGatewayState _state;

    public FakeGatewayGgufModelStore(FakeOpenAiGatewayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<string?> ResolveProjectorFilePathAsync(string modelName, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct) =>
        Task.FromResult(FakeGatewayLocalModelProvider.Describe(_state));

    public Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct) =>
        throw new NotSupportedException("The browser host downloads nothing.");

    public Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException("The browser host downloads nothing.");

    public Task DeleteModelAsync(string modelName, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<bool> ExistsAsync(string modelName, CancellationToken ct) =>
        Task.FromResult(_state.Models.Any(model => string.Equals(model.Id, modelName, StringComparison.OrdinalIgnoreCase)));

    public Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string modelName, CancellationToken ct) =>
        Task.FromResult<GgufModelFootprintFacts?>(null);
}
