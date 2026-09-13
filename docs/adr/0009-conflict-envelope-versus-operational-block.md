# ADR 0009: A 409 uses the global conflict envelope, unless the refusal is an operational block with its own typed body

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-09-09.
- **Date:** 2026-09-09
- **Scope:** Which of the two 409 body shapes an endpoint answers with, and why both exist. It changes no wire
  contract: every route named below keeps the status code, JSON members and reason strings it has today.
- **Authority:** Decided by the maintainer on 2026-09-09, after a read-only endpoint survey found two 409
  conventions in force while `ConflictExceptionHandler`'s own doc comment forbade one of them.

## Context

`ConflictExceptionHandler` is the single `IExceptionHandler` that turns a typed domain exception into a 409. Its
doc comment states, correctly for what it governs, that endpoints "must not hand-build conflict bodies": every
exception in its switch answers with one `ConflictProblemDetails` envelope, discriminated by a `ConflictType`
member drawn from the global `NodeConflictProblemType` enum, and declared on a route with
`ProblemDetailsProducesExtensions.ProducesConflictProblemDetails`.

Alongside it, eight per-feature `*BlockedResponse` DTOs are built directly in endpoints and declared with
`.Produces<T>(StatusCodes.Status409Conflict)`. Read as written, the doc comment made all of them violations.

They are not — but **not for the reason that is usually given**. The intuitive defence, that the Blocked family
exists because the client needs richer machine-readable detail than the envelope can carry, is false in this
repository and was checked before this ADR was written: nothing in the SPA reads any member of a 409 body except
`message`. See "What the client actually reads" below. The reasons the family is legitimate are structural, on
the producer side, and were each re-opened in code.

**Most of these refusals are never thrown, so no exception handler can see them.** Of the eighteen sites that
construct a `*BlockedResponse`, twelve are reached from a value the service returned, not from a `catch`:
`TrainingRuntimeInstallOutcome`, `BaseArtifactDeleteOutcome`, `LlamaCppSourceBuildStartOutcome`,
`TrainingExportStartOutcome`, `LlamaCppRuntimeAdministrationFailure.Busy`, the `removed` / `buildActive` tuple
from `LlamaCppPrebuiltRuntimeMutationGuard.TryRemoveAsync`, the `Evicted` flag on the image supervisor's evict
result, and a plain `OperatingSystem.IsLinux()` guard. Routing those through the global envelope would mean
throwing an exception to signal an expected, non-exceptional outcome, purely to reach a handler.

**A single route reaches the same reason code from both directions.**
`StartTrainingRuntimeInstallEndpoint.HandleAsync` answers `"prerequisites"` from
`TrainingRuntimeInstallOutcome.MissingPrerequisites` **and** from `catch (TrainingRuntimeException)`;
`StartCudaBuildEndpoint.HandleAsync` and `StartLlamaCppSourceBuildEndpoint.HandleAsync` do the same with
`LlamaCppSourceBuildStartOutcome.MissingPrerequisites` and `catch (LlamaRuntimeException)`;
`StartStableDiffusionCppSourceBuildEndpoint.HandleAsync` splits `"prerequisites"` across two outcomes. Sending
only the caught half through `ConflictExceptionHandler` would give one reason code two different wire shapes on
one route, which is worse than either convention alone.

**The discriminator is global; the reason vocabularies are per-route.** `NodeConflictProblemType` is one enum
shared by every centralised conflict. Absorbing the Blocked family would add roughly fifteen operational codes
(`not-linux`, `disk`, `prerequisites`, `processes-running`, `already-building`, `already-installing`,
`runtime-busy`, `keep-model-warm-enabled`, `remove-failed`, `source-build-error`, `downloading`,
`not-downloading`, `rejected`, …) to the same namespace as `WorkSessionInvalidTransition` and
`GraphWorkflowRunConflict`, and would put each route's payload members — `RunningProcessCount`,
`Prerequisites`, `Activity` — on the one globally shared `ConflictProblemDetails` class, where every other route
would also publish them.

