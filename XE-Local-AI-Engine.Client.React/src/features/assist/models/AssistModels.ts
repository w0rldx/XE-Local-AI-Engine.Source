import type {
	XeLocalAiEngineClientEndpointsCommonGenerationMetadata,
	XeLocalAiEngineClientServicesDraftingDraftMode,
} from "@/core/api/generated";

// The provenance block is OPAQUE to the client: the draft response hands it over, the save request echoes it back
// verbatim. Nothing here is read for a decision, so it is aliased rather than remodelled — remodelling it would
// silently drop any field the server adds later.
export type GenerationMetadata = XeLocalAiEngineClientEndpointsCommonGenerationMetadata;

/** Create (brief only) or Improve (brief + the content already in the form). PascalCase matches the wire enum. */
export type AssistMode = XeLocalAiEngineClientServicesDraftingDraftMode;

/** Which surface a draft is for. The two endpoints differ only in what they call the long free-text field. */
export type AssistSurface = "agent" | "skill";

// Normalized draft. `content` is the agent's instructions or the skill's body — the dialog and both parent forms
// only ever handle this one shape, so the surface split stays inside the mutation hook.
export interface AssistDraft {
	name: string;
	description: string;
	content: string;
	generationMetadata: GenerationMetadata;
}

/** What the form already holds, sent as the baseline for an Improve draft. */
export interface AssistExistingContent {
	name: string;
	description: string;
	content: string;
}

/** Matches the endpoint's brief cap; enforced on the textarea so an over-long brief never reaches the 400. */
export const ASSIST_BRIEF_MAX = 4000;
