namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
[Category(TestCategories.Unit)]
public sealed class LlamaTokenEstimatorCalibrationServiceTests
{
    [Test]
    public async Task TryCalibrateAsync_UsesRootTokenizeEndpointAndNeverUndercountsObservedSample()
    {
        HttpRequestMessage? captured = null;
        string? payload = null;
        var tokenCount = (LlamaTokenEstimatorCalibrationService.CalibrationText.Length / 6) + 1;
        using var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            captured = request;
            payload = await (request.Content ?? throw new ArgumentNullException(nameof(request))).ReadAsStringAsync(cancellationToken);
            return JsonResponse(TokenArray(tokenCount));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        using var service = CreateService(client, store);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123/v1"), CancellationToken.None);

        AssertEx.True(calibrated);
        AssertEx.Equal("http://127.0.0.1:18123/tokenize", captured!.RequestUri!.AbsoluteUri);
        using var requestDocument = JsonDocument.Parse(payload!);
        AssertEx.Equal(LlamaTokenEstimatorCalibrationService.CalibrationText,
            requestDocument.RootElement.GetProperty("content").GetString());
        var divisor = store.ResolveDivisor("model-a");
        AssertEx.True(LlamaTokenEstimatorCalibrationService.CalibrationText.Length / divisor >= tokenCount,
            "the calibrated estimate for the observed ASCII sample must never be below /tokenize's token count");
    }