**What the client actually reads — recorded so nobody re-derives it wrongly.** The SPA does **not** read the extra
members off a 409 today. `ApiError`'s `resolveMessage` reads `detail`, then `message`, then `title`, and nothing else; the
prerequisite checklists come from their own GET routes (`useTrainingRuntimePrerequisites`,
`useSourceBuildPrerequisites`, `useImageRuntimeSourceBuildPrerequisites`), the running-process count from the
runtime status query, and the image `activity` snapshot from `ImageRuntimeStatusResponse` via
`ImageRuntimeSourceBuildMappers`. The justification for the Blocked family is therefore entirely the producer
side — the refusal is a returned value, and the vocabulary is per-route — and **never** a consumer that reads the
checklist off the error. Those members are a published, schema-named contract that a client *may* branch on; they
are not one that this repository's client branches on yet. Any future argument for or against this convention has
to be made on §3's producer test, not on what the SPA renders.

## Decision

1. **A domain conflict goes through the global envelope.** "This state transition is not legal right now" — a
   read-only conversation, a superseded version, a gate already decided, a worker not paired. The service throws
   a typed domain exception, `ConflictExceptionHandler` maps it to a `NodeConflictProblemType` member, and the
   endpoint declares `ProducesConflictProblemDetails()`. Endpoints must not hand-build these bodies; that rule
   stands unchanged.

2. **An operational block gets a per-feature typed response.** "A runtime, a child process, a build or a
   prerequisite is standing in the way" — the request is well-formed and will succeed later once the operator
   clears the obstacle. The endpoint answers `Results.Conflict(new …BlockedResponse { … })` and declares
   `.Produces<…BlockedResponse>(StatusCodes.Status409Conflict)`.

3. **The test between them is mechanical, and it is about the producer, not the payload.** Ask: *can this route
   refuse this request, with this reason code, without anything being thrown?* If yes — the service reports the
   refusal as an outcome enum, a `false`, or a result record — it is an operational block, because no
   `IExceptionHandler` sits on that path and inventing an exception to reach one is the wrong direction. If the
   only way to refuse is a thrown typed domain exception, and the whole answer to the client is "which conflict
   this is", it belongs in the global envelope. When one route can refuse both ways under one reason code, the
   whole route uses the Blocked body, so the code has one shape.

4. **Every Blocked family has exactly one place that builds its body.** A feature that produces a Blocked
   response from more than one endpoint owns a `…BlockedEndpointSupport` static that constructs it, and any
   reason code shared between two routes is a named constant on that class. `ImageRuntimeBlockedEndpointSupport`
   is the reference shape; `TrainingRuntimeBlockedEndpointSupport` and `BaseArtifactBlockedEndpointSupport` were
   added under this ADR; `LlamaCppSourceBuildStartEndpointSupport` centralises the outcome→reason mapping that
   the two llama.cpp start routes share.

5. **A Blocked DTO is not a licence to skip the exception pipeline for exceptions the pipeline already owns.**
   The endpoint-local `catch` that feeds a Blocked body stays inside the reviewed allowlist asserted by
   `EndpointExceptionMappingSourceGuardTests.EndpointCatches_AreLimitedToTheReviewedContextualBatchAndRecoverySites`.
   Adding a Blocked family does not add catches to that allowlist by default.

## The eight Blocked families

