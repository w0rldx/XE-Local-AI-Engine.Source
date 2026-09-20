namespace XE_Local_AI_Engine.Tests.Endpoints.AgentHome.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The operator's review-and-land surface, with the service substituted: what these routes owe is the
///     STATUS-CODE contract — 200 for an informational refusal, 404 for a run with no patch, 409 for a refused
///     apply, hash mismatch included.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentHomePatchEndpointTests
{
    private const string Root = "/api/local/v1/agent-home/runs";

    private const string RunId = "run-1758300000000-1";

    private const string Preview = $"{Root}/{RunId}/patch/preview";

    private const string Apply = $"{Root}/{RunId}/patch/apply";

    // Carries hex LETTERS on purpose, so the uppercase-rendering test below is not comparing a string to itself.
    private const string PatchHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    [Arguments("POST", Preview)]
    [Arguments("POST", Apply)]
    public async Task Route_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = NewFactory(new StubPatchApplyService());
        using var client = factory.CreateClient();
        using var request = Request(method, route, ApplyBody(PatchHash));

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode,
            $"{method} {route} writes to the operator's own folders; it must require the operator token.");
    }

    [Test]
    [Arguments("POST", Preview)]
    [Arguments("POST", Apply)]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        await using var factory = NewFactory(new StubPatchApplyService());
        using var client = factory.CreateClient();
        using var request = Request(method, route, ApplyBody(PatchHash));
        factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode,
            $"{method} {route} is operator-only. An MCP or proxy principal is a strictly lesser one and must never reach it.");
    }

    [Test]
    public async Task Preview_WithAPatchThatChecksClean_Answers200WithThePlanAndTheHash()
    {
        var service = new StubPatchApplyService
        {
            PreviewResult = new NodePatchApplyPreview
            {
                CanApply = true,
                Files =
                [
                    new PatchApplyFileEntry
                    {
                        Alias = "repo-01",
                        RelativePath = "src/App.cs",
                        ChangeType = "modified",
                        Added = 3,
                        Removed = 1
                    }
                ],
                Rejections = [],
                PatchSha256 = PatchHash
            }
        };

        using var document = await SendJsonAsync(service, "POST", Preview, body: null, HttpStatusCode.OK);

        AssertEx.True(document.RootElement.GetProperty("canApply").GetBoolean());
        AssertEx.Equal(PatchHash, document.RootElement.GetProperty("patchSha256").GetString());
        var file = document.RootElement.GetProperty("files")[0];
        AssertEx.Equal("repo-01", file.GetProperty("alias").GetString());
        AssertEx.Equal("src/App.cs", file.GetProperty("relativePath").GetString());
        AssertEx.Equal(expected: 3, file.GetProperty("added").GetInt32());
    }

    /// <summary>
    ///     A patch that will not apply is still an ANSWER, not an error: "these files conflict" is what the operator
    ///     asked. Only "there is no patch" is a 404.
    /// </summary>
    [Test]
    public async Task Preview_WhenThePatchCannotApply_Answers200CarryingTheRejections()
    {
        var service = new StubPatchApplyService
        {
            PreviewResult = new NodePatchApplyPreview
            {
                CanApply = false,
                Files = [],
                Rejections = ["alias 'repo-01': patch does not apply cleanly (error: patch failed)"],
                ContainsBinary = true,
                PatchSha256 = PatchHash
            }
        };

        using var document = await SendJsonAsync(service, "POST", Preview, body: null, HttpStatusCode.OK);

        AssertEx.False(document.RootElement.GetProperty("canApply").GetBoolean());
        AssertEx.True(document.RootElement.GetProperty("containsBinary").GetBoolean());
        AssertEx.Equal(expected: 1, document.RootElement.GetProperty("rejections").GetArrayLength());
    }

    [Test]
    [Arguments("POST", Preview)]
    [Arguments("POST", Apply)]
    public async Task Route_WhenTheRunHasNoExportedPatch_Answers404(string method, string route)
    {
        var service = new StubPatchApplyService
        {
            PreviewResult = new NodePatchApplyPreview
            {
                CanApply = false,
                Files = [],
                Rejections = ["no exported patch is available for this run."],
                PatchMissing = true
            },
            ApplyResult = new NodePatchApplyResult
            {
                Applied = false,
                AppliedFiles = [],
                Rejections = ["no exported patch is available for this run."],
                PatchMissing = true
            }
        };

        await using var factory = NewFactory(service);
        using var client = factory.CreateClient();
        using var request = Request(method, route, ApplyBody(PatchHash));
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} on a run with nothing to land is a 404.");
    }

    [Test]
    public async Task Apply_WhenThePatchApplies_Answers200WithTheFilesThatLanded()
    {
        var service = new StubPatchApplyService
        {
            ApplyResult = new NodePatchApplyResult
            {
                Applied = true,
                AppliedFiles =
                [
                    new PatchApplyFileEntry
                    {
                        Alias = "repo-01",
                        RelativePath = "src/App.cs",
                        ChangeType = "modified",
                        Added = 3,
                        Removed = 1
                    }
                ],
                Rejections = []
            }
        };

        using var document = await SendJsonAsync(service, "POST", Apply, ApplyBody(PatchHash), HttpStatusCode.OK);

        AssertEx.Equal("src/App.cs", document.RootElement.GetProperty("appliedFiles")[0].GetProperty("relativePath").GetString());
        AssertEx.Equal(PatchHash, service.LastRequest?.ExpectedPatchSha256,
            "the hash the caller approved must reach the service; without it the apply is unbound and the 409 below can never happen.");
    }

    [Test]
    public async Task Apply_WhenTheServiceRejects_Answers409WithTheRedactedReasons()
    {
        var service = new StubPatchApplyService
        {
            ApplyResult = new NodePatchApplyResult
            {
                Applied = false,
                AppliedFiles = [],
                Rejections = ["alias 'repo-01': a target path escapes the folder root."]
            }
        };

        using var document = await SendJsonAsync(service, "POST", Apply, ApplyBody(PatchHash), HttpStatusCode.Conflict);

        AssertEx.Contains(document.RootElement.ToString(), "escapes the folder root");
    }

    /// <summary>
    ///     The binding, over HTTP: the bytes on disk no longer hash to what the preview reported, so nothing is
    ///     written and the caller is told to look again rather than being handed a success for a diff it never read.
    /// </summary>
    [Test]
    public async Task Apply_WhenTheHashNoLongerMatches_Answers409()
    {
        var service = new StubPatchApplyService
        {
            ApplyResult = new NodePatchApplyResult
            {
                Applied = false,
                AppliedFiles = [],
                Rejections = ["the exported patch changed since it was previewed."]
            }
        };

        using var document = await SendJsonAsync(service, "POST", Apply, ApplyBody(PatchHash), HttpStatusCode.Conflict);

        AssertEx.Contains(document.RootElement.ToString(), "changed since it was previewed");
    }

    /// <summary>
    ///     A half-landed apply is a refusal too, but the operator has to be told WHICH folders already moved. The
    ///     named error is what lets a client say so without reading prose.
    /// </summary>
    [Test]
    public async Task Apply_WhenOnlyPartOfThePatchLanded_Answers409NamingWhatWasWritten()
    {
        var service = new StubPatchApplyService
        {
            ApplyResult = new NodePatchApplyResult
            {
                Applied = false,
                AppliedFiles =
                [
                    new PatchApplyFileEntry
                    {
                        Alias = "repo-01",
                        RelativePath = "src/App.cs",
                        ChangeType = "modified"
                    }
                ],
                Rejections = ["alias 'repo-02': apply failed after a clean check (git rejected the patch.)"],
                PartiallyApplied = true
            }
        };

        using var document = await SendJsonAsync(service, "POST", Apply, ApplyBody(PatchHash), HttpStatusCode.Conflict);

        var body = document.RootElement.ToString();
        AssertEx.Contains(body, "partiallyApplied");
        AssertEx.Contains(body, "repo-01/src/App.cs");
    }

    [Test]
    // Every id here stays ONE path segment: a `..` or an escaped slash is normalized before routing sees it, so it
    // would answer 404 from the route table and prove nothing about the validator.
    [Arguments("-run", "a run id that could read as a command-line option is refused")]
    [Arguments("run.id", "a run id with a path-significant character is refused")]
    [Arguments("run%20id", "a run id with a space is refused")]
    public async Task Preview_WithAMalformedRunId_Answers400(string runId, string why)
    {
        var service = new StubPatchApplyService();
        await using var factory = NewFactory(service);
        using var client = factory.CreateClient();
        using var request = Request("POST", $"{Root}/{runId}/patch/preview", body: null);
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, why);
        AssertEx.Equal(expected: 0, service.PreviewCalls, "a malformed run id must not reach the service at all.");
    }

    [Test]
    [Arguments("", "an apply with no hash is unbound and must be refused")]
    [Arguments("not-a-hash", "an apply with a hash that never came from a preview is refused")]
    [Arguments("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefAA", "an over-long hash is refused")]
    [Arguments("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", "a 63-character hash is refused")]
    public async Task Apply_WithoutThePreviewHash_Answers400(string hash, string why)
    {
        var service = new StubPatchApplyService();
        await using var factory = NewFactory(service);
        using var client = factory.CreateClient();
        using var request = Request("POST", Apply, ApplyBody(hash));
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, why);
        AssertEx.Equal(expected: 0, service.ApplyCalls, "an unbound apply must not reach the service at all.");
    }

    /// <summary>
    ///     An apply body that OMITS the member rather than sending a bad value: the DTO's `required` modifier
    ///     refuses it through System.Text.Json, a different path from the validator — and the one a generated client
    ///     predating the field takes.
    /// </summary>
    [Test]
    public async Task Apply_WithNoPatchHashProperty_Answers400()
    {
        var service = new StubPatchApplyService();
        await using var factory = NewFactory(service);
        using var client = factory.CreateClient();
        using var request = Request("POST", Apply, "{}");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode,
            "an apply body with no patchSha256 member at all is unbound and must be refused.");
        AssertEx.Equal(expected: 0, service.ApplyCalls, "an unbound apply must not reach the service at all.");
    }

    /// <summary>
    ///     The service compares the hash case-insensitively, so the edge must accept what the comparison accepts. A
    ///     caller refused here for an uppercase rendering would be refused on a distinction the product does not make.
    /// </summary>
    [Test]
    public async Task Apply_WithAnUppercaseHash_IsAcceptedAndReachesTheService()
    {
        var service = new StubPatchApplyService();
        var upper = PatchHash.ToUpperInvariant();

        using var document = await SendJsonAsync(service, "POST", Apply, ApplyBody(upper), HttpStatusCode.OK);

        AssertEx.NotEqual(PatchHash, upper, "the fixture hash must contain hex letters, or this proves nothing.");
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("appliedFiles").GetArrayLength());
        AssertEx.Equal(upper, service.LastRequest?.ExpectedPatchSha256,
            "the hash must reach the service unchanged; the case-insensitive comparison is the service's own.");
    }

    private static string ApplyBody(string hash) =>
        JsonSerializer.Serialize(new
        {
            patchSha256 = hash
        });

    private static async Task<JsonDocument> SendJsonAsync(StubPatchApplyService service,
        string method,
        string route,
        string? body,
        HttpStatusCode expected)
    {
        await using var factory = NewFactory(service);
        using var client = factory.CreateClient();
        using var request = Request(method, route, body);
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(expected, response.StatusCode, $"{method} {route} answered {(int)response.StatusCode}: {payload}");
        return JsonDocument.Parse(payload);
    }

    private static TestServerWebAppFactory NewFactory(INodePatchApplyService service) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodePatchApplyService>();
                services.AddScoped(_ => service);
            }
        };

    private static HttpRequestMessage Request(string method, string route, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json")
        };
        return request;
    }

    /// <summary>
    ///     The service, stubbed to the answer each test needs and recording what reached it. Hand-written because
    ///     two assertions are about the REQUEST the endpoint composed, easier to read off a field than a call log.
    /// </summary>
    private sealed class StubPatchApplyService : INodePatchApplyService
    {
        public NodePatchApplyPreview PreviewResult { get; init; } = new()
        {
            CanApply = true,
            Files = [],
            Rejections = [],
            PatchSha256 = PatchHash
        };

        public NodePatchApplyResult ApplyResult { get; init; } = new()
        {
            Applied = true,
            AppliedFiles = [],
            Rejections = []
        };

        public NodePatchApplyRequest? LastRequest { get; private set; }

        public int PreviewCalls { get; private set; }

        public int ApplyCalls { get; private set; }

        public Task<NodePatchApplyPreview> PreviewAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            LastRequest = request;
            return Task.FromResult(PreviewResult);
        }

        public Task<NodePatchApplyResult> ApplyApprovedAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            LastRequest = request;
            return Task.FromResult(ApplyResult);
        }
    }
}
