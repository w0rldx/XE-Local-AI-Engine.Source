import type { ModelOption } from "@/features/chat/models/ChatModels";
import { EXTERNAL_PROVIDER } from "@/features/chat/pages/ChatModelOptions";

// Decides whether the chat page should poll GET models/{name}/details for the current selection.
//
// Details (max context length, template) come from the local runtime. We only fetch when the selection can actually
// yield them:
//   - a non-empty concrete model name is resolved, AND
//   - the selection is not a cloud (Codex) model — cloud ids have no LOCAL details (the endpoint 404s for them), AND
//   - when the selection maps to a known list option, that option is available — an unavailable model can't return
//     details, so polling it just retries a guaranteed failure, AND
//   - the resolved concrete name is actually present in the installed-model list.
//
// The installed-list gate is the terminal domain state that fixes the configured-but-not-installed starter model: the
// local-default sentinel resolves to `configuredDefaultModelName`, which may name a model whose GGUF was never
// downloaded. That name is not in the installed list, so GET details 404s — and polling it forever surfaces as a
// stuck, noisy error. A SignalR invalidation on download completion refreshes the installed list, so the moment the
// model IS installed this flips true and the poll begins on its own.
//
// Note: GGUF (llamacpp) selections that ARE installed get polled — the details endpoint answers those with a 200 carrying maxContextTokens,
// which the context usage meter needs. Only cloud, unavailable, and not-installed selections are suppressed.
export function shouldFetchLocalModelDetails(
	concreteModelName: string,
	selectedOption: ModelOption | undefined,
	selectedModelIsCloud: boolean,
	concreteModelInstalled: boolean,
): boolean {
	if (concreteModelName.length === 0) {
		return false;
	}

	if (selectedModelIsCloud) {
		return false;
	}

	// A matched option that is explicitly unavailable can't serve details — don't poll it.
	if (selectedOption !== undefined && selectedOption.isAvailable === false) {
		return false;
	}

	// An external model is served by a remote endpoint, so it is never in the node's installed list — but the details
	// route DOES answer for its `ext:` id, from the operator's own declarations, with the context window the usage
	// meter needs. The installed-list gate below exists for a local default whose GGUF was never downloaded; applying
	// it here is what left every external selection metered against an unknown capacity ("N of —").
	if (selectedOption?.provider === EXTERNAL_PROVIDER) {
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
