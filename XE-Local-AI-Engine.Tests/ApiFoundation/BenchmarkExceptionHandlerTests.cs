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

[Category(TestCategories.Unit)]
public sealed class BenchmarkExceptionHandlerTests
{
    [Test]
    public async Task TryHandleAsync_ClaimedBenchmarkExceptions_WriteTheirExistingProblemEnvelopes()
    {
        foreach (var testCase in ClaimedCases)
        {
            var result = await HandleAsync(testCase.Exception);

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
            var result = await HandleAsync(testCase.Exception);

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

        var handled = await new BenchmarkExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None);
        body.Position = 0;
        using var reader = new StreamReader(body);
        return new HandlerResult
        {
            Handled = handled,
            StatusCode = context.Response.StatusCode,
            ContentType = context.Response.ContentType ?? string.Empty,
            Body = await reader.ReadToEndAsync()
        };
    }

    private static readonly BenchmarkHandlerCase[] ClaimedCases =
    [
        new()
        {
            Name = "not found",
            Exception = new BenchmarkNotFoundException("unsafe-storage-path:/benchmarks/private"),
            StatusCode = StatusCodes.Status404NotFound,
            Code = BenchmarkErrorCode.NotFound,
            Detail = "The requested benchmark resource was not found.",
            Title = "Not Found",
            ProblemType = "https://tools.ietf.org/html/rfc9110#section-15.5.5",
            ForbiddenProviderText = "unsafe-storage-path"
        },
        new()
        {
            Name = "validation",
            Exception = new BenchmarkValidationException("The benchmark request is invalid."),
            StatusCode = StatusCodes.Status400BadRequest,
            Code = BenchmarkErrorCode.InvalidRequest,
            Detail = "The benchmark request is invalid.",
            Title = "Bad Request",
            ProblemType = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            ForbiddenProviderText = null
        },
        new()
        {
            Name = "known conflict",
            Exception = new BenchmarkConflictException("VersionConflict"),
            StatusCode = StatusCodes.Status409Conflict,
            Code = BenchmarkErrorCode.VersionConflict,
            Detail = "The resource version changed. Refresh and retry.",
            Title = "Conflict",
            ProblemType = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            ForbiddenProviderText = null
        },
        new()
        {
            Name = "unknown conflict fallback",
            Exception = new BenchmarkConflictException("unsafe-provider-conflict"),
            StatusCode = StatusCodes.Status409Conflict,
            Code = BenchmarkErrorCode.InvalidLifecycleTransition,
            Detail = "The benchmark lifecycle transition is not allowed.",
            Title = "Conflict",
            ProblemType = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            ForbiddenProviderText = "unsafe-provider-conflict"
        },
        new()
        {
            Name = "eligibility",
            Exception = new BenchmarkEligibilityException("The selected model is not eligible for benchmarking."),
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            Code = BenchmarkErrorCode.IneligibleModel,
            Detail = "The selected model is not eligible for benchmarking.",
            Title = "Unprocessable Entity",
            ProblemType = "https://tools.ietf.org/html/rfc4918#section-11.2",
            ForbiddenProviderText = null
        },
        new()
        {
            Name = "unsupported KV cache",
            Exception = new BenchmarkUnsupportedKvCacheTypeException("The selected KV cache type is not supported."),
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            Code = BenchmarkErrorCode.UnsupportedKvCacheType,
            Detail = "The selected KV cache type is not supported.",
            Title = "Unprocessable Entity",
            ProblemType = "https://tools.ietf.org/html/rfc4918#section-11.2",
            ForbiddenProviderText = null
        },
        new()
        {
            Name = "judge policy changed",
            Exception = new BenchmarkJudgePolicyChangedException("unsafe-provider-revision-detail"),
            StatusCode = StatusCodes.Status409Conflict,
            Code = BenchmarkErrorCode.JudgePolicyChanged,
            Detail = "The project's judge policy changed. Refresh and retry.",
            Title = "Conflict",
            ProblemType = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            ForbiddenProviderText = "unsafe-provider-revision-detail"
        }
    ];

    private static readonly FallthroughCase[] FallthroughCases =
    [
        new()
        {
            Name = "contextual KeyNotFoundException",
            Exception = new KeyNotFoundException("contextual")
        },
        new()
        {
            Name = "unrelated InvalidOperationException",
            Exception = new InvalidOperationException("unrelated")
        }
    ];

    private sealed record BenchmarkHandlerCase
    {
        public required string Name { get; init; }

        public required Exception Exception { get; init; }

        public required int StatusCode { get; init; }

        public required BenchmarkErrorCode Code { get; init; }

        public required string Detail { get; init; }

        public required string Title { get; init; }

        public required string ProblemType { get; init; }

        public required string? ForbiddenProviderText { get; init; }
    }

    private sealed record FallthroughCase
    {
        public required string Name { get; init; }

        public required Exception Exception { get; init; }
    }

    private sealed record HandlerResult
    {
        public required bool Handled { get; init; }

        public required int StatusCode { get; init; }

        public required string ContentType { get; init; }

        public required string Body { get; init; }
    }
}
