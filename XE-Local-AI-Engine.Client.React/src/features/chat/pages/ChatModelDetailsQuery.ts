import type { ModelOption } from "@/features/chat/models/ChatModels";
import { EXTERNAL_PROVIDER } from "@/core/models/LocalModelProviders";

// Decides whether the chat page should poll GET models/{name}/details for the current selection.
//
// Details (max context length, template) come from the local runtime for a local model, and from declarations for a
// remote one (an external connection's declared window, a Codex id's catalogued window, an Azure deployment's
// operator-declared window). We only fetch when the selection can actually yield them:
//   - a non-empty concrete model name is resolved, AND
//   - when the selection maps to a known list option, that option is available — an unavailable model can't return
//     details, so polling it just retries a guaranteed failure, AND
//   - the selection is remote (external or cloud), or the resolved concrete name is present in the installed list.
//
// The installed-list gate is the terminal domain state that fixes the configured-but-not-installed starter model: the
// local-default sentinel resolves to `configuredDefaultModelName`, which may name a model whose GGUF was never
// downloaded. That name is not in the installed list, so GET details 404s — and polling it forever surfaces as a
// stuck, noisy error. A SignalR invalidation on download completion refreshes the installed list, so the moment the
// model IS installed this flips true and the poll begins on its own.
//
// A Codex id with no catalogued window still 404s. That is one request per stale cycle, not a loop: the query
// client never retries a 404 (shouldRetryQuery), the page reads only `data`, so the meter shows unknown and nothing
// is surfaced. Gating those ids here would need a second copy of the backend's window table.
export function shouldFetchLocalModelDetails(
	concreteModelName: string,
	selectedOption: ModelOption | undefined,
	concreteModelInstalled: boolean,
): boolean {
	if (concreteModelName.length === 0) {
		return false;
	}

	// A matched option that is explicitly unavailable can't serve details — don't poll it.
	if (selectedOption !== undefined && selectedOption.isAvailable === false) {
		return false;
	}

	// An external or cloud model is served remotely, so it is never in the node's installed list — but the details
	// route DOES answer for it from declarations, with the context window the usage meter needs. The installed-list
	// gate below exists for a local default whose GGUF was never downloaded; applying it here is what left every
	// remote selection metered against an unknown capacity ("N of —").
	if (selectedOption?.provider === EXTERNAL_PROVIDER || selectedOption?.isCloud === true) {
		return true;
	}

	// A concrete name that is not in the installed list can only 404 — treat it as a terminal state, not a retry loop.
	return concreteModelInstalled;
}

// Resolves the context-usage meter's capacity from a model's details: prefer the RUNNING process's
// effective window (the launched `-c`, reported once the model is warm) over the model's advertised train ceiling, then
// undefined (unknown → the meter shows "—"). Kept pure so the preference is unit-testable without rendering the page.
export function resolveContextCapacityTokens(
	details: { effectiveContextTokens?: number | null; maxContextTokens?: number | null } | undefined,
): number | undefined {
	return details?.effectiveContextTokens ?? details?.maxContextTokens ?? undefined;
}
