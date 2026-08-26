namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkExceptionHandlerTests
{
    [Test]
    public async Task TryHandleAsync_ClaimedBenchmarkExceptions_WriteTheirExistingProblemEnvelopes()
    {
        foreach (var testCase in ClaimedCases)
        {
            var result = await HandleAsync(testCase.Exception).ConfigureAwait(false);

            AssertEx.True(result.Handled, $"{testCase.Name} must be claimed by the Benchmark handler.");
            AssertEx.Equal(testCase.StatusCode, result.StatusCode, testCase.Name);
            AssertEx.Equal("application/problem+json", result.ContentType, testCase.Name);
            using var document = JsonDocument.Parse(result.Body);
            AssertEx.Equal(expected: 6, document.RootElement.EnumerateObject().Count(), testCase.Name);
            AssertEx.Equal(testCase.ProblemType, document.RootElement.GetProperty("type").GetString(), testCase.Name);
            AssertEx.Equal(testCase.Title, document.RootElement.GetProperty("title").GetString(), testCase.Name);
            AssertEx.Equal(testCase.StatusCode, document.RootElement.GetProperty("status").GetInt32(), testCase.Name);
            AssertEx.Equal(testCase.Code.ToString(), document.RootElement.GetProperty("code").GetString(), testCase.Name);
            AssertEx.Equal(testCase.Detail, document.RootElement.GetProperty("detail").GetString(), testCase.Name);
            AssertEx.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()),
                $"{testCase.Name} must preserve the ProblemDetails request trace id.");
            if (testCase.ForbiddenProviderText is not null)
            {
                AssertEx.False(result.Body.Contains(testCase.ForbiddenProviderText, StringComparison.Ordinal),
                    $"{testCase.Name} leaked provider/storage exception text.");
            }
        }
    }

    [Test]
    public async Task TryHandleAsync_KeyNotFoundAndUnrelatedExceptions_FallThrough()
    {
        foreach (var testCase in FallthroughCases)
        {
            var result = await HandleAsync(testCase.Exception).ConfigureAwait(false);

            AssertEx.False(result.Handled, $"{testCase.Name} must not be claimed by the Benchmark handler.");
            AssertEx.Equal(StatusCodes.Status200OK, result.StatusCode, testCase.Name);
            AssertEx.Equal(string.Empty, result.ContentType, testCase.Name);
            AssertEx.Equal(string.Empty, result.Body, testCase.Name);
        }
    }

    private static async Task<HandlerResult> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(options => ConfigureServices.ConfigureJsonSerializerOptions(options.SerializerOptions));
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var handled = await new BenchmarkExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false);
        body.Position = 0;
        using var reader = new StreamReader(body);
        return new HandlerResult(handled,
            context.Response.StatusCode,
            context.Response.ContentType ?? string.Empty,
            await reader.ReadToEndAsync().ConfigureAwait(false));
    }

    private static readonly BenchmarkHandlerCase[] ClaimedCases =
    [
        new("not found",
            new BenchmarkNotFoundException("unsafe-storage-path:/benchmarks/private"),
            StatusCodes.Status404NotFound,
            BenchmarkErrorCode.NotFound,
            "The requested benchmark resource was not found.",
            "Not Found",
            "https://tools.ietf.org/html/rfc9110#section-15.5.5",
            "unsafe-storage-path"),
        new("validation",
            new BenchmarkValidationException("The benchmark request is invalid."),
            StatusCodes.Status400BadRequest,
            BenchmarkErrorCode.InvalidRequest,
            "The benchmark request is invalid.",
            "Bad Request",
            "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            ForbiddenProviderText: null),
        new("known conflict",
            new BenchmarkConflictException("VersionConflict"),
            StatusCodes.Status409Conflict,
            BenchmarkErrorCode.VersionConflict,
            "The resource version changed. Refresh and retry.",
            "Conflict",
            "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            ForbiddenProviderText: null),
        new("unknown conflict fallback",
            new BenchmarkConflictException("unsafe-provider-conflict"),
            StatusCodes.Status409Conflict,
            BenchmarkErrorCode.InvalidLifecycleTransition,
            "The benchmark lifecycle transition is not allowed.",
            "Conflict",
            "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            "unsafe-provider-conflict"),
        new("eligibility",
            new BenchmarkEligibilityException("The selected model is not eligible for benchmarking."),
            StatusCodes.Status422UnprocessableEntity,
            BenchmarkErrorCode.IneligibleModel,
            "The selected model is not eligible for benchmarking.",
            "Unprocessable Entity",
            "https://tools.ietf.org/html/rfc4918#section-11.2",
            ForbiddenProviderText: null),
        new("unsupported KV cache",
            new BenchmarkUnsupportedKvCacheTypeException("The selected KV cache type is not supported."),
            StatusCodes.Status422UnprocessableEntity,
            BenchmarkErrorCode.UnsupportedKvCacheType,
            "The selected KV cache type is not supported.",
            "Unprocessable Entity",
            "https://tools.ietf.org/html/rfc4918#section-11.2",
            ForbiddenProviderText: null),
        new("judge policy changed",
            new BenchmarkJudgePolicyChangedException("unsafe-provider-revision-detail"),
            StatusCodes.Status409Conflict,
            BenchmarkErrorCode.JudgePolicyChanged,
            "The project's judge policy changed. Refresh and retry.",
            "Conflict",
            "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            "unsafe-provider-revision-detail")
    ];

    private static readonly FallthroughCase[] FallthroughCases =
    [
        new("contextual KeyNotFoundException", new KeyNotFoundException("contextual")),
        new("unrelated InvalidOperationException", new InvalidOperationException("unrelated"))
    ];

    private sealed record BenchmarkHandlerCase(string Name,
        Exception Exception,
        int StatusCode,
        BenchmarkErrorCode Code,
        string Detail,
        string Title,
        string ProblemType,
        string? ForbiddenProviderText);

    private sealed record FallthroughCase(string Name, Exception Exception);

    private sealed record HandlerResult(bool Handled, int StatusCode, string ContentType, string Body);
}