    [Test]
    public async Task TryCalibrateAsync_SuccessLogsOnlyTheMeasuredDivisor()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(JsonResponse(TokenArray(CalibrationTextTokenCount(divisor: 5)))));
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        var logger = new CapturingLogger();
        using var service = CreateService(client, store, logger: logger);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://localhost:18123/v1"), CancellationToken.None);

        AssertEx.True(calibrated);
        AssertEx.ContainsSingle(logger.Divisors, divisor => divisor == store.ResolveDivisor("model-a"));
        AssertEx.Empty(logger.Reasons);
    }

    [Test]
    public async Task TryCalibrateAsync_MeasuresTheToolTemplatePreambleFromRenderedCountsWithAndWithoutTheProbeTools()
    {
        var countRequests = new ConcurrentQueue<(Uri Uri, string Body)>();
        using var handler = new DelegateHandler((_, _) => Task.FromResult(JsonResponse(TokenArray(CalibrationTextTokenCount(divisor: 4)))),
            async (request, cancellationToken) =>
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                countRequests.Enqueue((request.RequestUri!, body));
                using var document = JsonDocument.Parse(body);
                return JsonResponse(InputTokens(document.RootElement.GetProperty("tools").GetArrayLength() == 0 ? 40 : 540));
            });
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        var logger = new CapturingLogger();
        using var service = CreateService(client, store, logger: logger);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123/v1"), CancellationToken.None);

        AssertEx.True(calibrated);
        AssertEx.Equal(expected: 4, store.ResolveDivisor("model-a"));
        // What the budgeters already charge for the two probe tools: each definition framed as one message, plus the wrapper constant.
        var probeCharge = LlamaTokenEstimatorCalibrationService.ProbeTools.Sum(static tool =>
            ((tool.Name.Length + 1 + tool.Description.Length + 1 + tool.JsonSchema.GetRawText().Length) / 4) + 4
            + TokenEstimatorCalibrationStore.ToolDefinitionWrapperTokens);
        AssertEx.Equal(500 - probeCharge, store.ResolveToolTemplatePreamble("model-a"));
        AssertEx.ContainsSingle(logger.Preambles, preamble => preamble == 500 - probeCharge);

        var requests = countRequests.ToArray();
        AssertEx.Equal(expected: 2, requests.Length);
        AssertEx.True(requests.All(static request => request.Uri.AbsoluteUri == "http://127.0.0.1:18123/v1/messages/count_tokens"));
        using var without = JsonDocument.Parse(requests[0].Body);
        using var with = JsonDocument.Parse(requests[1].Body);
        AssertEx.Equal(LlamaTokenEstimatorCalibrationService.ProbeSystemPrompt, with.RootElement.GetProperty("system").GetString());
        AssertEx.Equal(LlamaTokenEstimatorCalibrationService.ProbeUserMessage,
            with.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        AssertEx.Equal(expected: 0, without.RootElement.GetProperty("tools").GetArrayLength());
        AssertEx.True(with.RootElement.GetProperty("tools").EnumerateArray()
                          .Select(static tool => tool.GetProperty("name").GetString())
                          .SequenceEqual(["get_weather", "search_documents"]));
        AssertEx.Equal("object", with.RootElement.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Test]
    public async Task TryCalibrateAsync_WhenTheTemplateCountFails_StoresTheDivisorAndKeepsThePriorPreamble()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(JsonResponse(TokenArray(CalibrationTextTokenCount(divisor: 5)))),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        store.SetToolTemplatePreamble("model-a", preambleTokens: 77);
        var logger = new CapturingLogger();
        using var service = CreateService(client, store, logger: logger);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123/v1"), CancellationToken.None);

        AssertEx.True(calibrated, "the divisor channel does not depend on the template probe");
        AssertEx.Equal(expected: 5, store.ResolveDivisor("model-a"));
        AssertEx.Equal(expected: 77, store.ResolveToolTemplatePreamble("model-a"));
        AssertEx.ContainsSingle(logger.Reasons, static reason => reason == "HttpStatus");
        AssertEx.ContainsSingle(logger.Preambles, static preamble => preamble is null);
    }

    [Test]
    public async Task TryCalibrateAsync_WhenTheConstantsAlreadyOverChargeTheTemplate_StoresZero()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(JsonResponse(TokenArray(CalibrationTextTokenCount(divisor: 4)))),
            (_, _) => Task.FromResult(JsonResponse(InputTokens(60))));
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        using var service = CreateService(client, store);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123/v1"), CancellationToken.None);

        AssertEx.True(calibrated);
        AssertEx.True(LlamaTokenEstimatorCalibrationService.CalculateToolTemplatePreamble(60, 60, charsPerToken: 4) < 0);
        AssertEx.Equal(expected: 0, store.ResolveToolTemplatePreamble("model-a"));
    }

    [Test]
    public async Task TryCalibrateAsync_ProviderFailureRetainsPriorCalibrationAndLogsBoundedReason()
    {
        using var handler = new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        store.SetDivisor("model-a", charsPerToken: 6);
        var logger = new CapturingLogger();
        using var service = CreateService(client, store, logger: logger);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://localhost:18123/v1"), CancellationToken.None);

        AssertEx.False(calibrated);
        AssertEx.Equal(expected: 6, store.ResolveDivisor("model-a"));
        AssertEx.ContainsSingle(logger.Reasons, static reason => reason == "HttpStatus");
    }

    [Test]
    public async Task TryCalibrateAsync_RedirectIsRejectedWithoutFollowingOrChangingCalibration()
    {
        var calls = 0;
        using var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers =
                {
                    Location = new Uri("https://example.test/tokenize")
                }
            });
        });
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        store.SetDivisor("model-a", 5);
        using var service = CreateService(client, store);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123"), CancellationToken.None);

        AssertEx.False(calibrated);
        AssertEx.Equal(1, calls);
        AssertEx.Equal(5, store.ResolveDivisor("model-a"));
    }

    [Test]
    public async Task TryCalibrateAsync_FinalRemoteEndpointIsRejected()
    {
        using var handler = new DelegateHandler(async (_, _) =>
        {
            await Task.Yield();
            var response = JsonResponse(TokenArray(10));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://example.test/tokenize");
            return response;
        });
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        using var service = CreateService(client, store);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123"), CancellationToken.None);

        AssertEx.False(calibrated);
        AssertEx.Equal(TokenEstimatorCalibrationStore.DefaultCharsPerToken, store.ResolveDivisor("model-a"));
    }

    [Test]
    public void CreateTokenizeHandler_DisablesRedirectsAndAmbientProxy()
    {
        using var handler = LlamaServerNativeClient.CreateTokenizeHandler();

        AssertEx.False(handler.AllowAutoRedirect);
        AssertEx.False(handler.UseProxy);
        AssertEx.True(handler.CheckCertificateRevocationList);
    }

    [Test]
    public async Task TryCalibrateAsync_RemoteEndpointIsRejectedWithoutNetworkCall()
    {
        var calls = 0;
        using var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(TokenArray(10)));
        });
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        using var service = CreateService(client, store);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("https://example.test/v1"), CancellationToken.None);

        AssertEx.False(calibrated);
        AssertEx.Equal(0, calls);
    }

    [Test]
    public async Task TryCalibrateAsync_CallerCancellationIsPropagated()
    {
        using var handler = new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(TokenArray(10));
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(client, new TokenEstimatorCalibrationStore());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123/v1"), cancellation.Token));
    }

    [Test]
    public async Task Schedule_RecalibratesOnlyWhenARequestTriggersTheDueCheck()
    {
        var calls = 0;
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler((_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                firstCall.TrySetResult();
            }
            else if (call == 2)
            {
                secondCall.TrySetResult();
            }

            return Task.FromResult(JsonResponse(TokenArray(CalibrationTextTokenCount(5))));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        using var service = CreateService(client,
            store,
            interval: TimeSpan.FromMinutes(30),
            timeProvider: timeProvider);
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.Schedule("model-a", new Uri("http://127.0.0.1:18123"));
            await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await store.WaitForWriteAsync("model-a", 5).WaitAsync(TimeSpan.FromSeconds(2));

            timeProvider.Advance(TimeSpan.FromMinutes(31));
            AssertEx.Equal(1, calls);

            service.Schedule("model-a", new Uri("http://127.0.0.1:18123"));
            await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.Equal(2, calls);
        AssertEx.Equal(5, store.ResolveDivisor("model-a"));
    }

    [Test]
    public async Task Schedule_InvalidatedQueuedTarget_DoesNotContactStaleEndpointOrWriteDivisor()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contactedPorts = new ConcurrentQueue<int>();
        using var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            contactedPorts.Enqueue(request.RequestUri!.Port);
            if (request.RequestUri.Port == 18123)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return JsonResponse(TokenArray(CalibrationTextTokenCount(5)));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        using var service = CreateService(client, store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.Schedule("active-model", new Uri("http://127.0.0.1:18123"));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            service.Schedule("stale-model", new Uri("http://127.0.0.1:18124"));
            service.Invalidate("stale-model");
            service.Schedule("barrier-model", new Uri("http://127.0.0.1:18125"));

            releaseFirst.TrySetResult();
            await store.WaitForWriteAsync("barrier-model", 5).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.False(contactedPorts.Contains(18124), "invalidated queued work must be discarded before HTTP begins");
        AssertEx.False(store.Writes.Any(static write => write.ModelName == "stale-model"),
            "invalidated queued work must never update the calibration store");
    }

    [Test]
    public async Task Schedule_WhenQueueIsFull_RejectedWorkRemainsRetryable()
    {
        var contactedPorts = new ConcurrentQueue<int>();
        using var handler = new DelegateHandler((request, _) =>
        {
            contactedPorts.Enqueue(request.RequestUri!.Port);
            return Task.FromResult(JsonResponse(TokenArray(CalibrationTextTokenCount(5))));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        using var service = CreateService(client, store, workCapacity: 1);

        service.Schedule("model-a", new Uri("http://127.0.0.1:18123"));
        service.Schedule("model-b", new Uri("http://127.0.0.1:18124"));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.WaitForWriteAsync("model-a", 5).WaitAsync(TimeSpan.FromSeconds(2));
            AssertEx.False(store.Writes.Any(static write => write.ModelName == "model-b"));

            service.Schedule("model-b", new Uri("http://127.0.0.1:18124"));
            await store.WaitForWriteAsync("model-b", 5).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.True(contactedPorts.SequenceEqual([18123, 18124]));
    }

    [Test]
    public async Task Schedule_RepeatedSameTarget_CoalescesPendingWork()
    {
        var calls = 0;
        using var handler = new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(JsonResponse(TokenArray(CalibrationTextTokenCount(5))));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        using var service = CreateService(client, store, workCapacity: 2);

        for (var index = 0; index < 100; index++)
        {
            service.Schedule("model-a", new Uri("http://127.0.0.1:18123"));
        }

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.WaitForWriteAsync("model-a", 5).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.Equal(1, calls);
        AssertEx.Equal(1, store.Writes.Count);
    }

    [Test]
    public async Task InvalidateThenReschedule_SamePortReuseDiscardsEjectedGeneration()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new DelegateHandler(async (_, cancellationToken) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return JsonResponse(TokenArray(CalibrationTextTokenCount(8)));
            }

            return JsonResponse(TokenArray(CalibrationTextTokenCount(2)));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        using var service = CreateService(client, store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var reused = new Uri("http://127.0.0.1:18123");
            service.Schedule("model-a", reused);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            service.Invalidate("model-a");
            service.Schedule("model-a", reused);
            releaseFirst.TrySetResult();
            await store.WaitForWriteAsync("model-a", 2).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.Equal(2, store.ResolveDivisor("model-a"));
        AssertEx.True(store.Writes.SequenceEqual([new CalibrationWrite("model-a", 2)]));
    }

    [Test]
    public async Task EndpointChange_ResetsGenerationAndDiscardsOldEndpointResult()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.Port == 18123)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return JsonResponse(TokenArray(CalibrationTextTokenCount(8)));
            }

            return JsonResponse(TokenArray(CalibrationTextTokenCount(3)));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        using var service = CreateService(client, store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.Schedule("model-a", new Uri("http://127.0.0.1:18123"));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            service.Schedule("model-a", new Uri("http://127.0.0.1:18124"));
            releaseFirst.TrySetResult();
            await store.WaitForWriteAsync("model-a", 3).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.Equal(3, store.ResolveDivisor("model-a"));
    }

    [Test]
    [Arguments(100, 100, TokenEstimatorCalibrationStore.MinimumCharsPerToken)]
    [Arguments(10, 6, 1)]
    [Arguments(1000, 1, TokenEstimatorCalibrationStore.MaximumCharsPerToken)]
    [Arguments(0, 0, TokenEstimatorCalibrationStore.DefaultCharsPerToken)]
    public void CalculateDivisor_ClampsAndFallsBackWithoutUndercounting(int characters, int tokens, int expected)
    {
        var divisor = LlamaTokenEstimatorCalibrationService.CalculateDivisor(characters, tokens);
        AssertEx.Equal(expected, divisor);
        if (characters > 0 && tokens > 0)
        {
            AssertEx.True(characters / divisor >= tokens);
        }
    }

    [Test]
    public async Task Schedule_WhenProfilingOwnsTheModel_SkipsTheProbeInsteadOfPostingToTheMeasurement()
    {
        // The probe is dispatched from the worker long after the chat that scheduled it. Unleased, profiling's claim
        // wins and this POST lands on whatever now answers that port — commonly the measurement process itself.
        var contacted = 0;
        using var handler = new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref contacted);
            return Task.FromResult(JsonResponse(TokenArray(count: 10)));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(Arg.Any<string>(), Arg.Any<ModelRole>())
                  .Returns(_ =>
                  {
                      refused.TrySetResult();
                      return LlamaServerLeaseAcquisition.ProfilingOwned;
                  });
        using var service = CreateService(client, store, supervisor: supervisor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.Schedule("model-a", new Uri("http://127.0.0.1:18123"));

            // The lease decision is the point the worker either sends or skips; once it has been made and refused,
            // any probe would already have been dispatched.
            await refused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.Equal(expected: 0, Volatile.Read(ref contacted), "No probe may be sent while profiling owns the model.");
        _ = supervisor.Received(1).TryAcquireInferenceLease("model-a", ModelRole.Chat);
    }

    [Test]
    public async Task Schedule_HoldsAnInferenceLeaseWhileProbing()
    {
        var heldDuringProbe = false;
        var lease = Substitute.For<ILlamaServerInferenceLease>();
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler((_, _) =>
        {
            heldDuringProbe = lease.ReceivedCalls().All(call => call.GetMethodInfo().Name != nameof(IDisposable.Dispose));
            probed.TrySetResult();
            return Task.FromResult(JsonResponse(TokenArray(count: 10)));
        });
        using var client = new HttpClient(handler);
        var store = new RecordingCalibrationStore();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(Arg.Any<string>(), Arg.Any<ModelRole>())
                  .Returns(LlamaServerLeaseAcquisition.Granted(lease));
        using var service = CreateService(client, store, supervisor: supervisor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.Schedule("model-a", new Uri("http://127.0.0.1:18123"));
            await probed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.True(heldDuringProbe, "The lease must still be held while the probe is in flight.");
        lease.Received(1).Dispose();
    }

    private static LlamaTokenEstimatorCalibrationService CreateService(HttpClient client,
        ITokenEstimatorCalibrationStore store,
        TimeSpan? interval = null,
        ILogger<LlamaTokenEstimatorCalibrationService>? logger = null,
        TimeProvider? timeProvider = null,
        int? workCapacity = null,
        ILlamaServerProcessSupervisor? supervisor = null)
    {
        return new LlamaTokenEstimatorCalibrationService(new LlamaServerNativeClient(Substitute.For<IHttpClientFactory>(), client),
            store,
            supervisor ?? LeasingSupervisor(),
            logger ?? NullLogger<LlamaTokenEstimatorCalibrationService>.Instance,
            interval ?? TimeSpan.FromMinutes(30),
            timeProvider ?? TimeProvider.System,
            workCapacity ?? LlamaTokenEstimatorCalibrationService.DefaultWorkCapacity);
    }

    /// <summary>A supervisor that grants the probe's lease, which is the normal state for a model serving requests.</summary>
    private static ILlamaServerProcessSupervisor LeasingSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireInferenceLease(Arg.Any<string>(), Arg.Any<ModelRole>())
                  .Returns(LlamaServerLeaseAcquisition.Granted(Substitute.For<ILlamaServerInferenceLease>()));
        return supervisor;
    }

    private static int CalibrationTextTokenCount(int divisor)
    {
        return Math.Max(1, LlamaTokenEstimatorCalibrationService.CalibrationText.Length / divisor);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string TokenArray(int count)
    {
        return $$"""{"tokens":[{{string.Join(',', Enumerable.Repeat("1", count))}}]}""";
    }

    private static string InputTokens(int count)
    {
        return $$"""{"input_tokens":{{count}}}""";
    }

    /// <summary>Routes <c>/tokenize</c> to the test's handler and <c>count_tokens</c> to a separate one, so a divisor test sees only its own requests.</summary>
    /// <remarks>The default count answers a consistent pair: 60 without tools, 460 with.</remarks>
    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _countHandler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? countHandler = null)
        {
            _handler = handler;
            _countHandler = countHandler ?? DefaultCountAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return request.RequestUri!.AbsolutePath == "/v1/messages/count_tokens"
                ? _countHandler(request, cancellationToken)
                : _handler(request, cancellationToken);
        }

        private static async Task<HttpResponseMessage> DefaultCountAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse(InputTokens(body.RootElement.GetProperty("tools").GetArrayLength() == 0 ? 60 : 460));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _sync = new();
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            lock (_sync)
            {
                _utcNow += elapsed;
            }
        }
    }

    private sealed class RecordingCalibrationStore : ITokenEstimatorCalibrationStore
    {
        private readonly ConcurrentDictionary<string, int> _divisors = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<CalibrationWrite, TaskCompletionSource> _expectedWrites = new();

        public ConcurrentQueue<CalibrationWrite> Writes { get; } = new();

        public int ResolveDivisor(string? modelName)
        {
            return modelName is not null && _divisors.TryGetValue(modelName, out var divisor)
                ? divisor
                : TokenEstimatorCalibrationStore.DefaultCharsPerToken;
        }

        // The /tokenize channel this fixture exercises is independent of the observed-usage channel: this service never
        // writes one and never reads the other, which is precisely what keeps a provider failure here from disturbing a
        // correction real rounds have already earned.
        public void RecordObservedUsage(string modelName, long estimatedTokens, long observedInputTokens)
        {
            throw new NotSupportedException("The llama.cpp /tokenize calibration service must not write observed-usage samples.");
        }

        public double ResolveObservedCorrection(string? modelName)
        {
            return TokenEstimatorCalibrationStore.NeutralObservedCorrection;
        }

        public ConcurrentDictionary<string, int> Preambles { get; } = new(StringComparer.Ordinal);

        public int ResolveToolTemplatePreamble(string? modelName)
        {
            return modelName is not null && Preambles.TryGetValue(modelName, out var preamble) ? preamble : 0;
        }

        public void SetToolTemplatePreamble(string modelName, int preambleTokens)
        {
            Preambles[modelName] = preambleTokens;
        }

        public void SetDivisor(string modelName, int charsPerToken)
        {
            var write = new CalibrationWrite(modelName, charsPerToken);
            _divisors[modelName] = charsPerToken;
            Writes.Enqueue(write);
            if (_expectedWrites.TryGetValue(write, out var completion))
            {
                completion.TrySetResult();
            }
        }

        public Task WaitForWriteAsync(string modelName, int charsPerToken)
        {
            var write = new CalibrationWrite(modelName, charsPerToken);
            var completion = _expectedWrites.GetOrAdd(write,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Writes.Contains(write))
            {
                completion.TrySetResult();
            }

            return completion.Task;
        }
    }

    private readonly record struct CalibrationWrite(string ModelName, int CharsPerToken);

    private sealed class CapturingLogger : ILogger<LlamaTokenEstimatorCalibrationService>
    {
        public List<string> Reasons { get; } = [];

        public List<int> Divisors { get; } = [];

        public List<int?> Preambles { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                var reason = values.FirstOrDefault(static pair => pair.Key == "FailureReason").Value as string;
                if (reason is not null)
                {
                    Reasons.Add(reason);
                }

                if (values.FirstOrDefault(static pair => pair.Key == "CharsPerToken").Value is int divisor)
                {
                    Divisors.Add(divisor);
                    Preambles.Add(values.FirstOrDefault(static pair => pair.Key == "ToolTemplatePreambleTokens").Value as int?);
                }
            }
        }
    }
}
