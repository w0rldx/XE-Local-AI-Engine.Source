namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>/v1/systemone</c> client: the wire body per question type, the three answer shapes, and a typed outcome for
///     every way a call fails, because the graph routes a 501 differently from a timeout.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LlamaServerSystemOneClientTests
{
    private const string ModelName = "ggml-org/Julia-1-GGUF:Q8_0";
    private static readonly Uri Endpoint = new("http://127.0.0.1:18200/v1");

    [Test]
    public async Task AChoiceQuestion_PostsOneNamedQuestionAndReadsTheChoice()
    {
        using var lease = new RecordingInferenceLease();
        using var handler = new BodyCapturingHandler(_ => JsonOk(
            """{"model":"m","answers":{"q":{"type":"choice","choice":"billing","probabilities":{"billing":0.9,"shipping":0.1},"confidence":0.8}},"usage":{"input_tokens":42,"output_tokens":0}}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(lease), http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(Choice(), CancellationToken.None);

        AssertEx.Equal(LlamaServerSystemOneOutcome.Answered, result.Outcome);
        var answer = AssertEx.NotNull(result.Answer);
        AssertEx.Equal("choice", answer.Type);
        AssertEx.Equal("billing", answer.Choice);
        AssertEx.Equal(0.9, AssertEx.NotNull(answer.Probabilities)["billing"]);
        AssertEx.Equal(0.8, answer.Confidence);
        AssertEx.Equal(42, result.InputTokens);
        AssertEx.True(lease.Disposed, "the lease is released once the call settles.");

        AssertEx.Equal("http://127.0.0.1:18200/v1/systemone", handler.LastRequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(AssertEx.NotNull(handler.LastBody));
        AssertEx.Equal("Ticket: my card was charged twice", body.RootElement.GetProperty("state").GetString());
        var question = body.RootElement.GetProperty("questions").GetProperty("q");
        AssertEx.Equal("choice", question.GetProperty("type").GetString());
        AssertEx.Equal("Which team?", question.GetProperty("instructions").GetString());
        var criteria = question.GetProperty("criteria");
        AssertEx.Equal("payments, refunds", criteria.GetProperty("billing").GetString());
        AssertEx.Equal(JsonValueKind.Null, criteria.GetProperty("shipping").ValueKind, "an undescribed option is sent with a null description.");
    }

    [Test]
    public async Task AScoreQuestion_SendsTheLevelsInOrderAndReadsTheScoreAndLegend()
    {
        using var handler = new BodyCapturingHandler(_ => JsonOk(
            """{"answers":{"q":{"type":"score","score":1.6,"legend":{"0":"can wait","1":"soon","2":"now"},"probabilities":{"0":0.1,"1":0.3,"2":0.6},"confidence":0.6}},"usage":{"input_tokens":7}}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(), http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(new LlamaServerSystemOneRequest
        {
            ModelName = ModelName,
            State = "server down",
            QuestionType = LlamaServerSystemOneQuestionType.Score,
            Instructions = "How urgent?",
            Labels = ["can wait", "soon", "now"]
        }, CancellationToken.None);

        var answer = AssertEx.NotNull(result.Answer);
        AssertEx.Equal(1.6, answer.Score);
        AssertEx.Equal("now", AssertEx.NotNull(answer.Legend)["2"]);
        using var body = JsonDocument.Parse(AssertEx.NotNull(handler.LastBody));
        var question = body.RootElement.GetProperty("questions").GetProperty("q");
        AssertEx.Equal("score", question.GetProperty("type").GetString());
        AssertEx.Equal("can wait,soon,now", string.Join(",", question.GetProperty("criteria").EnumerateArray().Select(static level => level.GetString())));
    }

    [Test]
    public async Task ANoulQuestion_SendsNoCriteriaAndReadsTheLikelihood()
    {
        using var handler = new BodyCapturingHandler(_ => JsonOk("""{"answers":{"q":{"type":"noul","noul":0.63}}}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(), http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(new LlamaServerSystemOneRequest
        {
            ModelName = ModelName,
            State = "x",
            QuestionType = LlamaServerSystemOneQuestionType.Noul,
            Instructions = "Is it spam?"
        }, CancellationToken.None);

        AssertEx.Equal(0.63, AssertEx.NotNull(result.Answer).Noul);
        AssertEx.Null(result.InputTokens, "a response without usage reports no tokens rather than zero.");
        using var body = JsonDocument.Parse(AssertEx.NotNull(handler.LastBody));
        AssertEx.False(body.RootElement.GetProperty("questions").GetProperty("q").TryGetProperty("criteria", out _));
    }

    [Test]
    [Arguments(HttpStatusCode.NotImplemented, LlamaServerSystemOneOutcome.NotDecisionModel)]
    [Arguments(HttpStatusCode.BadRequest, LlamaServerSystemOneOutcome.InvalidRequest)]
    [Arguments(HttpStatusCode.InternalServerError, LlamaServerSystemOneOutcome.Transport)]
    public async Task AFailureStatus_IsTypedAndKeepsTheServersReason(HttpStatusCode status, LlamaServerSystemOneOutcome expected)
    {
        using var lease = new RecordingInferenceLease();
        using var handler = new BodyCapturingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"error":{"message":"server says no"}}""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(lease), http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(Choice(), CancellationToken.None);

        AssertEx.Equal(expected, result.Outcome);
        AssertEx.Null(result.Answer);
        AssertEx.Contains(AssertEx.NotNull(result.Detail), "server says no");
        AssertEx.Contains(result.Detail!, ((int)status).ToString(CultureInfo.InvariantCulture));
        AssertEx.True(lease.Disposed);
    }

    [Test]
    public async Task APromptLargerThanOneBatch_IsARefusedRequest_ThoughTheServerSays500()
    {
        // Body verbatim from llama.cpp b11669.
        using var handler = new BodyCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                """{"error":{"code":500,"message":"input (10516 tokens) is too large to process. increase the physical batch size (current batch size: 7936)","type":"server_error"}}""",
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(), http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(Choice(), CancellationToken.None);

        AssertEx.Equal(LlamaServerSystemOneOutcome.InvalidRequest, result.Outcome);
    }

    [Test]
    public async Task AnAnswerForNoQuestion_IsMalformed()
    {
        using var handler = new BodyCapturingHandler(_ => JsonOk("""{"answers":{"other":{"type":"choice","choice":"a"}}}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(), http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(Choice(), CancellationToken.None);

        AssertEx.Equal(LlamaServerSystemOneOutcome.Malformed, result.Outcome);
    }

    [Test]
    public async Task AServerThatHangs_TimesOutOnTheClientsOwnBudget_NotTheCallers()
    {
        using var handler = new BodyCapturingHandler(_ => JsonOk("{}"), TimeSpan.FromSeconds(30));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(), http, NullLogger<LlamaServerSystemOneClient>.Instance,
            requestTimeout: TimeSpan.FromMilliseconds(50));
        using var callerCts = new CancellationTokenSource();

        var result = await client.DecideAsync(Choice(), callerCts.Token);

        AssertEx.Equal(LlamaServerSystemOneOutcome.Timeout, result.Outcome);
        AssertEx.False(callerCts.IsCancellationRequested);
    }

    [Test]
    public async Task TwoCallsOnOneModel_ReachTheServerOneAtATime()
    {
        // The process is --parallel 1: a second request sent at once would spend its own budget queued behind the first.
        using var handler = new ParkingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(Ready(), http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var first = client.DecideAsync(Choice(), CancellationToken.None);
        await handler.FirstArrived.WaitAsync(TestBudgets.Contended);
        var second = client.DecideAsync(Choice(), CancellationToken.None);
        handler.Release();
        var results = await Task.WhenAll(first, second).WaitAsync(TestBudgets.Contended);

        AssertEx.True(results.All(static result => result.Outcome == LlamaServerSystemOneOutcome.Answered));
        AssertEx.Equal(expected: 2, handler.Requests);
        AssertEx.Equal(expected: 1, handler.MaxInFlight, "the second call waits for the first to settle before it posts.");
    }

    [Test]
    public async Task AColdDecisionModel_IsStartedThenLeased()
    {
        using var lease = new RecordingInferenceLease();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(ModelName, ModelRole.Decision)
                  .Returns(LlamaServerLeaseAcquisition.NotRunning, LlamaServerLeaseAcquisition.Granted(lease));
        supervisor.EnsureRunningAsync(ModelName, ModelRole.Decision, Arg.Any<CancellationToken>()).Returns(DecisionEndpoint());
        using var handler = new BodyCapturingHandler(_ => JsonOk("""{"answers":{"q":{"type":"choice","choice":"billing"}}}"""));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(supervisor, http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(Choice(), CancellationToken.None);

        AssertEx.Equal(LlamaServerSystemOneOutcome.Answered, result.Outcome);
        await supervisor.Received(1).EnsureRunningAsync(ModelName, ModelRole.Decision, Arg.Any<CancellationToken>());
        supervisor.Received(2).TryAcquireInferenceLease(ModelName, ModelRole.Decision);
        AssertEx.True(lease.Disposed, "the lease taken after the start is released when the call settles.");
    }

    [Test]
    public async Task AProcessThatCannotStart_IsUnavailable()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(ModelName, ModelRole.Decision).Returns(LlamaServerLeaseAcquisition.NotRunning);
        supervisor.EnsureRunningAsync(ModelName, ModelRole.Decision, Arg.Any<CancellationToken>())
                  .Returns<Task<LlamaServerEndpoint>>(_ => throw new LlamaRuntimeException("The requested model is not installed."));
        using var handler = new BodyCapturingHandler(_ => JsonOk("{}"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(supervisor, http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(Choice(), CancellationToken.None);

        AssertEx.Equal(LlamaServerSystemOneOutcome.Unavailable, result.Outcome);
        AssertEx.Null(handler.LastRequestUri, "nothing is POSTed without a process.");
    }

    [Test]
    [Arguments(true, LlamaServerSystemOneOutcome.Evicting)]
    [Arguments(false, LlamaServerSystemOneOutcome.Busy)]
    public async Task AnEvictingOrProfiledProcess_IsRefusedWithoutAPost(bool evicting, LlamaServerSystemOneOutcome expected)
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(ModelName, ModelRole.Decision)
                  .Returns(evicting ? LlamaServerLeaseAcquisition.Evicting : LlamaServerLeaseAcquisition.ProfilingOwned);
        using var handler = new BodyCapturingHandler(_ => JsonOk("{}"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new LlamaServerSystemOneClient(supervisor, http, NullLogger<LlamaServerSystemOneClient>.Instance);

        var result = await client.DecideAsync(Choice(), CancellationToken.None);

        AssertEx.Equal(expected, result.Outcome);
        AssertEx.Null(handler.LastRequestUri);
        await supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void TheBudget_IsFiveSecondsPlusTenMillisecondsPerEstimatedToken_CappedAtThirty()
    {
        AssertEx.Equal(TimeSpan.FromSeconds(5), LlamaServerSystemOneClient.ResolveRequestTimeout(new LlamaServerSystemOneRequest
        {
            ModelName = ModelName,
            State = string.Empty,
            QuestionType = LlamaServerSystemOneQuestionType.Noul,
            Instructions = string.Empty
        }));
        AssertEx.Equal(TimeSpan.FromSeconds(6), LlamaServerSystemOneClient.ResolveRequestTimeout(new LlamaServerSystemOneRequest
        {
            ModelName = ModelName,
            State = new string('x', 400),
            QuestionType = LlamaServerSystemOneQuestionType.Noul,
            Instructions = string.Empty
        }), "400 characters estimate 100 tokens, one second.");
        AssertEx.Equal(TimeSpan.FromSeconds(30), LlamaServerSystemOneClient.ResolveRequestTimeout(new LlamaServerSystemOneRequest
        {
            ModelName = ModelName,
            State = new string('x', 1_000_000),
            QuestionType = LlamaServerSystemOneQuestionType.Noul,
            Instructions = string.Empty
        }));
    }

    private static LlamaServerSystemOneRequest Choice() =>
        new()
        {
            ModelName = ModelName,
            State = "Ticket: my card was charged twice",
            QuestionType = LlamaServerSystemOneQuestionType.Choice,
            Instructions = "Which team?",
            Labels = ["billing", "shipping"],
            Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["billing"] = "payments, refunds"
            }
        };

    private static LlamaServerEndpoint DecisionEndpoint() =>
        new()
        {
            ModelName = ModelName,
            Role = ModelRole.Decision,
            BaseAddress = Endpoint
        };

    /// <summary>A warm decision model whose every lease is a fresh one the client must release.</summary>
    private static ILlamaServerProcessSupervisor Ready()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(Arg.Any<string>(), ModelRole.Decision)
                  .Returns(_ => LlamaServerLeaseAcquisition.Granted(new RecordingInferenceLease(), DecisionEndpoint()));
        return supervisor;
    }

    private static ILlamaServerProcessSupervisor Ready(RecordingInferenceLease lease)
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(Arg.Any<string>(), ModelRole.Decision).Returns(_ => LlamaServerLeaseAcquisition.Granted(lease, DecisionEndpoint()));
        return supervisor;
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    /// <summary>Parks every request until the test releases it, and records the most requests ever in flight at once.</summary>
    private sealed class ParkingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _maxInFlight;
        private int _requests;

        public Task FirstArrived => _arrived.Task;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int Requests => Volatile.Read(ref _requests);

        public void Release() =>
            _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_released)
            {
                _requests++;
                _inFlight++;
                _maxInFlight = Math.Max(_maxInFlight, _inFlight);
            }

            _arrived.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            lock (_released)
            {
                _inFlight--;
            }

            return JsonOk("""{"answers":{"q":{"type":"choice","choice":"billing"}}}""");
        }
    }

    private sealed class BodyCapturingHandler : HttpMessageHandler
    {
        private readonly TimeSpan? _delay;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public BodyCapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, TimeSpan? delay = null)
        {
            _responder = responder;
            _delay = delay;
        }

        public Uri? LastRequestUri { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_delay is { } pause)
            {
                // real-timer: a hang is the input. It honors the token, so the client's own bounded timeout is what ends the wait.
                await Task.Delay(pause, cancellationToken);
            }

            return _responder(request);
        }
    }
}
