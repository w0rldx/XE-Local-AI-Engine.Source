namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;

using System.Net;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The five lifecycle verbs, the update preview and the cancel. Everything here turns on two rules: a 202 body is
///     the ADMITTED row and never the outcome, and every command but cancel carries the version the operator last read.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppLifecycleEndpointTests
{
    [Test]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStop)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceRestart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceReset)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceUpdate)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceCancel)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceUpdatePreview)]
    public async Task Route_WithoutAToken_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = Factory(Substitute.For<IExternalAppService>());

        using var response = await ExternalAppEndpointPayloads.SendAnonymousAsync(factory, method, route).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require a token.");
    }

    [Test]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceStop)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceRestart)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceReset)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceUpdate)]
    [Arguments("POST", ExternalAppEndpointPayloads.InstanceCancel)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceUpdatePreview)]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        await using var factory = Factory(Substitute.For<IExternalAppService>());

        using var response = await ExternalAppEndpointPayloads.SendAsNonOperatorAsync(factory, method, route).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {route} is operator-only.");
    }

    [Test]
    [Arguments(ExternalAppEndpointPayloads.InstanceStart, "Starting")]
    [Arguments(ExternalAppEndpointPayloads.InstanceStop, "Stopping")]
    [Arguments(ExternalAppEndpointPayloads.InstanceRestart, "Starting")]
    [Arguments(ExternalAppEndpointPayloads.InstanceReset, "Resetting")]
    public async Task LifecycleVerb_ReturnsAcceptedWithTheAdmittedSnapshot(string route, string status)
    {
        var admitted = Enum.Parse<ExternalAppInstanceStatus>(status);

        var apps = Substitute.For<IExternalAppService>();
        StubLifecycle(apps, ExternalAppEndpointPayloads.Summary(admitted, version: 8));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", route, new
                                   {
                                       expectedVersion = 7L
                                   })
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode, $"{route} admits and returns; the container work runs on the runner.");
        AssertEx.Equal(status, document.RootElement.GetProperty("status").GetString(), "the status set carries the transient values.");
        AssertEx.Equal(8, document.RootElement.GetProperty("version").GetInt64(), "the version the next command must echo.");
    }

    /// <summary>
    ///     The response completes while the work behind it is still blocked, and the request's own token is not what
    ///     cancels it. A client that navigates away must not abort an install halfway.
    /// </summary>
    [Test]
    public async Task Start_ReturnsBeforeTheOperationFinishesAndDoesNotCancelItWithTheRequestToken()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedToken = CancellationToken.None;

        var apps = Substitute.For<IExternalAppService>();
        apps.StartAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observedToken = call.Arg<CancellationToken>();
                admitted.SetResult();
                return Task.FromResult(ExternalAppEndpointPayloads.Summary(ExternalAppInstanceStatus.Starting, version: 8));
            });

        // The work the runner would own. It is gated on a task this test controls, so "still running" is a fact the
        // test established rather than a window it waited out.
        var operation = release.Task;

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceStart, new
                                   {
                                       expectedVersion = 7L
                                   })
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await admitted.Task.ConfigureAwait(false);
        AssertEx.False(operation.IsCompleted, "the response is back while the operation behind it is still running.");
        AssertEx.False(observedToken.IsCancellationRequested,
            "the request token covers the admission only; the runner holds its own lifetime-linked token.");

        release.SetResult();
        await operation.ConfigureAwait(false);
    }

    [Test]
    [Arguments(ExternalAppEndpointPayloads.InstanceStart)]
    [Arguments(ExternalAppEndpointPayloads.InstanceStop)]
    [Arguments(ExternalAppEndpointPayloads.InstanceRestart)]
    [Arguments(ExternalAppEndpointPayloads.InstanceReset)]
    [Arguments(ExternalAppEndpointPayloads.InstanceUpdate)]
    public async Task LifecycleVerb_WithoutAnExpectedVersion_ReturnsBadRequestWithoutReachingTheService(string route)
    {
        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        var body = route == ExternalAppEndpointPayloads.InstanceUpdate
            ? UpdateBody(expectedVersion: null)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        using var response = await ExternalAppEndpointPayloads.SendAsOperatorAsync(factory, "POST", route, body).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"{route} must not act at version 0 for a client that forgot the guard.");
        AssertEx.Contains(payload, "expectedVersion", StringComparison.Ordinal);
        AssertEx.Empty(apps.ReceivedCalls());
    }

    [Test]
    [Arguments(ExternalAppEndpointPayloads.InstanceStart)]
    [Arguments(ExternalAppEndpointPayloads.InstanceStop)]
    [Arguments(ExternalAppEndpointPayloads.InstanceRestart)]
    [Arguments(ExternalAppEndpointPayloads.InstanceReset)]
    [Arguments(ExternalAppEndpointPayloads.InstanceUpdate)]
    public async Task LifecycleVerb_WithAStaleExpectedVersion_ReturnsVersionConflict(string route)
    {
        var apps = Substitute.For<IExternalAppService>();
        ThrowFromLifecycle(apps, () => new ExternalAppConcurrencyException("The instance moved since you read it."));

        await using var factory = Factory(apps);

        var body = route == ExternalAppEndpointPayloads.InstanceUpdate
            ? UpdateBody(expectedVersion: 3L)
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["expectedVersion"] = 3L
            };

        using var response = await ExternalAppEndpointPayloads.SendAsOperatorAsync(factory, "POST", route, body).ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppVersionConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>
    ///     An unparseable instance id is refused by the binder, not by the service: the route carries no <c>:guid</c>
    ///     constraint, so a malformed id MATCHES the route and would otherwise reach a handler holding
    ///     <c>Guid.Empty</c> — which is a real instance id as far as the store is concerned.
    ///     <para>
    ///         The two reads are here as well as the two commands because all four bind the same
    ///         <c>ExternalAppInstanceRequest</c>: every service member the four could reach is stubbed to SUCCEED, so
    ///         the 400 is the binder refusing rather than an unstubbed substitute failing further in.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("POST", "/start")]
    [Arguments("POST", "/cancel")]
    [Arguments("GET", "")]
    [Arguments("GET", "/update-preview")]
    public async Task InstanceRoute_WithAMalformedInstanceId_ReturnsBadRequestWithoutReachingTheService(string method, string suffix)
    {
        var apps = Substitute.For<IExternalAppService>();
        StubLifecycle(apps, ExternalAppEndpointPayloads.Summary(ExternalAppInstanceStatus.Starting, version: 8));
        apps.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.Detail());
        apps.PreviewUpdateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.UpdatePreviewOf());

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       method,
                                       $"{ExternalAppEndpointPayloads.Instances}/not-a-guid{suffix}",
                                       new
                                       {
                                           expectedVersion = 7L
                                       })
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest,
            response.StatusCode,
            $"a malformed id must not reach {method} {suffix} as Guid.Empty.");
        AssertEx.Empty(apps.ReceivedCalls());
    }

    /// <summary>
    ///     The uninstall carries the version in the QUERY, so the three cases that matter are distinct: OMITTED is a 400
    ///     that never reaches the service, an explicit zero REACHES it — a 400 there would prove the presence check was
    ///     really a truthiness check — and a stale value is the service's 409.
    /// </summary>
    [Test]
    public async Task Uninstall_WithoutTheQuery_ReturnsBadRequestAndCallsNothing()
    {
        var apps = Substitute.For<IExternalAppService>();
        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "DELETE", ExternalAppEndpointPayloads.Instance)
                                   .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "an omitted query must not delete at version 0.");
        AssertEx.Contains(body, "expectedVersion", StringComparison.Ordinal);
        AssertEx.Empty(apps.ReceivedCalls());
    }

    [Test]
    public async Task Uninstall_WithAnExplicitZero_ReachesTheService()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.UninstallAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.Summary(ExternalAppInstanceStatus.Uninstalling, version: 1));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "DELETE", $"{ExternalAppEndpointPayloads.Instance}?expectedVersion=0")
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode, "zero is a version, not an absence.");
        await apps.Received(1).UninstallAsync(ExternalAppEndpointPayloads.InstanceId, 0L, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Uninstall_WithAStaleQueryValue_ReturnsVersionConflict()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.UninstallAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ => throw new ExternalAppConcurrencyException("The instance moved since you read it."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "DELETE", $"{ExternalAppEndpointPayloads.Instance}?expectedVersion=3")
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppVersionConflict", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>The gate is checked before the transition table, so "busy" is one answer that does not depend on how far the other operation got.</summary>
    [Test]
    public async Task LifecycleVerb_WhileAnotherOperationHoldsTheGate_ReturnsOperationInFlight()
    {
        var apps = Substitute.For<IExternalAppService>();
        ThrowFromLifecycle(apps, () => new ExternalAppOperationInFlightException("Another operation is running on this instance."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceStop, new
                                   {
                                       expectedVersion = 7L
                                   })
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppOperationInFlight", document.RootElement.GetProperty("conflictType").GetString());
    }

    [Test]
    [Arguments(ExternalAppEndpointPayloads.InstanceStart)]
    [Arguments(ExternalAppEndpointPayloads.InstanceStop)]
    [Arguments(ExternalAppEndpointPayloads.InstanceCancel)]
    public async Task Verb_ForAnUnknownInstance_ReturnsNotFoundWithAnEmptyBody(string route)
    {
        var apps = Substitute.For<IExternalAppService>();
        ThrowFromLifecycle(apps, () => new ExternalAppNotFoundException("No such instance."));
        apps.CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ExternalAppNotFoundException("No such instance."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", route, new
                                   {
                                       expectedVersion = 7L
                                   })
                                   .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Empty(body);
    }

    /// <summary>
    ///     The one command without <c>expectedVersion</c>: requiring the operator's tab to be current before it can stop
    ///     a runaway pull would defeat the point.
    /// </summary>
    [Test]
    public async Task Cancel_TakesNoExpectedVersionAndAnswersAccepted()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceCancel)
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await apps.Received(1).CancelAsync(ExternalAppEndpointPayloads.InstanceId, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>A transient status with no live operation is a crashed one, which the boot reconciler settles rather than a cancel.</summary>
    [Test]
    public async Task Cancel_WithNothingInFlight_ReturnsInvalidTransition()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new ExternalAppInvalidTransitionException("Nothing is running on this instance."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceCancel)
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppInvalidTransition", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>
    ///     One assertion per member the update preview owes, so a rename on the service side fails here rather than
    ///     reaching the SPA as a null.
    /// </summary>
    [Test]
    public async Task UpdatePreview_CarriesEveryDeclaredMemberUnderItsOwnName()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewUpdateAsync(ExternalAppEndpointPayloads.InstanceId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.UpdatePreviewOf(addedPermissions: ["capabilities", "extraHosts"]));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceUpdatePreview)
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);
        var root = document.RootElement;

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(ExternalAppEndpointPayloads.ApplicationId, root.GetProperty("applicationId").GetString());
        AssertEx.Equal(ExternalAppEndpointPayloads.InstanceIdLiteral, root.GetProperty("instanceId").GetString());
        AssertEx.Equal(2, root.GetProperty("currentManifestVersion").GetInt32());
        AssertEx.Equal(3, root.GetProperty("targetManifestVersion").GetInt32(), "named for the TARGET, so no reader has to guess.");
        AssertEx.Equal(ExternalAppEndpointPayloads.ManifestSha256, root.GetProperty("manifestSha256").GetString());
        AssertEx.Equal(2, root.GetProperty("variables").GetArrayLength(), "every variable the TARGET manifest declares.");
        AssertEx.True(root.GetProperty("canUpdate").GetBoolean());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("blockedReason").ValueKind);
        AssertEx.Equal("Enough memory and disk are free.", root.GetProperty("resourceVerdict").GetProperty("message").GetString());
        AssertEx.Equal("CHOWN", root.GetProperty("effectivePermissions").GetProperty("services").GetProperty("worker").GetProperty("capabilities")[0].GetString());

        var added = root.GetProperty("addedPermissions");
        AssertEx.Equal("capabilities", added[0].GetString());
        AssertEx.Equal("extraHosts", added[1].GetString());
    }

    /// <summary>
    ///     The stored value of a variable that stopped being <c>secret</c> is discarded on update and never returned in
    ///     plaintext, so it arrives UNSET and the operator re-enters it.
    /// </summary>
    [Test]
    public async Task UpdatePreview_MasksTheCurrentValueOfASecretAndOmitsOneThatStoppedBeingSecret()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewUpdateAsync(ExternalAppEndpointPayloads.InstanceId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.UpdatePreviewOf(currentValues: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ExternalAppEndpointPayloads.SecretVariableName] = ExternalAppVariableMask.Value
            }));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceUpdatePreview)
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        var currentValues = document.RootElement.GetProperty("currentValues");
        AssertEx.Equal(ExternalAppVariableMask.Value, currentValues.GetProperty(ExternalAppEndpointPayloads.SecretVariableName).GetString());
        AssertEx.False(currentValues.TryGetProperty(ExternalAppEndpointPayloads.PlainVariableName, out _),
            "a value that is no longer masked and no longer stored arrives unset rather than in plaintext.");
    }

    /// <summary>
    ///     An application that left the catalog previews as a 200 blocked preview, because the dialog must be able to
    ///     say WHY there is nothing to update — while the command on the same instance is a 404.
    /// </summary>
    [Test]
    public async Task UpdatePreview_WhenTheApplicationLeftTheCatalog_IsABlockedTwoHundredWhileTheCommandIsNotFound()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.PreviewUpdateAsync(ExternalAppEndpointPayloads.InstanceId, Arg.Any<CancellationToken>())
            .Returns(ExternalAppEndpointPayloads.UpdatePreviewOf(canUpdate: false, blockedReason: ExternalAppBlockedReason.CatalogMissing));
        apps.UpdateAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<UpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ => throw new ExternalAppNotFoundException("Odysseus is no longer in the catalog."));

        await using var factory = Factory(apps);

        using var preview = await ExternalAppEndpointPayloads
                                  .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceUpdatePreview)
                                  .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(preview).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, preview.StatusCode);
        AssertEx.False(document.RootElement.GetProperty("canUpdate").GetBoolean());
        AssertEx.Equal("CatalogMissing", document.RootElement.GetProperty("blockedReason").GetString());
        AssertEx.Equal(0, document.RootElement.GetProperty("addedPermissions").GetArrayLength());

        using var command = await ExternalAppEndpointPayloads
                                  .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceUpdate, UpdateBody(7L))
                                  .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, command.StatusCode, "the preview explains; the command refuses.");
    }

    /// <summary>
    ///     The refusal that can only say "conflict" is useless when the useful answer is "these permissions are new", so
    ///     the 409 carries them as a string array from the closed vocabulary.
    /// </summary>
    [Test]
    public async Task Update_WithoutAcknowledgingAWidening_ReturnsTheAddedPermissionsOnTheConflict()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.UpdateAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<UpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ => throw new ExternalAppPermissionChangeRequiresAcknowledgementException("This update grants permissions the installed version does not have.",
                ["capabilities", "writableRootFilesystem"]));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory,
                                       "POST",
                                       ExternalAppEndpointPayloads.InstanceUpdate,
                                       UpdateBody(7L, acceptPermissions: false))
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);
        var root = document.RootElement;

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppPermissionChangeRequiresAcknowledgement", root.GetProperty("conflictType").GetString());

        var added = root.GetProperty("addedPermissions");
        AssertEx.Equal(2, added.GetArrayLength());
        AssertEx.Equal("capabilities", added[0].GetString());
        AssertEx.Equal("writableRootFilesystem", added[1].GetString());
        AssertEx.Contains(ExternalAppEffectivePermissions.Vocabulary, "writableRootFilesystem", "the names come from the one closed vocabulary.");
    }

    /// <summary>A refusal with no widening carries no names at all, rather than an empty array nobody can tell from a missing one.</summary>
    [Test]
    public async Task Conflict_WithoutAddedPermissions_OmitsTheMemberEntirely()
    {
        var apps = Substitute.For<IExternalAppService>();
        ThrowFromLifecycle(apps, () => new ExternalAppInvalidTransitionException("The instance is already running."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceStart, new
                                   {
                                       expectedVersion = 7L
                                   })
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.False(document.RootElement.TryGetProperty("addedPermissions", out _),
            "the member is omitted when null, so the body is unchanged for every other conflict.");
    }

    [Test]
    public async Task Update_WithAStaleFingerprint_ReturnsManifestChanged()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.UpdateAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<UpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ => throw new ExternalAppManifestChangedException("The catalog now serves a different manifest.",
                4,
                ExternalAppEndpointPayloads.OtherManifestSha256));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceUpdate, UpdateBody(7L))
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("ExternalAppManifestChanged", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>A target manifest can declare a variable the installed snapshot never had, and one of them may be required.</summary>
    [Test]
    public async Task Update_MissingANewlyRequiredVariable_ReturnsBadRequestNamingIt()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.UpdateAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<UpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ => throw new ExternalAppValidationException("Required variables are missing: SMTP_HOST.",
                ["SMTP_HOST"]));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "POST", ExternalAppEndpointPayloads.InstanceUpdate, UpdateBody(7L))
                                   .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "SMTP_HOST", StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> UpdateBody(long? expectedVersion, bool acceptPermissions = true)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["manifestVersion"] = 3,
            ["manifestSha256"] = ExternalAppEndpointPayloads.ManifestSha256,
            ["acceptPermissions"] = acceptPermissions,
            ["variables"] = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        if (expectedVersion is not null)
        {
            body["expectedVersion"] = expectedVersion.Value;
        }

        return body;
    }

    private static void StubLifecycle(IExternalAppService apps, ExternalAppInstanceSummary summary)
    {
        apps.StartAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(summary);
        apps.StopAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(summary);
        apps.RestartAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(summary);
        apps.ResetAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(summary);
        apps.UninstallAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(summary);
        apps.UpdateAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<UpdateCommand>(), Arg.Any<CancellationToken>()).Returns(summary);
    }

    private static void ThrowFromLifecycle(IExternalAppService apps, Func<Exception> exception)
    {
        apps.StartAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns<ExternalAppInstanceSummary>(_ => throw exception());
        apps.StopAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns<ExternalAppInstanceSummary>(_ => throw exception());
        apps.RestartAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns<ExternalAppInstanceSummary>(_ => throw exception());
        apps.ResetAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns<ExternalAppInstanceSummary>(_ => throw exception());
        apps.UninstallAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns<ExternalAppInstanceSummary>(_ => throw exception());
        apps.UpdateAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<UpdateCommand>(), Arg.Any<CancellationToken>())
            .Returns<ExternalAppInstanceSummary>(_ => throw exception());
    }

    private static TestServerWebAppFactory Factory(IExternalAppService apps) =>
        ExternalAppEndpointPayloads.EnabledFactory(apps);
}