| Response type | Members beyond the envelope | Produced by | Verdict |
|---|---|---|---|
| `ImageRuntimeBlockedResponse` | `reason`, `activity` (`ImageRuntimeActivityResponse`) | `CreateImageJobEndpoint`, `EjectImageRuntimeEndpoint`, `StartStableDiffusionCppSourceBuildEndpoint`, `RemoveStableDiffusionCppSourceBuildEndpoint`, all through `ImageRuntimeBlockedEndpointSupport` | **Qualifies.** Every producer but one reads a supervisor snapshot rather than catching; the `activity` object has no place on a shared envelope. The reference implementation of §4. |
| `LlamaCppSourceBuildBlockedResponse` | `reason`, `runningProcessCount` | `StartLlamaCppSourceBuildEndpoint`, `RemoveLlamaCppSourceBuildEndpoint` | **Qualifies.** Both the start outcome and the remove guard are returned values; the start route also reaches `"prerequisites"` from a catch, so §3's one-shape rule applies. |
| `CudaBuildBlockedResponse` | `reason`, `runningProcessCount` | `StartCudaBuildEndpoint`, `RemoveCudaBuildEndpoint` | **Qualifies**, for the same reasons; it is the CUDA-specific twin of the row above and shares `LlamaCppSourceBuildStartEndpointSupport`. |
| `TrainingRuntimeBlockedResponse` | `reason`, `prerequisites` (checklist) | `StartTrainingRuntimeInstallEndpoint`, `RemoveTrainingRuntimeEndpoint` | **Qualifies.** Four of its six reason codes come from an outcome enum or an OS guard, and `"prerequisites"` is reached from both directions on one route. |
| `LlamaCppUpdateBlockedResponse` | `runningProcessCount` (**no** `reason`) | `UpdateLlamaCppRuntimeEndpoint`, `EnsureLlamaCppBinaryEndpoint` | **Qualifies**, but it is the odd one out: it carries a count and no reason code, and both producers read `LlamaCppRuntimeAdministrationFailure.Busy` off a result record. Nothing is thrown on either path, so the envelope could not reach it at all. |
| `BaseArtifactBlockedResponse` | `reason` | `CreateBaseArtifactEndpoint`, `CancelBaseArtifactEndpoint`, `DeleteBaseArtifactEndpoint` | **Qualifies on the vocabulary, not on the payload.** It carries no member the envelope lacks; two of its three codes (`downloading`, `not-downloading`) are returned values, and only `rejected` is a catch. It stays Blocked so one route family speaks one shape — but it is the family a future pass would move first if the reason codes were ever centralised. |
| `TrainingExportBlockedResponse` | `reason` | `TrainingExportEndpoints` (the export-start endpoint) | **Qualifies**, with a known defect: its `reason` is `TrainingExportStartOutcome.ToString()`, so it emits `Busy` / `RuntimeUnavailable` in PascalCase while every other family emits kebab-case. **Deliberately left as-is** — those strings are the wire contract, and normalising them would break any client already switching on them. An inconsistency inside the convention, not a reason to abandon it. |
| `TranscriptionUnsupportedContainerResponse` | `reason`, `message`, `detectedContainer`, `supportedContainers`, `ffmpegRequired` | `UploadTranscriptionAudioEndpoint` | **Qualifies, at 415 rather than 409.** Added 2026-09-12 by the transcription feature: the refusal is a returned value (`TranscribeFileResult` with the `UnsupportedContainer` outcome), so no `IExceptionHandler` can see it, and the supported-container list has no place on a shared envelope. The status is 415 because the request body's media type is what was refused; the same one-shape-per-route rule applies. |
| `TrainingRunBlockedResponse` | `reason` | **nothing** | **Did not qualify. Declared but never produced — removed on 2026-09-10.** `CreateTrainingRunEndpoint` declared it with `.Produces<TrainingRunBlockedResponse>(409)` but no code constructed it, so it was published in `openapi/v1.json` and in the generated client (`types.gen.ts`, `zod.gen.ts`, `index.ts`) as a `CreateTrainingRunErrors[409]` the server could never send. **The wrong thing was the shape, not the status.** The route does answer 409, from a different exception family: `TrainingRunStore.CreateAndEnqueueAsync` throws `TrainingConflictException` for `VersionConflict`, `DatasetNotReady` and `BaseArtifactNotReady` inside the create transaction, and `TrainingExceptionHandler` answers it with a `TrainingErrorResponse` — the envelope `TrainingComparisonEndpoints` and `TrainingEvaluationEndpoints` already declare. Only `TrainingRunRejectedException` takes the `DomainValidationExceptionHandler` 400. **This ADR never blessed the Blocked DTO as a member of the family**; it was dead wire surface, and the removal — the DTO, the `.Produces` call and a regenerated client — landed as its own change because it moves the OpenAPI document. The row stays for the warning it carries, not as precedent. |

## A third 409 shape exists, and it is transitional

The two shapes above are the ones this ADR endorses. A third is in force today and must be named, or the next
reader will find it and conclude the ADR is wrong.

`DevelopmentConflictExceptionHandler` and `SelectedFolderExceptionHandler` answer 409 with FastEndpoints' own
`ProblemDetails` — the body `AddError(message)` plus `Send.ErrorsAsync(statusCode: 409)` produces, written
through the shared `FastEndpointsProblemWriter`:

