namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;

using System.Net;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The instance surface: the list, the install, the instance read and the variables write. The assertions are about
///     what the operator is shown and what the node keeps to itself — the installed snapshot rather than the catalog's
///     current manifest, and never a secret in either direction.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppInstanceEndpointTests
{
    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.Instances)]
    [Arguments("POST", ExternalAppEndpointPayloads.Instances)]
    [Arguments("GET", ExternalAppEndpointPayloads.Instance)]
    [Arguments("DELETE", ExternalAppEndpointPayloads.Instance)]
    [Arguments("PUT", ExternalAppEndpointPayloads.InstanceVariables)]
    public async Task Route_WithoutAToken_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = Factory(Substitute.For<IExternalAppService>());

        using var response = await ExternalAppEndpointPayloads.SendAnonymousAsync(factory, method, route);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require a token.");
    }

    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.Instances)]
    [Arguments("POST", ExternalAppEndpointPayloads.Instances)]
    [Arguments("GET", ExternalAppEndpointPayloads.Instance)]
    [Arguments("DELETE", ExternalAppEndpointPayloads.Instance)]
    [Arguments("PUT", ExternalAppEndpointPayloads.InstanceVariables)]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        await using var factory = Factory(Substitute.For<IExternalAppService>());

        using var response = await ExternalAppEndpointPayloads.SendAsNonOperatorAsync(factory, method, route);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {route} is operator-only.");
    }

    /// <summary>
    ///     The degraded projection has to survive the mapper too. An empty manifest with no services and no variables
    ///     is what a row whose stored snapshot could not be read is projected as, and the list must answer 200 with
    ///     both rows rather than fail on the one the service could not read.
    /// </summary>
    [Test]
    public async Task ListInstances_WithARowWhoseManifestCouldNotBeRead_StillAnswersOkWithBothRows()
    {
        var degraded = new ExternalAppInstanceDetail(ExternalAppEndpointPayloads.Summary() with
            {
                FailureCategory = ExternalAppFailureCategory.Unknown,
                FailureSummary = "The stored manifest for this application could not be read."
            },
            new ApplicationManifest("corrupt-app",
                1,
                string.Empty,
                "Corrupt App",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                new ApplicationPermissions(Internet: false, LocalNetwork: false, "none", "none"),
                new ApplicationResources(0, 0, 0, 0),
                [],
                []),
            TestedVersion: null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            "docker",
            RuntimeOverride: null,
            "/var/lib/xe/external-apps/corrupt",
            1,
            NeedsRecreate: false,
            1_779_000_000_000L,
            StartedAtUtc: null,
            StoppedAtUtc: null);

        var apps = Substitute.For<IExternalAppService>();
        apps.ListDetailsAsync(Arg.Any<CancellationToken>()).Returns([ExternalAppEndpointPayloads.Detail(), degraded]);

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Instances);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = document.RootElement.GetProperty("items");
        AssertEx.Equal(expected: 2, items.GetArrayLength(), "The healthy row is served alongside the degraded one.");
        AssertEx.Equal("The stored manifest for this application could not be read.",
            items[1].GetProperty("failureSummary").GetString());
        AssertEx.Equal(expected: 0, items[1].GetProperty("manifest").GetProperty("services").GetArrayLength());
    }

    /// <summary>
    ///     The list row is the FULL view: the Apps page renders an Open target, a published port and the runtime
    ///     provider straight off it. A summary row would force a GET per card before the page could show an address,
    ///     and the store reads the whole row either way.
    /// </summary>
    [Test]
    public async Task ListInstances_ProjectsFullInstanceViews()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListDetailsAsync(Arg.Any<CancellationToken>())
            .Returns([ExternalAppEndpointPayloads.Detail(ExternalAppEndpointPayloads.Summary(updateAvailable: true))]);

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Instances);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = document.RootElement.GetProperty("items")[0];
        AssertEx.Equal(ExternalAppEndpointPayloads.InstanceIdLiteral, item.GetProperty("id").GetString());
        AssertEx.Equal("Running", item.GetProperty("status").GetString());
        AssertEx.Equal(7, item.GetProperty("version").GetInt64());
        AssertEx.True(item.GetProperty("updateAvailable").GetBoolean());
        AssertEx.Equal(3, item.GetProperty("availableManifestVersion").GetInt32());
        AssertEx.Equal("docker", item.GetProperty("runtimeProvider").GetString());
        AssertEx.Equal("1.4.0", item.GetProperty("manifest").GetProperty("testedVersion").GetString(), "the INSTALLED snapshot, not the catalog's.");

        var openTarget = item.GetProperty("publishedPorts")
                             .EnumerateArray()
                             .Single(static port => port.GetProperty("url").ValueKind != JsonValueKind.Null);
        AssertEx.Equal("http://127.0.0.1:18080/app", openTarget.GetProperty("url").GetString(), "the card opens this without a second read.");
        AssertEx.Equal(ExternalAppVariableMask.Value,
            item.GetProperty("variables").GetProperty(ExternalAppEndpointPayloads.SecretVariableName).GetString(),
            "a list carries the masked values, never a stored secret.");
    }

    /// <summary>
    ///     202 rather than 201 or 200: admission is synchronous and the pull, the create and the start run afterwards,
    ///     so the body is the row as admission left it and never the outcome.
    /// </summary>
    [Test]
    public async Task Install_ReturnsAcceptedWithTheAdmittedRow()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.InstallAsync(Arg.Any<InstallCommand>(), Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Summary(ExternalAppInstanceStatus.Installing, version: 1));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody());
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertEx.Equal("Installing", document.RootElement.GetProperty("status").GetString(), "the admitted status is transient.");
        AssertEx.Equal(1, document.RootElement.GetProperty("version").GetInt64());

        var command = apps.ReceivedCalls()
                          .Single(static call => call.GetMethodInfo().Name == nameof(IExternalAppService.InstallAsync))
                          .GetArguments()[0] as InstallCommand;
        var admitted = AssertEx.NotNull(command);
        AssertEx.Equal(ExternalAppEndpointPayloads.ManifestSha256, admitted.ManifestSha256, "the fingerprint reaches the service unchanged.");
        AssertEx.True(admitted.AcceptPermissions);
    }

    /// <summary>Consent is never inferred from a caller having sent the rest of the form.</summary>
    [Test]
    public async Task Install_WithoutAcceptingThePermissions_ReturnsBadRequestNamingTheField()
    {
        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody(acceptPermissions: false));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "acceptPermissions", StringComparison.Ordinal);
        AssertEx.Empty(apps.ReceivedCalls());
    }

    [Test]
    [Arguments("not a variable name")]
    [Arguments("9LEADING_DIGIT")]
    public async Task Install_WithAnUnusableVariableKey_ReturnsBadRequestNamingTheKeyOnly(string key)
    {
        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody(variables: new Dictionary<string, string>(StringComparer.Ordinal)
                                       {
                                           [key] = "value"
                                       }));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, key, StringComparison.Ordinal);
        AssertEx.False(body.Contains("\"value\"", StringComparison.Ordinal), "a rejected variable's VALUE never reaches the body.");
        AssertEx.Empty(apps.ReceivedCalls());
    }

    /// <summary>
    ///     A 400 body is logged and the variables map carries the application's admin password, so the message names
    ///     the KEY and the value never appears.
    /// </summary>
    [Test]
    public async Task Install_WithAnOverlongSecret_DoesNotEchoTheSubmittedValue()
    {
        var secret = new string('s', 5000);

        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody(variables: new Dictionary<string, string>(StringComparer.Ordinal)
                                       {
                                           [ExternalAppEndpointPayloads.SecretVariableName] = secret
                                       }));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, ExternalAppEndpointPayloads.SecretVariableName, StringComparison.Ordinal);
        AssertEx.False(body.Contains(secret, StringComparison.Ordinal), "the submitted value is what the user was asked to keep secret.");
    }

    /// <summary>
    ///     The 400 names the key it refused, and a 400 body is logged, so a caller that posts a ten-kilobyte key must
    ///     not get ten kilobytes of it back in the log. The bound is the one the key grammar already carries.
    /// </summary>
    [Test]
    public async Task Install_WithAnOverlongVariableKey_TruncatesTheEchoedKey()
    {
        var key = new string('K', 10_240);

        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody(variables: new Dictionary<string, string>(StringComparer.Ordinal)
                                       {
                                           [key] = "value"
                                       }));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, key[..64], StringComparison.Ordinal, "the message still names what was refused.");
        AssertEx.False(body.Contains(key[..65], StringComparison.Ordinal), "and stops at the bound the key grammar carries.");
        AssertEx.Empty(apps.ReceivedCalls());
    }

    [Test]
    [Arguments("manifestVersion")]
    [Arguments("manifestSha256")]
    public async Task Install_WithoutAFingerprintMember_ReturnsBadRequest(string omitted)
    {
        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applicationId"] = ExternalAppEndpointPayloads.ApplicationId,
            ["manifestVersion"] = 2,
            ["manifestSha256"] = ExternalAppEndpointPayloads.ManifestSha256,
            ["acceptPermissions"] = true,
            ["variables"] = new Dictionary<string, string>(StringComparer.Ordinal)
        };
        _ = body.Remove(omitted);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.Instances, body);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"an install without {omitted} accepts nothing.");
        AssertEx.Empty(apps.ReceivedCalls());
    }

    /// <summary>V1 allows one instance per application, so a second install is refused by the install gate.</summary>
    [Test]
    public async Task Install_WhenTheApplicationAlreadyHasAnInstance_ReturnsConflict()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.InstallAsync(Arg.Any<InstallCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ =>
                throw new ExternalAppAlreadyInstalledException("Odysseus is already installed on this node."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody());
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppAlreadyInstalled", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>
    ///     The fingerprint is what binds the acceptance to what was read: a catalog that republishes v3 with a wider
    ///     capability set keeps its version number, so a stale sha and a stale version are the same refusal.
    /// </summary>
    [Test]
    [Arguments(2, ExternalAppEndpointPayloads.OtherManifestSha256)]
    [Arguments(1, ExternalAppEndpointPayloads.ManifestSha256)]
    public async Task Install_WhenTheManifestMovedUnderTheOperator_ReturnsManifestChanged(int manifestVersion, string manifestSha256)
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.InstallAsync(Arg.Any<InstallCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ =>
                throw new ExternalAppManifestChangedException("The catalog now serves a different manifest.",
                    2,
                    ExternalAppEndpointPayloads.ManifestSha256));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody(manifestVersion: manifestVersion, manifestSha256: manifestSha256));
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppManifestChanged", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    public async Task GetInstance_ForAnUnknownId_ReturnsNotFoundWithAnEmptyBody()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceDetail>(_ => throw new ExternalAppNotFoundException("No such instance."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Instance);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Empty(body);
    }

    /// <summary>
    ///     The detail page reads the INSTALLED snapshot and never the catalog. The fixture's instance runs v2 while the
    ///     catalog offers v3, and it is v2 that must render, sanitised.
    /// </summary>
    [Test]
    public async Task GetInstance_RendersTheSanitisedInstalledSnapshot()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.GetAsync(ExternalAppEndpointPayloads.InstanceId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Detail(ExternalAppEndpointPayloads.Summary(updateAvailable: true)));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Instance);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        AssertEx.Equal(2, root.GetProperty("manifestVersion").GetInt32());
        AssertEx.Equal(3, root.GetProperty("availableManifestVersion").GetInt32(), "the catalog offers a newer one, and the row says so.");
        AssertEx.Equal("docker", root.GetProperty("runtimeProvider").GetString());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("runtimeOverride").ValueKind, "null means the node-wide selection applies.");
        AssertEx.Equal(42, root.GetProperty("lastSequence").GetInt64());

        var manifest = root.GetProperty("manifest");
        AssertEx.Equal(2, manifest.GetProperty("manifestVersion").GetInt32(), "the INSTALLED version, not the catalog's.");
        AssertEx.Equal("1.4.0", manifest.GetProperty("testedVersion").GetString(), "testedVersion rides on the manifest, with no duplicate at the top.");
        AssertEx.False(root.TryGetProperty("testedVersion", out _), "one spelling only.");
        AssertEx.False(manifest.GetProperty("services")[0].TryGetProperty("files", out _));
        AssertEx.Equal(JsonValueKind.Null, manifest.GetProperty("variables")[0].GetProperty("default").ValueKind);
        AssertEx.False(body.Contains("contentBase64", StringComparison.Ordinal));
    }

    /// <summary>
    ///     At most one port per application carries an open path, so a non-null <c>url</c> identifies the Open target.
    ///     Selecting "the first published port" instead would land the Open button on the wrong service.
    /// </summary>
    [Test]
    public async Task GetInstance_ComposesTheUrlOnlyForThePortWithAnOpenPath()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.GetAsync(ExternalAppEndpointPayloads.InstanceId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Detail());

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.Instance);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        var ports = document.RootElement.GetProperty("publishedPorts");
        var open = ports.EnumerateArray().Single(static port => port.GetProperty("service").GetString() == "web");
        var plain = ports.EnumerateArray().Single(static port => port.GetProperty("service").GetString() == "worker");

        AssertEx.Equal("/app", open.GetProperty("openPath").GetString());
        AssertEx.Equal("http://127.0.0.1:18080/app", open.GetProperty("url").GetString());
        AssertEx.Equal(JsonValueKind.Null, plain.GetProperty("openPath").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, plain.GetProperty("url").ValueKind, "a published port without an open path is not an Open target.");
    }

    /// <summary>
    ///     The sentinel the instance read returns and the sentinel a write keeps a stored secret on are the SAME
    ///     symbol. Two constants is how the mismatch that silently overwrites a stored secret happens.
    /// </summary>
    [Test]
    public async Task Variables_MaskOnReadAndKeepTheStoredSecretOnTheSentinel()
    {
        IReadOnlyDictionary<string, string>? submitted = null;

        var apps = Substitute.For<IExternalAppService>();
        apps.ConfigureAsync(ExternalAppEndpointPayloads.InstanceId,
                Arg.Any<long>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                submitted = call.Arg<IReadOnlyDictionary<string, string>>();
                return Task.FromResult(ExternalAppEndpointPayloads.Detail());
            });

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "PUT",
                                       ExternalAppEndpointPayloads.InstanceVariables,
                                       new
                                       {
                                           expectedVersion = 7L,
                                           variables = new Dictionary<string, string>(StringComparer.Ordinal)
                                           {
                                               [ExternalAppEndpointPayloads.SecretVariableName] = ExternalAppVariableMask.Value,
                                               [ExternalAppEndpointPayloads.PlainVariableName] = "http://127.0.0.1:9999"
                                           }
                                       });
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "the write is complete when it returns, so it is 200 and not 202.");

        var kept = AssertEx.NotNull(submitted);
        AssertEx.Equal(ExternalAppVariableMask.Value,
            kept[ExternalAppEndpointPayloads.SecretVariableName],
            "the sentinel reaches the service verbatim, which is what means 'keep what is stored'.");

        var variables = document.RootElement.GetProperty("variables");
        AssertEx.Equal(ExternalAppVariableMask.Value,
            variables.GetProperty(ExternalAppEndpointPayloads.SecretVariableName).GetString(),
            "and the read masks with the same symbol.");
        AssertEx.Equal("http://127.0.0.1:11434", variables.GetProperty(ExternalAppEndpointPayloads.PlainVariableName).GetString());
    }

    [Test]
    public async Task Variables_WithoutAnExpectedVersion_ReturnsBadRequestWithoutReachingTheService()
    {
        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "PUT",
                                       ExternalAppEndpointPayloads.InstanceVariables,
                                       new
                                       {
                                           variables = new Dictionary<string, string>(StringComparer.Ordinal)
                                       });
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "expectedVersion", StringComparison.Ordinal);
        AssertEx.Empty(apps.ReceivedCalls());
    }

    [Test]
    public async Task Variables_WithAStaleExpectedVersion_ReturnsVersionConflict()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ConfigureAsync(Arg.Any<Guid>(),
                Arg.Any<long>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceDetail>(_ => throw new ExternalAppConcurrencyException("The instance moved since you read it."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "PUT",
                                       ExternalAppEndpointPayloads.InstanceVariables,
                                       new
                                       {
                                           expectedVersion = 3L,
                                           variables = new Dictionary<string, string>(StringComparer.Ordinal)
                                       });
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppVersionConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>A created container's environment is immutable, so reconfiguring a running instance is refused.</summary>
    [Test]
    public async Task Variables_OnARunningInstance_ReturnsInvalidTransition()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ConfigureAsync(Arg.Any<Guid>(),
                Arg.Any<long>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceDetail>(_ =>
                throw new ExternalAppInvalidTransitionException("Stop the instance before changing its settings."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "PUT",
                                       ExternalAppEndpointPayloads.InstanceVariables,
                                       new
                                       {
                                           expectedVersion = 7L,
                                           variables = new Dictionary<string, string>(StringComparer.Ordinal)
                                       });
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppInvalidTransition", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>A required variable the operator did not supply is a catalog-driven check, so it is a 400 naming it.</summary>
    [Test]
    public async Task Install_WhenTheServiceRejectsTheVariables_ReturnsBadRequestNamingThem()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.InstallAsync(Arg.Any<InstallCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ => throw new ExternalAppValidationException($"Required variables are missing: {ExternalAppEndpointPayloads.SecretVariableName}.",
                [ExternalAppEndpointPayloads.SecretVariableName]));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.Instances,
                                       ExternalAppEndpointPayloads.InstallBody());
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, ExternalAppEndpointPayloads.SecretVariableName, StringComparison.Ordinal);
    }

    private static TestServerWebAppFactory Factory(IExternalAppService apps) =>
        ExternalAppEndpointPayloads.EnabledFactory(apps);
}
