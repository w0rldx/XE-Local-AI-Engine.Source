namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class TrainingExceptionHandlerTests
{
    [Test]
    public async Task TryHandleAsync_ClaimedTrainingExceptions_WriteTheirExistingEnvelopes()
    {
        foreach (var testCase in ClaimedCases)
        {
            var result = await HandleAsync(testCase.Exception);

            AssertEx.True(result.Handled, $"{testCase.Name} must be claimed by the Training handler.");
            AssertEx.Equal(testCase.StatusCode, result.StatusCode, testCase.Name);
            AssertEx.Equal("application/json; charset=utf-8", result.ContentType, testCase.Name);
            using var document = JsonDocument.Parse(result.Body);
            AssertEx.Equal(expected: 2, document.RootElement.EnumerateObject().Count(), testCase.Name);
            AssertEx.Equal(testCase.Code.ToString(), document.RootElement.GetProperty("code").GetString(), testCase.Name);
            AssertEx.Equal(testCase.Message, document.RootElement.GetProperty("message").GetString(), testCase.Name);
            AssertEx.False(document.RootElement.TryGetProperty("traceId", out _),
                $"{testCase.Name} must preserve the trace-free Training wire envelope.");
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

            AssertEx.False(result.Handled, $"{testCase.Name} must not be claimed by the Training handler.");
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

        var handled = await new TrainingExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None);
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

    private static readonly TrainingHandlerCase[] ClaimedCases =
    [
        new()
        {
            Name = "not found",
            Exception = new TrainingNotFoundException("unsafe-storage-path:/training/private"),
            StatusCode = StatusCodes.Status404NotFound,
            Code = TrainingErrorCode.NotFound,
            Message = "The requested training resource was not found.",
            ForbiddenProviderText = "unsafe-storage-path"
        },
        new()
        {
            Name = "validation",
            Exception = new TrainingValidationException("The training request is invalid."),
            StatusCode = StatusCodes.Status400BadRequest,
            Code = TrainingErrorCode.InvalidRequest,
            Message = "The training request is invalid.",
            ForbiddenProviderText = null
        },
        new()
        {
            Name = "known conflict",
            Exception = new TrainingConflictException("TrainingBusy"),
            StatusCode = StatusCodes.Status409Conflict,
            Code = TrainingErrorCode.TrainingBusy,
            Message = "Training, an evaluation or an export holds the GPU; dataset generation cannot start until it finishes.",
            ForbiddenProviderText = null
        },
        new()
        {
            Name = "unknown conflict fallback",
            Exception = new TrainingConflictException("unsafe-provider-conflict"),
            StatusCode = StatusCodes.Status409Conflict,
            Code = TrainingErrorCode.InvalidLifecycleTransition,
            Message = "The training lifecycle transition is not allowed.",
            ForbiddenProviderText = "unsafe-provider-conflict"
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

    private sealed record TrainingHandlerCase
    {
        public required string Name { get; init; }

        public required Exception Exception { get; init; }

        public required int StatusCode { get; init; }

        public required TrainingErrorCode Code { get; init; }

        public required string Message { get; init; }

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
