namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class TrainingExceptionHandlerTests
{
    [Test]
    public async Task TryHandleAsync_ClaimedTrainingExceptions_WriteTheirExistingEnvelopes()
    {
        foreach (var testCase in ClaimedCases)
        {
            var result = await HandleAsync(testCase.Exception).ConfigureAwait(false);

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
            var result = await HandleAsync(testCase.Exception).ConfigureAwait(false);

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

        var handled = await new TrainingExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false);
        body.Position = 0;
        using var reader = new StreamReader(body);
        return new HandlerResult(handled,
            context.Response.StatusCode,
            context.Response.ContentType ?? string.Empty,
            await reader.ReadToEndAsync().ConfigureAwait(false));
    }

    private static readonly TrainingHandlerCase[] ClaimedCases =
    [
        new("not found",
            new TrainingNotFoundException("unsafe-storage-path:/training/private"),
            StatusCodes.Status404NotFound,
            TrainingErrorCode.NotFound,
            "The requested training resource was not found.",
            "unsafe-storage-path"),
        new("validation",
            new TrainingValidationException("The training request is invalid."),
            StatusCodes.Status400BadRequest,
            TrainingErrorCode.InvalidRequest,
            "The training request is invalid.",
            ForbiddenProviderText: null),
        new("known conflict",
            new TrainingConflictException("TrainingBusy"),
            StatusCodes.Status409Conflict,
            TrainingErrorCode.TrainingBusy,
            "Training, an evaluation or an export holds the GPU; dataset generation cannot start until it finishes.",
            ForbiddenProviderText: null),
        new("unknown conflict fallback",
            new TrainingConflictException("unsafe-provider-conflict"),
            StatusCodes.Status409Conflict,
            TrainingErrorCode.InvalidLifecycleTransition,
            "The training lifecycle transition is not allowed.",
            "unsafe-provider-conflict")
    ];

    private static readonly FallthroughCase[] FallthroughCases =
    [
        new("contextual KeyNotFoundException", new KeyNotFoundException("contextual")),
        new("unrelated InvalidOperationException", new InvalidOperationException("unrelated"))
    ];

    private sealed record TrainingHandlerCase(
        string Name,
        Exception Exception,
        int StatusCode,
        TrainingErrorCode Code,
        string Message,
        string? ForbiddenProviderText);

    private sealed record FallthroughCase(string Name, Exception Exception);

    private sealed record HandlerResult(bool Handled, int StatusCode, string ContentType, string Body);
}
