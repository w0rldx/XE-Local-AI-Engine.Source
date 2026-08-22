# ADR 0002: Development cloud authorization uses `ChatOptions.AdditionalProperties`

- **Status:** Accepted
- **Date:** 2026-07-21
- **Scope:** Development Mode cloud-egress carrier and enforcement boundary
- **Pinned dependency (current):** `Microsoft.Extensions.AI` 10.8.3 / `Microsoft.Extensions.AI.Abstractions` 10.9.0 (`Directory.Packages.props`)
- **Pinned dependency (at decision):** `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.Abstractions` 10.7.0 — see "Re-verification 2026-08-17"

## Context

Development Mode must authorize every raw cloud-provider round, including the function-result follow-up round created inside `FunctionInvokingChatClient`. The authorization cannot depend on ambient execution context alone, and an ordinary Chat request must remain behavior-compatible.

At the time of this decision the repository pinned Microsoft.Extensions.AI 10.7.0, whose official NuGet packages identify the exact upstream source commit as `fa0072f10f11eae347aaecaa3c3e81e701b0f79d`. The pin has since moved; see "Re-verification 2026-08-17".

## Version-aware evidence (derived at 10.7.0)

At the 10.7.0 source commit `fa0072f10f11eae347aaecaa3c3e81e701b0f79d`:

