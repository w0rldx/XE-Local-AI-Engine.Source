namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the three per-area <c>*EndpointSupport</c> mappers after they were promoted to registered
///     <c>IExceptionHandler</c>s: the status each exception TYPE carries, and — for the selected-folder family, whose
///     two specific types derive from the aggregate — that the arm order still keeps three statuses apart instead of
///     flattening them. Each handler must also decline what it does not name, or an unrelated fault would answer 4xx
///     instead of the 500 that says something is broken.
/// </summary>
public sealed class PromotedEndpointSupportHandlerTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task SelectedFolderHandler_AnswersEachTypeWithTheStatusItsOwnEndpointsUsedToSend()
    {
        var notFound = await HandleAsync(new SelectedFolderExceptionHandler(),
                new SelectedFolderNotFoundException("No selected folder with that id is registered."))
            .ConfigureAwait(false);
        AssertEx.Equal(expected: 404, notFound.StatusCode);
        AssertEx.Equal(expected: 0L, notFound.BodyLength, "the selected-folder 404 has always been bodyless.");

        var conflict = await HandleAsync(new SelectedFolderExceptionHandler(),
                new SelectedFolderConflictException("That alias is already registered to another folder."))
            .ConfigureAwait(false);
        AssertEx.Equal(expected: 409, conflict.StatusCode);
        AssertEx.Equal("That alias is already registered to another folder.", conflict.Detail);

        var validation = await HandleAsync(new SelectedFolderExceptionHandler(),
                new SelectedFolderValidationException("The selected folder is not a Git repository root."))
            .ConfigureAwait(false);
        AssertEx.Equal(expected: 400, validation.StatusCode);
        AssertEx.Equal("The selected folder is not a Git repository root.", validation.Detail);
    }

    /// <summary>
    ///     Both specific types derive from the aggregate, so a switch that matched the base first would answer all
    ///     three 400. That is exactly why the family is kept out of <c>DomainValidationExceptionHandler</c>, and this
    ///     is the assertion that would catch the arms being reordered.
    /// </summary>
    [Test]
    public async Task SelectedFolderHandler_MatchesTheDerivedTypesBeforeTheirBase()
    {
        var derivedStatuses = new List<int>();
        foreach (Exception exception in new SelectedFolderValidationException[]
                 {
                     new SelectedFolderNotFoundException("unknown id"),
                     new SelectedFolderConflictException("alias in use")
                 })
        {
            derivedStatuses.Add((await HandleAsync(new SelectedFolderExceptionHandler(), exception).ConfigureAwait(false)).StatusCode);
        }

        AssertEx.Equal("404,409", string.Join(',', derivedStatuses),
            "the derived selected-folder types must be matched before the aggregate they inherit from.");
    }

    [Test]
    public async Task SelectedFolderHandler_ForAnUnrelatedException_DeclinesToHandleIt()
    {
        var context = NewContext();
        var handled = await new SelectedFolderExceptionHandler()
                            .TryHandleAsync(context, new InvalidOperationException("something else"), CancellationToken.None)
                            .ConfigureAwait(false);

        AssertEx.False(handled);
    }

    /// <summary>
    ///     The import mapper's status split, which used to be repeated as a catch in both import endpoints. Every arm
    ///     is asserted because the codes are the contract the SPA's import flow branches on.
    /// </summary>
    [Test]
    [Arguments("ModelConflict", 409)]
    [Arguments("DestinationConflict", 409)]
    [Arguments("AcquisitionAlreadyActive", 409)]
    [Arguments("OperationNotFound", 404)]
    [Arguments("InsufficientStorage", 507)]
    [Arguments("SomethingElse", 400)]
    public async Task GgufImportHandler_KeepsTheStatusTheSharedMapperAlwaysReturned(string errorCode, int expectedStatus)
    {
        var context = NewContext();
        var handled = await new GgufImportExceptionHandler()
                            .TryHandleAsync(context, new GgufImportApplicationException(errorCode, "sanitized"), CancellationToken.None)
                            .ConfigureAwait(false);

        AssertEx.True(handled, errorCode);
        AssertEx.Equal(expectedStatus, context.Response.StatusCode, errorCode);
    }

    [Test]
    public async Task GgufImportHandler_ForAnExceptionOutsideTheImportFamily_DeclinesToHandleIt()
    {
        var context = NewContext();
        var handled = await new GgufImportExceptionHandler()
                            .TryHandleAsync(context, new GgufAcquisitionConflictException(), CancellationToken.None)
                            .ConfigureAwait(false);

        AssertEx.False(handled, "the download family has its own handler; claiming it here would answer the wrong body.");
    }

    [Test]
    public async Task GgufDownloadHandler_KeepsTheConflictStatusTheSharedMapperReturned()
    {
        var context = NewContext();
        var handled = await new GgufDownloadExceptionHandler()
                            .TryHandleAsync(context, new GgufAcquisitionConflictException(), CancellationToken.None)
                            .ConfigureAwait(false);

        AssertEx.True(handled);
        AssertEx.Equal(expected: 409, context.Response.StatusCode);
    }

    [Test]
    public async Task GgufDownloadHandler_ForAnExceptionOutsideItsNarrowedFamily_DeclinesToHandleIt()
    {
        var context = NewContext();
        var handled = await new GgufDownloadExceptionHandler()
                            .TryHandleAsync(context, new TimeoutException("the probe timed out"), CancellationToken.None)
                            .ConfigureAwait(false);

        AssertEx.False(handled, "only the narrowed acquisition/Hugging Face family was ever mapped; the rest is a 500.");
    }

    private DefaultHttpContext NewContext() =>
        new()
        {
            TraceIdentifier = "trace-" + Guid.NewGuid().ToString("N"),
            RequestServices = Factory.Services,
            Request =
            {
                Path = "/api/local/v1/promoted"
            },
            Response =
            {
                Body = new MemoryStream()
            }
        };

    private async Task<HandledResponse> HandleAsync(SelectedFolderExceptionHandler handler, Exception exception)
    {
        var context = NewContext();

        AssertEx.True(await handler.TryHandleAsync(context, exception, CancellationToken.None).ConfigureAwait(false),
            exception.GetType().Name);

        var length = context.Response.Body.Length;
        string? detail = null;
        if (length > 0)
        {
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body).ConfigureAwait(false);
            detail = document.RootElement.GetProperty("detail").GetString();
        }

        return new HandledResponse(context.Response.StatusCode, length, detail);
    }

    private sealed record HandledResponse(int StatusCode, long BodyLength, string? Detail);
}
