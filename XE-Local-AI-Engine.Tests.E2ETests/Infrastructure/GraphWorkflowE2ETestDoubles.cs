namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Admission for a host that has no weights to size. Every decision is an <see cref="CapacityVerdict.Allow" /> with
///     no reservation, which is exactly what the real gate returns for a model that costs this node nothing.
///     <para>
///         The graph-workflow Agent lane consults <see cref="ICapacityService" /> before it ever reaches a provider, and
///         the real gate resolves a model's footprint from an installed GGUF. The E2E fixture has none — FakeOllama
///         serves every model over HTTP — so the real gate reads the footprint as unknown and refuses the turn before a
///         single request is made. The unit-side <c>GraphWorkflowAgentHostFixture</c> replaces this seam for the same
///         reason; the browser suite needs only this one, because every other collaborator on that path is real here.
///     </para>
///     <para>
///         A null reservation is deliberate and not a shortcut: the lane disposes whatever it is handed on every
///         terminal path, and handing it nothing means a leak here could not hide a leak there. What capacity actually
///         decides — byte budgets, process caps, same-model queueing — is owned by the unit tests over the real service.
///     </para>
/// </summary>
internal sealed class E2EAlwaysAdmitCapacityService : ICapacityService
{
    public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct) =>
        Task.FromResult(new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false));
}