1. [`ChatOptions`' copy constructor](https://github.com/dotnet/extensions/blob/fa0072f10f11eae347aaecaa3c3e81e701b0f79d/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatOptions.cs#L19-L54) assigns `AdditionalProperties = other.AdditionalProperties?.Clone()`.
2. [`ChatOptions.Clone`](https://github.com/dotnet/extensions/blob/fa0072f10f11eae347aaecaa3c3e81e701b0f79d/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatOptions.cs#L226-L238) documents shallow-cloned collections and returns `new(this)`. Therefore the dictionary instance changes, but each stored value reference is preserved.
3. [`FunctionInvokingChatClient.GetResponseAsync`](https://github.com/dotnet/extensions/blob/fa0072f10f11eae347aaecaa3c3e81e701b0f79d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs#L328-L418) and [its streaming counterpart](https://github.com/dotnet/extensions/blob/fa0072f10f11eae347aaecaa3c3e81e701b0f79d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs#L512-L694) call the inner client once per iteration, append tool results, and call it again until the loop stops.
4. [`UpdateOptionsForNextIteration`](https://github.com/dotnet/extensions/blob/fa0072f10f11eae347aaecaa3c3e81e701b0f79d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs#L982-L1018) clones options only when it must reset required tool mode, propagate a changed conversation ID, or clear a continuation token. Otherwise it reuses the same `ChatOptions` instance.
5. [`PrepareOptionsForLastIteration`](https://github.com/dotnet/extensions/blob/fa0072f10f11eae347aaecaa3c3e81e701b0f79d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs#L1030-L1062) also clones before removing function declarations.

Microsoft Learn independently describes [`ChatOptions.Clone`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chatoptions.clone) as a shallow clone and describes [`FunctionInvokingChatClient`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient) as repeatedly sending generated function results back to the inner client.

## Decision

1. Carry a code-owned, sealed immutable Development authorization value in a namespaced `ChatOptions.AdditionalProperties` entry. Store only immutable scalar values and identifiers/hashes in the carrier; never store mutable collections, raw prompt text, repository text, or secrets.
2. Keep an explicit Development purpose marker in the same code-owned carrier shape so the authorizer can distinguish ordinary Chat from a Development request even when the authorization portion is missing or malformed.
3. Enforce authorization inside `RuntimeChatClient`, immediately after a cloud client is selected and before either non-streaming invocation or streaming enumeration is delegated to that client. This is the selected-cloud boundary reached by every inner `FunctionInvokingChatClient` round.
4. Treat ambient scope as optional correlation only. The envelope in `ChatOptions` is authoritative and must independently survive execution-context suppression.
5. Regression tests must force the 10.7.0 clone branch rather than merely observe two rounds. The fake first response should return a non-null conversation ID (or otherwise require an option mutation), and assertions must prove:
   - round 1 and round 2 received distinct `ChatOptions` objects;
   - their `AdditionalProperties` dictionaries are distinct;
   - both dictionaries contain the same immutable authorization value reference and values;
   - the authorizer ran before both fake transports.
6. Exercise both non-streaming and streaming function loops. The latter is required because production invocation uses the streaming path and Microsoft.Extensions.AI implements the two loops separately. The observed order must be `authorize1 -> transport1 -> authorize2 -> transport2` on each surface.
7. If this exact carrier assertion fails under the pinned package or composed application pipeline, cloud support remains disabled; do not compensate with `AsyncLocal`. The approved fallback is a dedicated Development client that reapplies the immutable carrier on every provider round.

The dedicated-client fallback is not implemented in this change. Until it is, upgrading Microsoft.Extensions.AI is blocked unless the forced-clone streaming and non-streaming carrier tests continue to pass unchanged. This is an explicit defense-in-depth deferral, not evidence that arbitrary future clone behavior is supported.

## Consequences

- `AdditionalProperties` is sufficient under Microsoft.Extensions.AI 10.7.0 because its shallow clone preserves the immutable carrier value across the option clones used by the function loop.
- A generic authorizer at `RuntimeChatClient` covers both direct sends and autonomous tool-result follow-ups without changing ordinary Chat routing.
- Tests must pin this version-sensitive behavior so a future Microsoft.Extensions.AI upgrade cannot silently remove the carrier from inner rounds.
- The unimplemented dedicated-client fallback is an upgrade blocker. A dependency update must either preserve the verified shallow-carrier behavior or land the fail-closed dedicated Development client first.
- This ADR decides only the cloud carrier and final authorization boundary; persistence, orchestration, UI, and API behavior are governed by their own contracts.

## Re-verification 2026-08-17

The pin moved off 10.7.0 without an ADR update, which is precisely what the upgrade gate above was written to prevent. `Directory.Packages.props` now carries `Microsoft.Extensions.AI` 10.8.3 and `Microsoft.Extensions.AI.Abstractions` 10.9.0. The moves were routine dependency servicing: `0f57645b` and `027ddd95` (2026-07-26 / 2026-07-31, in-repo servicing commits, not dependabot) took `Microsoft.Extensions.AI` 10.7.0 -> 10.8.1 -> 10.8.3, and dependabot's `90165471` (2026-08-14, "Bump the agent-ai-coupled group") took `Microsoft.Extensions.AI.Abstractions` and `.OpenAI` to 10.9.0. This ADR was last edited on 2026-07-22 (`36d8566d`).

What was checked on 2026-08-17, and what was not:

- **Carrier regression tests re-run and green.** `dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj --configuration Debug --no-build -- --treenode-filter "/*/*/DevelopmentCloudEgressAuthorizationTests/*"` -> 19 total, 19 succeeded, 0 failed, 0 skipped. That class still contains `FunctionLoop_NonStreaming_ForcedCloneAuthorizesBothRawRoundsBeforeTransport` and `FunctionLoop_Streaming_ForcedCloneAuthorizesBothRawRoundsBeforeTransport` with the Decision 5 assertions unchanged (distinct `ChatOptions`, distinct `AdditionalProperties` dictionaries, same carrier value reference in both, authorizer before each transport). Under the current pin the forced-clone branch is therefore still taken and the carrier still survives it.
- **The version-aware evidence above has NOT been re-derived.** Its dotnet/extensions source links are pinned to the 10.7.0 commit and describe that revision only; nobody has read the 10.8.3/10.9.0 sources to confirm the clone semantics are unchanged. Read that section as historical evidence for the original decision, not as a statement about the running package. The passing tests are the current evidence.
- **The dedicated-client fallback named in Decision 7 is still not implemented.** No dedicated Development chat client type exists in the tree.
