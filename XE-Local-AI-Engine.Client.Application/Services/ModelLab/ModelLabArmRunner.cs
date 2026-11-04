namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Runs baseline, vector and random control under the job's own lease, each on a private server the chat path cannot reach.</summary>
/// <remarks>
///     Greedy with one seed, each prompt alone with no prompt cache, so a difference between arms comes from the vector. Arms compare only with each
///     other: the harness's launch is not a chat's. An arm is saved as soon as its outputs exist, because a later failure, cancel or kill must not cost it.
/// </remarks>
public sealed class ModelLabArmRunner
{
    public const string HttpClientName = "ModelLabArms";

    private static readonly string[] ArmNames = [ModelLabArmNames.Baseline, ModelLabArmNames.Vector, ModelLabArmNames.Random];

    private readonly ITrainingRunEventBuffer _events;
    private readonly ITransientLlamaServerEvaluationHarness _harness;
    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _timeProvider;

    public ModelLabArmRunner(ITransientLlamaServerEvaluationHarness harness,
        IHttpClientFactory httpClients,
        ITrainingRunEventBuffer events,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _events = events;
        _harness = harness;
        _httpClients = httpClients;
        _timeProvider = timeProvider;
    }

    /// <summary>Runs all three, or throws what stopped them. Returns false when the last server was not seen to exit, which the caller must report.</summary>
    public async Task<bool> RunAsync(Guid experimentId,
        ModelLabArmPlan plan,
        ILlamaServerRuntimeMutationLease heldLease,
        Func<ModelLabSteeringArm, Task> armFinished,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(heldLease);
        ArgumentNullException.ThrowIfNull(armFinished);
        var unsteered = new TransientLlamaServerEvaluationRequest
        {
            ModelFilePath = plan.ModelFilePath,
            AdapterFilePath = null,
            ContextTokens = ModelLabTraceSpec.ArmContextTokens,
            ReadinessTimeout = TimeSpan.FromMinutes(10),
            LaunchPolicy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1
        };
        var phaseStart = _timeProvider.GetTimestamp();
        var results = await _harness.RunArmsAsync<bool>(heldLease,
            [
                unsteered,
                unsteered with { ControlVectorPath = plan.VectorPath, ControlVectorScale = plan.Scale },
                unsteered with { ControlVectorPath = plan.RandomVectorPath, ControlVectorScale = plan.Scale }
            ],
            async (arm, session, token) =>
            {
                var startup = _timeProvider.GetElapsedTime(phaseStart);
                var name = ArmNames[arm];
                _ = _events.Append(experimentId, TrainingRunEventKind.Phase, new TrainingRunPayload { Phase = $"arm-{name}" });
                var generationStart = _timeProvider.GetTimestamp();
                var outputs = new List<ModelLabArmOutput>(plan.Prompts.Count);
                using var client = _httpClients.CreateClient(HttpClientName);
                foreach (var prompt in plan.Prompts)
                {
                    outputs.Add(await GenerateAsync(client, session, prompt, token));
                    _ = _events.Append(experimentId, TrainingRunEventKind.Progress, new TrainingRunPayload { Step = outputs.Count, TotalSteps = plan.Prompts.Count });
                }

                var generation = _timeProvider.GetElapsedTime(generationStart);
                await armFinished(new ModelLabSteeringArm
                {
                    Name = name,
                    Outputs = outputs,
                    StartupSeconds = Math.Round(startup.TotalSeconds, 3),
                    GenerationSeconds = Math.Round(generation.TotalSeconds, 3),
                    Runtime = session.Launch.Variant.ToString(),
                    Build = session.Launch.ExecutableVersion,
                    ExecutableSha256 = session.Launch.ExecutableSha256,
                    ModelSha256 = session.Model.ModelSha256,
                    VectorSha256 = session.ControlVector?.Sha256,
                    Scale = session.ControlVector?.Scale
                });
                phaseStart = _timeProvider.GetTimestamp();
                return true;
            },
            ct);
        return results[^1].Teardown.ProcessExitObserved;
    }

    private static async Task<ModelLabArmOutput> GenerateAsync(HttpClient client, TransientLlamaServerEvaluationSession session, string prompt, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(new Uri(session.BaseAddress, "/v1/chat/completions"), new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = session.ModelId,
            ["messages"] = new[] { new Dictionary<string, string>(StringComparer.Ordinal) { ["role"] = "user", ["content"] = prompt } },
            ["max_tokens"] = ModelLabTraceSpec.MaxNewTokens,
            ["temperature"] = 0,
            ["seed"] = ModelLabTraceSpec.Seed,
            ["cache_prompt"] = false,
            // One candidate per token is the cheapest way this route gives the token ids, which are what arms compare on.
            ["logprobs"] = true,
            ["top_logprobs"] = 1,
            ["stream"] = false,
            ["chat_template_kwargs"] = new Dictionary<string, bool>(StringComparer.Ordinal) { ["enable_thinking"] = false }
        }, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlamaRuntimeException(string.Create(CultureInfo.InvariantCulture, $"The arm's server answered HTTP {(int)response.StatusCode}."));
        }

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var choices = document.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            throw new LlamaRuntimeException("The arm's server answered with no choices.");
        }

        var choice = choices[0];
        return new ModelLabArmOutput
        {
            Prompt = prompt,
            Text = choice.GetProperty("message").GetProperty("content").GetString() ?? string.Empty,
            TokenIds = choice.TryGetProperty("logprobs", out var logprobs) && logprobs.ValueKind == JsonValueKind.Object
                ? [.. logprobs.GetProperty("content").EnumerateArray().Select(static entry => entry.GetProperty("id").GetInt32())]
                : [],
            FinishReason = choice.TryGetProperty("finish_reason", out var reason) ? reason.GetString() : null
        };
    }
}
