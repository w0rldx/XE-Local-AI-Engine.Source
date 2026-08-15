namespace XE_Local_AI_Engine.Tests.Endpoints.Knowledge;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins which repository-import failures are the caller's problem and which are the host's. The endpoint used to
///     catch a bare <see cref="InvalidOperationException" />, which meant an unreadable Git index or a file that could
///     not be opened came back as a 400 carrying an I/O message — a client error the client could do nothing about.
///     Now only <see cref="KnowledgeRepositoryImportRejectedException" /> (and the folder/argument rejections) are 400;
///     <see cref="KnowledgeRepositoryReadException" /> falls through to the global handler as a 500.
/// </summary>
public sealed class ImportKnowledgeRepositoryEndpointTests
{
    private const string ImportRoute = "/api/local/v1/knowledge-base/repositories/import";

    [Test]
    public async Task Import_WhenTheRepositoryIsPastAnImportBound_ReturnsBadRequest()
    {
        using var response = await ImportWithFailingServiceAsync(
                                       new KnowledgeRepositoryImportRejectedException("The repository contains more supported files than one import permits."))
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Contains(body, "more supported files than one import permits", StringComparison.Ordinal);
    }

    [Test]
    public async Task Import_WhenTheSelectedFolderInputIsRejected_ReturnsBadRequest()
    {
        using var response = await ImportWithFailingServiceAsync(
                                       new SelectedFolderValidationException("The selected folder id is not a valid identifier."))
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Import_WhenTheHostCannotReadTheRepository_StaysAServerError()
    {
        using var response = await ImportWithFailingServiceAsync(
                                       new KnowledgeRepositoryReadException("The registered repository file index could not be read."))
                                   .ConfigureAwait(false);

        // 500, not a 400 dressed up with an I/O message. (The message still reaches the body here because the test
        // host runs as a development environment; DefaultExceptionHandler replaces it in production.)
        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Test]
    public async Task Import_WhenAFileResolvesOutsideTheRepositoryRoot_StaysAServerError()
    {
        // A path escape is a security stop, not a request the operator can correct — it must never be echoed as a 400.
        using var response = await ImportWithFailingServiceAsync(
                                       new UnauthorizedAccessException("A repository file resolved outside the registered repository root."))
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ImportWithFailingServiceAsync(Exception failure)
    {
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IKnowledgeRepositoryImportService>();
                services.AddSingleton<IKnowledgeRepositoryImportService>(new ThrowingRepositoryImportService(failure));
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ImportRoute)
        {
            Content = JsonContent.Create(new
            {
                selectedFolderId = Guid.NewGuid()
            })
        };
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private sealed class ThrowingRepositoryImportService(Exception failure) : IKnowledgeRepositoryImportService
    {
        public Task<KnowledgeRepositoryImportResult> ImportAsync(Guid selectedFolderId,
            string? collectionId,
            CancellationToken cancellationToken) =>
            Task.FromException<KnowledgeRepositoryImportResult>(failure);
    }
}
