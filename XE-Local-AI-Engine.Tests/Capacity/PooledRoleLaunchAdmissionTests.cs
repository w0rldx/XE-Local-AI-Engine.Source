namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="PooledRoleLaunchAdmission" /> tests: Allow hands over the reservation, QueueSameModel holds nothing, a
///     rejection throws its reason, only the embedder skips budget refusal, and each call uses its own disposed scope.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class PooledRoleLaunchAdmissionTests
{
    private const string Model = "gpustack/bge-reranker-v2-m3-GGUF:Q4_K_M";

    [Test]
    public async Task Allow_HandsTheReservationToTheCaller_AndOutlivesTheScope()
    {
        var reservation = new TrackingDisposable();
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.Allow,
            Reason = "Capacity available.",
            OllamaEvictionWarning = false,
            Reservation = reservation
        });

        var returned = await host.Admission.AdmitAsync(Model, ModelRole.Reranker, CancellationToken.None);

        AssertEx.True(ReferenceEquals(reservation, returned), "Allow must hand over the exact reservation the decision carried.");
        AssertEx.Equal(1, host.Created.Count);
        AssertEx.True(host.Created[0].Disposed, "The per-call scope must be disposed when the decision returns.");
        AssertEx.False(reservation.Disposed, "Disposing the scope must not release the reservation the supervisor now owns.");
    }

    [Test]
    public async Task QueueSameModel_HoldsNothing()
    {
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.QueueSameModel,
            Reason = "Model already running; the spawn will share that process.",
            OllamaEvictionWarning = false
        });

        var returned = await host.Admission.AdmitAsync(Model, ModelRole.Reranker, CancellationToken.None);

        AssertEx.Null(returned);
        AssertEx.Equal(1, host.Created.Count);
    }

    [Test]
    public async Task Reject_ThrowsTheSanitizedReason()
    {
        const string Reason = "Insufficient capacity for 'm' (Reranker): not enough free memory for another model. No model is loaded.";
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.RejectInsufficient,
            Reason = Reason,
            OllamaEvictionWarning = false
        });

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => host.Admission.AdmitAsync(Model, ModelRole.Reranker, CancellationToken.None));

        AssertEx.Equal(Reason, exception.Message);
    }

    [Test]
    [Arguments(ModelRole.Embedding, true)]
    [Arguments(ModelRole.Reranker, false)]
    public async Task OnlyTheEmbedder_IsNeverRejectedOnBudget(ModelRole role, bool expected)
    {
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.QueueSameModel,
            Reason = "Model already running; the spawn will share that process.",
            OllamaEvictionWarning = false
        });

        await host.Admission.AdmitAsync(Model, role, CancellationToken.None);

        var request = AssertEx.NotNull(host.Created.Single().Request);
        AssertEx.Equal(Model, request.ModelName);
        AssertEx.Equal(role, request.Role);
        AssertEx.Equal(expected, request.NeverRejectOnBudget);
    }

    [Test]
    public async Task ChatRole_IsRefusedBeforeAnyDecision()
    {
        await using var host = Host.Create(new CapacityDecision
        {
            Verdict = CapacityVerdict.Allow,
            Reason = "Capacity available.",
            OllamaEvictionWarning = false
        });

        await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() => host.Admission.AdmitAsync(Model, ModelRole.Chat, CancellationToken.None));

        AssertEx.Empty(host.Created);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Host(ServiceProvider provider, List<RecordingCapacityService> created)
        {
            _provider = provider;
            Created = created;
            Admission = new PooledRoleLaunchAdmission(provider.GetRequiredService<IServiceScopeFactory>());
        }

        public PooledRoleLaunchAdmission Admission { get; }

        public List<RecordingCapacityService> Created { get; }

        public static Host Create(CapacityDecision decision)
        {
            var created = new List<RecordingCapacityService>();
            var services = new ServiceCollection();
            services.AddScoped<ICapacityService>(_ =>
            {
                var service = new RecordingCapacityService(decision);
                created.Add(service);
                return service;
            });
            return new Host(services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true
            }), created);
        }

        public ValueTask DisposeAsync() =>
            _provider.DisposeAsync();
    }

    // A hand-written capacity service: the verdict is the input under test, and disposal proves the scope ended.
    private sealed class RecordingCapacityService : ICapacityService, IDisposable
    {
        private readonly CapacityDecision _decision;

        public RecordingCapacityService(CapacityDecision decision)
        {
            _decision = decision;
        }

        public CapacityRequest? Request { get; private set; }

        public bool Disposed { get; private set; }

        public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct) =>
            DecideAsync(new CapacityRequest
            {
                ModelName = modelName,
                Role = role
            }, ct);

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(_decision);
        }

        public void Dispose() =>
            Disposed = true;
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() =>
            Disposed = true;
    }
}
