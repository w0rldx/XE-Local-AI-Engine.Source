namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LlamaTokenEstimatorCalibrationServiceTests
{
    [Test]
    public async Task TryCalibrateAsync_UsesRootTokenizeEndpointAndStoresBoundedPerModelDivisor()
    {
        HttpRequestMessage? captured = null;
        string? payload = null;
        var tokenCount = Math.Max(1, LlamaTokenEstimatorCalibrationService.CalibrationText.Length / 6);
        using var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            captured = request;
            payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(TokenArray(tokenCount));
        });
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        using var service = CreateService(client, store);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://127.0.0.1:18123/v1"), CancellationToken.None);

        AssertEx.True(calibrated);
        AssertEx.Equal("http://127.0.0.1:18123/tokenize", captured!.RequestUri!.AbsoluteUri);
        using var requestDocument = JsonDocument.Parse(payload!);
        AssertEx.Equal(LlamaTokenEstimatorCalibrationService.CalibrationText,
            requestDocument.RootElement.GetProperty("content").GetString());
        AssertEx.Contains(payload, "\"add_special\":false");
        AssertEx.Equal(expected: 6, store.ResolveDivisor("model-a"));
        AssertEx.Equal(TokenEstimatorCalibrationStore.DefaultCharsPerToken, store.ResolveDivisor("model-b"));
    }

    [Test]
    public async Task TryCalibrateAsync_ProviderFailureRetainsPriorCalibration()
    {
        using var handler = new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        store.SetDivisor("model-a", charsPerToken: 6);
        using var service = CreateService(client, store);

        var calibrated = await service.TryCalibrateAsync("model-a", new Uri("http://localhost:18123/v1"), CancellationToken.None);

        AssertEx.False(calibrated);
        AssertEx.Equal(expected: 6, store.ResolveDivisor("model-a"));
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
        AssertEx.Equal(TokenEstimatorCalibrationStore.DefaultCharsPerToken, store.ResolveDivisor("model-a"));
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
    public async Task Schedule_RunsImmediatelyThenPeriodically_WithoutBlockingCaller()
    {
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) >= 2)
            {
                secondCall.TrySetResult();
            }

            return Task.FromResult(JsonResponse(TokenArray(Math.Max(1, LlamaTokenEstimatorCalibrationService.CalibrationText.Length / 5))));
        });
        using var client = new HttpClient(handler);
        var store = new TokenEstimatorCalibrationStore();
        using var service = CreateService(client, store, TimeSpan.FromMilliseconds(20));
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.Schedule("model-a", new Uri("http://127.0.0.1:18123/v1"));
            await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.True(calls >= 2);
        AssertEx.Equal(expected: 5, store.ResolveDivisor("model-a"));
    }

    [Test]
    [Arguments(1, 100, TokenEstimatorCalibrationStore.MinimumCharsPerToken)]
    [Arguments(1000, 1, TokenEstimatorCalibrationStore.MaximumCharsPerToken)]
    [Arguments(0, 0, TokenEstimatorCalibrationStore.DefaultCharsPerToken)]
    public void CalculateDivisor_ClampsAndFallsBack(int characters, int tokens, int expected)
    {
        AssertEx.Equal(expected, LlamaTokenEstimatorCalibrationService.CalculateDivisor(characters, tokens));
    }

    private static LlamaTokenEstimatorCalibrationService CreateService(HttpClient client,
        ITokenEstimatorCalibrationStore store,
        TimeSpan? interval = null)
    {
        return new LlamaTokenEstimatorCalibrationService(client,
            store,
            NullLogger<LlamaTokenEstimatorCalibrationService>.Instance,
            interval ?? TimeSpan.FromMinutes(30));
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

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}
