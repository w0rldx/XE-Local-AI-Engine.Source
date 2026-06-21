import type { ModelOption } from "@/features/chat/models/ChatModels";

// Decides whether the chat page should poll GET models/{name}/details for the current selection.
//
// Details (max context length, template) come from the local runtime. We only fetch when the selection can actually
// yield them:
//   - a non-empty concrete model name is resolved, AND
//   - the selection is not a cloud (Codex) model — cloud ids have no LOCAL details (the endpoint 404s for them), AND
//   - when the selection maps to a known list option, that option is available — an unavailable model can't return
//     details, so polling it just retries a guaranteed failure. When no option is found (e.g. the local-default
//     sentinel resolving to a concrete name) we still fetch: the backend routes the name to its provider and returns
//     200 for an installed Ollama OR GGUF model (the GGUF branch was added in CL-4).
//
// Note: GGUF (llamacpp) selections ARE polled — CL-4 makes those a 200 carrying maxContextTokens, which the context
// usage meter needs. Only cloud and unavailable selections are suppressed.
export function shouldFetchLocalModelDetails(
	concreteModelName: string,
	selectedOption: ModelOption | undefined,
	selectedModelIsCloud: boolean,
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

	return true;
}