```json
{
  "type":     "https://www.rfc-editor.org/rfc/rfc7231#section-6.5.8",
  "title":    "Conflict",
  "status":   409,
  "instance": "/api/local/v1/development/projects/p/tasks/t/next-action",
  "traceId":  "trace-9fcf8c65...",
  "detail":   "The Development project version is stale (expected 3, current 4).",
  "errors":   [ { "name": "generalErrors", "reason": "The Development project version is stale (expected 3, current 4)." } ]
}
```

Content type `application/problem+json; charset=utf-8`. The `errors[].name` on the wire is `generalErrors`; a test
capturing this body from a bare `DefaultHttpContext` sees `GeneralErrors`, because the camel-casing comes from the
host's serializer naming policy, which only the real host configures. `DevelopmentExceptionHandlerTests` pins the
raw constant and comments the difference.

**The `type` URI base is not one convention but two, and the split runs through our OWN handlers.**
`DevelopmentExceptionHandlerTests` already pins the divergence and comments it — read that comment before
touching this; it is the authority on what the wire carries. What it does not say is where the second base comes
from, and that is the part a future reader needs. `ConflictExceptionHandler` *sets* `Type` explicitly, to
`https://tools.ietf.org/html/rfc7231#section-6.5.8`. `DevelopmentConflictExceptionHandler` sets no `Type` at all —
it writes the body through `AddError` + `Send.ErrorsAsync`, so FastEndpoints derives
`https://www.rfc-editor.org/rfc/rfc7231#section-6.5.8`. Two of this repository's own conflict handlers, same
status, same RFC, same section, two hosts.

**Aligning our side onto the FastEndpoints base would make the API less consistent, not more.**
`tools.ietf.org/html/` is not a stray: three hand-written handlers share it (`ConflictExceptionHandler`,
`RequestBodyTooLargeProblem`, `DefaultExceptionHandler`), and — the fact that settles the direction — it is also
what ASP.NET Core's own `ProblemDetailsDefaults` emits. `BenchmarkEndpointSupport.Problem` passes no `type` to
`Results.Problem`, so the benchmark surface already answers `https://tools.ietf.org/html/rfc9110#section-15.5.10`
on a 409 and `https://tools.ietf.org/html/rfc4918#section-11.2` on a 422. Those are Microsoft's strings, pinned by
`BenchmarkExceptionHandlerTests`, and not ours to change without hand-building the bodies the framework exists to
build. So a single canonical `type` across this API is **unreachable**, and FastEndpoints is the outlier rather
than our handlers. One wrinkle worth knowing before anyone "modernises" the constants: ours cite RFC 7231, which
RFC 9110 obsoleted, while the framework's cite 9110 — same semantics, different document.

**What each side would cost, so the next survey does not re-derive it.** Moving ours breaks a pinned client
string: the SPA fixture `XE-Local-AI-Engine.Client.React/src/features/chat/api/NodeChatConflict.test.ts` asserts
the `tools.ietf.org` value. Moving the FastEndpoints side needs a custom `ErrorOptions.ResponseBuilder`, which
rewrites every `Send.ErrorsAsync` body on the API to change a member no client reads — see "What the client
actually reads" above. Note that the `www.rfc-editor.org/rfc/rfc7231#section-6.5.1` that appears in
`XE-Local-AI-Engine.Client.React/openapi/v1.json` and `zod.gen.ts` is neither of these: it is the generic schema
DEFAULT for the `type` property, and its section is 6.5.1 (400), so it says nothing about what a 409 body
carries at runtime.

**Why it exists.** These handlers were created to remove duplicated per-endpoint catches without changing any
response body. The endpoints they replaced hand-wrote exactly this shape, so reproducing it was the only
behaviour-preserving option. It belongs to the *area*, not to the handler: `DevelopmentWorkspaceSecurityException`
and `DevelopmentRepositoryStateConflictException` still write it from their own endpoints.

**Its exit condition is not the handler refactor.** `DevelopmentInvalidTransitionException` and
`DevelopmentConcurrencyException` are the literal twins of `WorkSessionInvalidTransitionException` and
`WorkSessionConcurrencyException`, which are already in the envelope; the split is an accident of which area was
written when. Reconciling Development onto the envelope is worth doing, but for the discriminator, not the
tidiness: a Development 409 carries no machine-readable reason at all, so a client cannot distinguish a stale
version (recoverable — re-read and retry) from an already-applied task (terminal). That reconciliation requires
new `NodeConflictProblemType` members, `ProducesConflictProblemDetails()` on the affected routes, a regenerated
client, and an SPA change that actually consumes the discriminator; done without the last of those, it pays a wire
break and buys nothing.

**What blocks it.** `DevelopmentWorkspaceSecurityException` answers 409 on the patch, preview and next-action
routes and 400 on register, create and reconnect. That split has to be settled first, or Development ends up on
two 409 shapes at once — strictly worse than today. Which status is correct is a product question about what those
routes mean, not a refactoring question.

**A trap for whoever does it.** `ReconnectDevelopmentRepositoryEndpoint` catches
`DevelopmentRepositoryStateConflictException` before `DevelopmentWorkspaceSecurityException`, and the former
derives from the latter. Registering a global handler for the derived type while that base-type arm remains
produces **dead code, not a centralised mapping** — the exception never leaves the endpoint.

## Consequences

**What this forecloses.** A future contributor cannot resolve a 409 question by reading
`ConflictExceptionHandler`'s doc comment alone; that comment now names this carve-out and points here. It also
forecloses the tidier-looking option of promoting the Blocked-family catches to registered `IExceptionHandler`s
in the `BenchmarkExceptionHandler` / `TrainingExceptionHandler` style. That is technically possible — a handler
writes the body while the endpoint keeps its `.Produces<T>(409)` declaration, exactly as
`ProducesConflictProblemDetails` already separates declaration from production, so OpenAPI typing is *not* the
obstacle. It is refused because it would split the routes named in §3 across two wire shapes for one reason
code, and would leave twelve unthrown refusals still hand-built in the endpoint, buying nothing.

**One member of the family was not endorsed, and is gone.** `TrainingRunBlockedResponse` was declared, published
and never produced; it was removed on 2026-09-10 — the DTO, the `.Produces<T>(409)` call, and the schema and its
`CreateTrainingRunErrors[409]` reference in the regenerated spec and client. It is listed above for completeness
and as a warning, not as precedent: a `.Produces<T>(409)` whose `T` no code constructs advertises a response the
server cannot send, and the drift is invisible to `openapi:check`, which regenerates the client from the
committed spec and so agrees with it happily. Adding a Blocked family means adding a producer, not only a
declaration.

**Removing the DTO is not removing the status, and the first attempt got that wrong.** The `.Produces` call went
with the DTO, leaving `CreateTrainingRunEndpoint` declaring 200 and 400 only — while the route still answered 409
through `TrainingExceptionHandler`, which is worse than the wrongly-typed declaration it replaced: an undeclared
status is one no typed SPA handler can be written against, and `openapi:check` agrees with that spec just as
happily. The route now declares `.Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict)`, matching the
other Training routes. When a dead `*BlockedResponse` is removed, check what the route's *other* exception
families do before deleting its status declaration.

**What it costs.** Seven response schemas instead of one, and seven reason vocabularies to keep kebab-case (one
already is not — see `TrainingExportBlockedResponse` above). §4's one-builder-per-family rule is the mitigation:
the shape and the shared codes exist once per feature, so the drift is bounded to a family rather than spread
across its endpoints.

**Evidence that the shapes are pinned.** `TrainingRuntimeEndpointTests` asserts the exact member count, reason
string and message of all three training 409 bodies
(`RuntimeInstall_WhenTheProviderFails_UsesTheInstallPrerequisitesReason`,
`RuntimeRemove_WhenTheProviderFails_UsesTheDistinctRemoveFailedReason`,
`CreateBaseArtifact_WhenTheSelectionIsRejected_PreservesItsDistinctBlockedBody`), and
`OpenApiDocumentTests` pins `ImageRuntimeBlockedResponse`'s published property set. Those are the tests a change
to this convention has to move.
